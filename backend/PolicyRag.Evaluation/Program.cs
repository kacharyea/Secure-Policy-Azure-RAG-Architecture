using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PolicyRag.Application;
using PolicyRag.Application.Interfaces;
using PolicyRag.Application.Services;
using PolicyRag.Evaluation;
using PolicyRag.Infrastructure;
using PolicyRag.Infrastructure.AI;
using PolicyRag.Infrastructure.Configuration;
using PolicyRag.Infrastructure.Storage;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    if (args is ["--help"])
    {
        Console.WriteLine("""
            PolicyRag.Evaluation (synthetic corpus and trusted test roles only; not browser/Entra testing)
              --mode Fixture|Azure       default Fixture; Azure also requires --allow-live
              --variant baseline|candidate
              --split development|holdout|all   default development
              --case <case-id>           optional single case
              --repeat 1..5              default 1
              --max-calls 1..500         default 120; pessimistic 3 downstream calls per question
              --config <ignored-local-json>    explicit runtime settings for Azure (or environment)
              --scores <json-file>       optional human scores for these exact results; prefer rescore
              --rescore <report.json> --scores <scores.json>   no model calls
              --out <new-directory>      default artifacts/evaluations/<run-id>
            Exit: 0 controls/gates passed, 2 failed/no-go, 3 blocked/incomplete/pending human review.
            No index writes, role changes, agent changes or fixture fallback.
            """);
        return 0;
    }
    try
    {
        var parsed = Parse(args);
        var mode = Get(parsed, "--mode", "Fixture");
        var variant = Get(parsed, "--variant", "baseline");
        var split = Get(parsed, "--split", "development");
        if (mode is not ("Fixture" or "Azure") || split is not ("development" or "holdout" or "all"))
            throw new EvaluationInputException("Invalid mode or split. See --help.");
        if (variant is not ("baseline" or "candidate"))
            throw new EvaluationInputException("Prompt variant must be baseline or candidate.");
        _ = PolicyPrompts.Get(variant);
        var repeat = Number(parsed, "--repeat", 1, 1, 5);
        var maxCalls = Number(parsed, "--max-calls", 120, 1, 500);
        var runId = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N")[..8];
        var output = Path.GetFullPath(Get(parsed, "--out", Path.Combine("artifacts", "evaluations", runId)));
        if (Directory.Exists(output)) throw new EvaluationInputException("Output directory already exists; choose a new path.");
        if (parsed.TryGetValue("--rescore", out var previous))
        {
            if (!parsed.TryGetValue("--scores", out var scoresPath))
                throw new EvaluationInputException("--rescore requires --scores.");
            var report = Read<EvaluationReport>(previous);
            if (report.SchemaVersion != "1" || report.Results is not { Length: > 0 and <= 500 } ||
                report.Results.Any(r => r is null || r.Evidence is null || r.Evidence.Usage is null ||
                    r.Evidence.Stages is null || r.Failures is null || r.CitedSourceIds is null ||
                    r.RetrievedSourceIds is null || r.Answer is null) ||
                report.Limitations is null || report.HumanScores is null ||
                report.Mode is not ("Fixture" or "Azure") ||
                report.Variant is not ("baseline" or "candidate"))
                throw new EvaluationInputException("Unsupported or incomplete saved report.");
            var scores = Read<HumanScore[]>(scoresPath);
            ValidateScores(scores, report.Results);
            var decision = EvaluationScoring.Decision(report.Mode, report.Results, scores,
                report.Revision != "unknown" && report.DirtyWorktree.HasValue);
            EvaluationReports.Write(output, report with { HumanScores = scores, Decision = decision });
            Console.WriteLine($"{decision}. Rescored existing run {report.RunId}; no model calls. Output: {output}");
            return ExitCode(decision);
        }
        if (parsed.ContainsKey("--scores"))
            throw new EvaluationInputException("Use --rescore to attach scores to actual saved results, not future answers.");
        if (mode == "Azure" && !parsed.ContainsKey("--allow-live"))
            throw new EvaluationInputException("Azure mode requires --allow-live and approved synthetic test access.");

        var datasetPath = Path.Combine(AppContext.BaseDirectory, "cases.json");
        var corpusPath = Path.Combine(AppContext.BaseDirectory, "Data", "policies.json");
        var all = Read<EvaluationCase[]>(datasetPath);
        if (all.Length == 0 || all.Any(c => c is null || string.IsNullOrWhiteSpace(c.Id)) ||
            all.Select(c => c.Id).Distinct().Count() != all.Length ||
            all.Any(c => c.RequiredSourceIds is null || c.RequiredFacts is null ||
                c.Role is not ("employee" or "reviewer") ||
                c.Expected is not ("answered" or "denied" or "insufficient_evidence") ||
                c.Split is not ("development" or "holdout") || string.IsNullOrWhiteSpace(c.Question)))
            throw new EvaluationInputException("Invalid or duplicate evaluation cases.");
        var cases = all.Where(c => split == "all" || c.Split == split)
            .Where(c => !parsed.TryGetValue("--case", out var id) || c.Id == id).ToArray();
        if (cases.Length == 0) throw new EvaluationInputException("No cases selected.");
        if (mode == "Azure" && cases.Length * repeat * 3 > maxCalls)
            throw new EvaluationInputException("Selected run exceeds --max-calls pessimistic downstream-call budget.");

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = [], ContentRootPath = AppContext.BaseDirectory, EnvironmentName = Environments.Development
        });
        if (parsed.TryGetValue("--config", out var config))
            builder.Configuration.AddJsonFile(Path.GetFullPath(config), optional: false);
        // Environment overrides explicit local settings; never read the API's user secrets implicitly.
        builder.Configuration.AddEnvironmentVariables();
        var azure = builder.Configuration.GetSection("Rag:Azure").Get<AzureOptions>() ?? new();
        var options = new RagOptions { Mode = mode, PromptVariant = variant };
        StartupValidation.Validate(options, true, azure);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddPolicyRagInfrastructure(options, azure, true);
        builder.Services.AddWorkflowTelemetry(builder.Configuration);
        builder.Services.AddSingleton(JsonPolicyManifest.Load(corpusPath));
        builder.Services.AddTransient<RagOrchestrationService>();
        builder.Services.AddSingleton<AnswerValidationService>();
        using var host = builder.Build();
        await host.StartAsync();
        var catalog = host.Services.GetRequiredService<DocumentAuthorizationService>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var results = await EvaluationExecution.RunAsync(cases, repeat, mode, variant, runId,
            catalog, host.Services.GetRequiredService<RagOrchestrationService>(), timeout.Token);
        var (revision, dirty) = GitState();
        var array = results.ToArray();
        var decisionText = EvaluationScoring.Decision(mode, array, [], revision != "unknown" && dirty.HasValue);
        var limitations = new List<string>
        {
            "Synthetic corpus only. Direct service runner uses trusted test roles; does not test Entra login or HTTP middleware.",
            "Required-fact/source checks are deterministic proxies, not semantic quality scores. Human scores are separate.",
            "Usage missing from the provider remains null (unknown); no dollar cost or time-to-first-token is inferred.",
            "Security gates apply only to these evaluated cases; this is not production certification."
        };
        if (mode == "Fixture")
            limitations.Add("Fixture generator is scripted and ignores prompt variants. This run cannot prove live model quality or injection resistance.");
        var reportResult = new EvaluationReport("1", runId, mode, variant, PolicyPrompts.Hash(variant),
            Hash(datasetPath), Hash(corpusPath), revision, dirty,
            mode == "Fixture" ? "scripted-extractive-fixture" : azure.AnswerModel,
            DateTimeOffset.UtcNow, limitations.ToArray(), array, [], decisionText);
        var settings = mode == "Fixture"
            ? new EvaluationSettings("synthetic-vector", azure.EmbeddingDimensions, "scripted-extractive-fixture",
                "local", "local-manifest", "local", "local-keyword", 5, 0, repeat, maxCalls)
            : new EvaluationSettings(azure.EmbeddingModel, azure.EmbeddingDimensions, azure.AnswerModel,
                azure.GenerationProtocol, azure.SearchIndex, azure.SearchApiVersion, "hybrid-prefilter", 5, 20, repeat, maxCalls);
        reportResult = reportResult with
        {
            Settings = settings,
            SettingsHash = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(settings, EvaluationReports.Json)))),
            ImplementationHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|",
                Hash(typeof(RagOrchestrationService).Assembly.Location),
                Hash(typeof(ApimModelClient).Assembly.Location),
                Hash(typeof(EvaluationScoring).Assembly.Location)))))
        };
        EvaluationReports.Write(output, reportResult);
        EvaluationTelemetry.Record(reportResult);
        await host.StopAsync();
        Console.WriteLine($"{decisionText}. Report: {output}");
        return ExitCode(decisionText);
    }
    catch (EvaluationInputException error)
    {
        Console.Error.WriteLine(error.Message);
        return 1;
    }
    catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException or
        JsonException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"Evaluation setup/report failed ({error.GetType().Name}). " +
            "Check arguments, configuration and writable output path; no fixture fallback. See --help.");
        return 1;
    }
}

