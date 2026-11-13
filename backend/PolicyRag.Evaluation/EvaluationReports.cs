using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PolicyRag.Evaluation;

public static class EvaluationReports
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static void Write(string directory, EvaluationReport report)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(report, Json));
        var csv = new StringBuilder("case,split,outcome,expected,actual,securityGate,durationMs,errorCode\r\n");
        foreach (var result in report.Results)
            csv.AppendLine(string.Join(",", new[] { result.CaseId, result.Split, result.Outcome,
                result.Expected, result.Actual, result.SecurityGatePassed.ToString(),
                result.DurationMs.ToString(CultureInfo.InvariantCulture), result.ErrorCode ?? "" }.Select(Csv)));
        File.WriteAllText(Path.Combine(directory, "results.csv"), csv.ToString());
        var scores = report.Results.Where(r => r.Actual == "answered").SelectMany(r => new[] { "reviewer-a", "reviewer-b" }.Select(alias => new
        {
            caseId = r.CaseId, rater = alias,
            relevance = 0, completeness = 0, evidenceSupport = 0
        }));
        File.WriteAllText(Path.Combine(directory, "scores.template.json"), JsonSerializer.Serialize(scores, Json));
        var html = new StringBuilder("""
            <!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width">
            <title>Policy RAG evaluation evidence</title><style>
            body{font:16px system-ui;margin:2rem;color:#112f3a;max-width:1200px}
            table{border-collapse:collapse;width:100%}td,th{border:1px solid #ccc;padding:.6rem;text-align:left}
            pre{white-space:pre-wrap;overflow-wrap:anywhere}summary{cursor:pointer}h1,h2{color:#006e68}
            </style><h1>Policy RAG evaluation evidence</h1>
            """);
        html.Append($"<h2>{E(report.Decision)}</h2><p>Run: {E(report.RunId)} | {E(report.Mode)} | {E(report.Variant)}</p>");
        html.Append($"<p>Commit: {E(report.Revision)} | Dirty: {report.DirtyWorktree?.ToString() ?? "unknown"}</p>");
        html.Append($"<p>Implementation hash: {E(report.ImplementationHash)} | Runtime settings hash: {E(report.SettingsHash)}</p>");
        if (report.Settings is not null)
            html.Append($"<pre>Runtime settings: {E(JsonSerializer.Serialize(report.Settings, Json))}</pre>");
        html.Append($"<p>Prompt hash: {E(report.PromptHash)}<br>Dataset hash: {E(report.DatasetHash)}<br>Corpus hash: {E(report.CorpusHash)}</p>");
        foreach (var limitation in report.Limitations) html.Append($"<p>{E(limitation)}</p>");
        html.Append("<h2>Outcomes (all selected cases)</h2><ul>");
        foreach (var group in report.Results.GroupBy(r => r.Outcome))
            html.Append($"<li>{E(group.Key)}: {group.Count()}</li>");
        html.Append("</ul><p>Human quality scores are separate from exact-quote checks. Missing scores are not zero scores.</p>");
        var durations = report.Results.Where(r => r.Outcome != "NOT RUN").Select(r => r.DurationMs).Order().ToArray();
        if (durations.Length > 0)
            html.Append($"<p>Observed latency, n={durations.Length}: p50={durations[(int)Math.Ceiling(durations.Length * .5) - 1].ToString("F1", CultureInfo.InvariantCulture)} ms; " +
                $"p95={durations[(int)Math.Ceiling(durations.Length * .95) - 1].ToString("F1", CultureInfo.InvariantCulture)} ms " +
                "(nearest rank; includes denials/blockers; not a production benchmark).</p>");
        foreach (var result in report.Results)
        {
            html.Append($"<details><summary>{E(result.CaseId)} — {E(result.Outcome)} — {result.DurationMs.ToString("F1", CultureInfo.InvariantCulture)} ms</summary>");
            html.Append($"<p>Expected: {E(result.Expected)} | Actual: {E(result.Actual)} | Security gate: {(result.Outcome == "NOT RUN" ? "NOT EVALUATED" : result.SecurityGatePassed ? "PASS" : "FAIL")}</p>");
            html.Append($"<p>Role: {E(result.Role)} | Scope: {E(result.DocumentId ?? "all authorized")} | Question: {E(result.Question)}</p>");
            html.Append($"<pre>{E(result.Answer)}</pre><p>Citations: {E(string.Join(", ", result.CitedSourceIds))}</p>");
            html.Append($"<p>Retrieved: {E(string.Join(", ", result.RetrievedSourceIds))}</p>");
            html.Append($"<p>Error code: {E(result.ErrorCode ?? "none")} | Trace: {E(result.Evidence.TraceId)}</p>");
            foreach (var failure in result.Failures) html.Append($"<p>{E(failure)}</p>");
            html.Append("<table><tr><th>Stage</th><th>Outcome</th><th>ms</th><th>Synthetic</th></tr>");
            foreach (var stage in result.Evidence.Stages)
                html.Append($"<tr><td>{E(stage.Name)}</td><td>{E(stage.Outcome)}</td><td>{stage.DurationMs.ToString(CultureInfo.InvariantCulture)}</td><td>{stage.Synthetic}</td></tr>");
            html.Append("</table><p>Usage (embedding / input / output): " +
                $"{result.Evidence.Usage.EmbeddingTokens?.ToString() ?? "unknown"} / " +
                $"{result.Evidence.Usage.InputTokens?.ToString() ?? "unknown"} / " +
                $"{result.Evidence.Usage.OutputTokens?.ToString() ?? "unknown"}</p>");
            foreach (var score in report.HumanScores.Where(s => s.CaseId == result.CaseId))
                html.Append($"<p>Rater {E(score.Rater)}: relevance {score.Relevance}, completeness {score.Completeness}, evidence support {score.EvidenceSupport}</p>");
            html.Append("</details><hr>");
        }
        File.WriteAllText(Path.Combine(directory, "report.html"), html.ToString());
    }

    private static string E(string value) => WebUtility.HtmlEncode(value);
    private static string Csv(string value) => "\"" +
        (value.StartsWith('=') || value.StartsWith('+') || value.StartsWith('-') || value.StartsWith('@') ? "'" : "") +
        value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