static Dictionary<string, string> Parse(string[] args)
{
    string[] valued = ["--mode", "--variant", "--split", "--case", "--repeat", "--max-calls",
        "--config", "--scores", "--rescore", "--out"];
    var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i < args.Length; i++)
    {
        var key = args[i];
        if (parsed.ContainsKey(key)) throw new EvaluationInputException("Duplicate argument. See --help.");
        if (key == "--allow-live") { parsed.Add(key, "true"); continue; }
        if (!valued.Contains(key) || i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            throw new EvaluationInputException("Unknown argument or missing value. See --help.");
        parsed.Add(key, args[++i]);
    }
    return parsed;
}

static string Get(Dictionary<string, string> args, string name, string fallback) => args.GetValueOrDefault(name, fallback);
static int Number(Dictionary<string, string> args, string name, int fallback, int min, int max)
{
    if (!args.TryGetValue(name, out var value)) return fallback;
    if (!int.TryParse(value, out var number) || number < min || number > max)
        throw new EvaluationInputException($"Invalid {name}.");
    return number;
}
static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), EvaluationReports.Json)
    ?? throw new JsonException("Missing JSON value.");
static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
static int ExitCode(string decision) => decision == "NO-GO" ? 2 :
    decision is "INCOMPLETE" or "PENDING HUMAN REVIEW" ? 3 : 0;
static void ValidateScores(HumanScore[] scores, CaseResult[] results)
{
    if (scores.Any(s => s is null || string.IsNullOrWhiteSpace(s.Rater) || s.Relevance is < 1 or > 5 ||
        s.Completeness is < 1 or > 5 || s.EvidenceSupport is < 1 or > 5 ||
        !results.Any(r => r.CaseId == s.CaseId && r.Actual == "answered")) ||
        scores.Select(s => (s.CaseId, s.Rater)).Distinct().Count() != scores.Length)
        throw new EvaluationInputException("Scores require unique case/rater pairs and 1-5 scores for actual answered cases.");
}

static (string Revision, bool? Dirty) GitState()
{
    try
    {
        string Command(string arguments)
        {
            using var process = Process.Start(new ProcessStartInfo("git", arguments)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("Cannot start Git.");
            var text = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException("Git metadata unavailable.");
            return text.Trim();
        }
        return (Command("rev-parse HEAD"), Command("status --porcelain").Length != 0);
    }
    catch (Exception error) when (error is Win32Exception or InvalidOperationException)
    {
        Console.Error.WriteLine("Git metadata unavailable. Put git on PATH and run from the repository; handoff decision is INCOMPLETE.");
        return ("unknown", null);
    }
}

internal sealed class EvaluationInputException(string message) : Exception(message);
