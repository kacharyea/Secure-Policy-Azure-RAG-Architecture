using System.Diagnostics;
using System.Text.Json;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry.Trace;
using PolicyRag.Evaluation;
using PolicyRag.Infrastructure;

namespace SecurePolicyRag.Tests;

public sealed class EvaluationTests
{
    private static readonly Caller Employee = new("test-user-not-for-telemetry", "Test", ["employees"]);
    private static DocumentAuthorizationService Catalog() => new([
        new("public", "Policy", "v1", ["employees"],
            [new("public-1", "Employees must use multifactor authentication for remote access.")]),
        new("restricted", "Private", "v1", ["security-reviewers"],
            [new("restricted-1", "Privileged access review evidence must name the reviewer and date.")])
    ]);

    private static RagOrchestrationService Workflow(DocumentAuthorizationService catalog, IEmbeddingService embedding,
        IPolicySearch search, IAnswerGenerator generator) =>
        new(catalog, embedding, search, generator, new(), new ReviewStore(new(), TimeProvider.System), TimeProvider.System);

    [Fact]
    public async Task Fixture_stages_have_one_trace_and_no_sensitive_tags()
    {
        var finished = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == WorkflowTracing.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (finished) finished.Add(activity); }
        };
        ActivitySource.AddActivityListener(listener);
        var catalog = Catalog();
        var fixture = new FixtureProvider(catalog);
        var metrics = new RunMetrics("safe-correlation", "Fixture");
        var draft = await Workflow(catalog, fixture, fixture, fixture).AskAsync(
            Employee, new("remote access", "public"), metrics, default);
        Assert.Equal(32, draft.Evidence.TraceId.Length);
        Assert.Equal(["authorization", "embedding", "retrieval", "source_checks", "generation", "validation", "workflow"],
            metrics.Stages.Select(s => s.Name));
        Assert.All(metrics.Stages, s => Assert.Equal("Completed", s.Outcome));
        Assert.True(metrics.Stages.Single(s => s.Name == "retrieval").Synthetic);
        var activities = finished.Where(a => a.TraceId.ToString() == metrics.TraceId).ToArray();
        Assert.Equal(7, activities.Length);
        Assert.All(activities.Where(a => a.OperationName != "policy.workflow"),
            a => Assert.NotEqual(default, a.ParentSpanId));
        var root = Assert.Single(activities, a => a.OperationName == "policy.workflow");
        Assert.Equal("answered", root.GetTagItem("rag.answer_status"));
        Assert.Equal("workflow", root.GetTagItem("rag.stage"));
        Assert.Equal(0, root.GetTagItem("rag.calls.generation"));
        var serializedTags = JsonSerializer.Serialize(activities.Select(a => a.TagObjects.ToArray()));
        Assert.DoesNotContain(Employee.Id, serializedTags);
        Assert.DoesNotContain("remote access", serializedTags);
        Assert.DoesNotContain("multifactor authentication", serializedTags);
        Assert.Null(draft.Evidence.Usage.InputTokens);
        Assert.Equal(0, metrics.GenerationCalls);
    }

    [Fact]
    public async Task Denied_request_stops_trace_at_authorization()
    {
        var catalog = Catalog();
        var fixture = new FixtureProvider(catalog);
        var metrics = new RunMetrics("denial", "Fixture");
        await Assert.ThrowsAsync<WorkflowException>(() => Workflow(catalog, fixture, fixture, fixture)
            .AskAsync(Employee, new("privileged review", "restricted"), metrics, default));
        Assert.Equal(["authorization", "workflow"], metrics.Stages.Select(s => s.Name));
        Assert.All(metrics.Stages, s => Assert.Equal("Failed", s.Outcome));
        Assert.Equal(0, metrics.SearchCalls + metrics.EmbeddingCalls + metrics.GenerationCalls);
    }

    [Fact]
    public async Task Provider_failure_identifies_failed_stage_without_fixture_fallback()
    {
        var catalog = Catalog();
        var provider = new WorkflowTests.RecordingProvider { FailEmbedding = true };
        var metrics = new RunMetrics("upstream", "Azure");
        await Assert.ThrowsAsync<WorkflowException>(() => Workflow(catalog, provider, provider, provider)
            .AskAsync(Employee, new("remote access"), metrics, default));
        Assert.Equal("Failed", metrics.Stages.Single(s => s.Name == "embedding").Outcome);
        Assert.DoesNotContain(metrics.Stages, s => s.Name == "generation" || s.Name == "retrieval");
    }

    [Fact]
    public async Task No_evidence_trace_skips_generation()
    {
        var catalog = Catalog();
        var fixture = new FixtureProvider(catalog);
        var metrics = new RunMetrics("empty", "Fixture");
        var draft = await Workflow(catalog, fixture, fixture, fixture)
            .AskAsync(Employee, new("lunar shuttle fare"), metrics, default);
        Assert.Equal("insufficient_evidence", draft.Status);
        Assert.DoesNotContain(metrics.Stages, s => s.Name == "generation");
    }

    [Fact]
    public async Task Correct_quote_can_still_fail_evidence_sufficiency_evaluation()
    {
        var catalog = Catalog();
        var fixture = new FixtureProvider(catalog);
        var metrics = new RunMetrics("sufficiency", "Fixture");
        var test = new EvaluationCase("proof", "development", "employee",
            "Does remote access prove a review was completed?", "public", "insufficient_evidence", [], []);
        var draft = await Workflow(catalog, fixture, fixture, fixture)
            .AskAsync(Employee, new(test.Question, test.DocumentId), metrics, default);
        var result = EvaluationScoring.Score(test, draft, metrics, draft.Status, null, 1,
            new HashSet<string> { "public-1" });
        Assert.Equal("AuthorizedExactQuotes", metrics.Validation);
        Assert.True(result.SecurityGatePassed);
        Assert.Equal("FAIL", result.Outcome);
        Assert.Equal("NO-GO", EvaluationScoring.Decision("Fixture", [result], [], true));
    }

    [Fact]
    public void Blocked_and_skipped_cases_cannot_pass_release()
    {
        var test = new EvaluationCase("blocked", "development", "reviewer", "question", null, "answered", [], []);
        var result = EvaluationScoring.Score(test, null, new("blocked", "Azure"), "blocked", "upstream_rejected", 1,
            new HashSet<string>());
        Assert.Equal("BLOCKED", result.Outcome);
        Assert.Equal("INCOMPLETE", EvaluationScoring.Decision("Azure", [result], [], true));
        Assert.Equal("INCOMPLETE", EvaluationScoring.Decision("Azure",
            [result with { Outcome = "NOT RUN", Actual = "not_run" }], [], true));
    }

    [Fact]
    public async Task Console_prints_each_executed_question_before_its_result()
    {
        var first = new EvaluationCase("remote", "development", "employee", "remote access", "public", "answered", [], []);
        var second = first with { Id = "missing", Question = "lunar shuttle fare", DocumentId = null, Expected = "insufficient_evidence" };
        var catalog = Catalog();
        var fixture = new FixtureProvider(catalog);
        using var output = new StringWriter();
        var results = await EvaluationExecution.RunAsync([first, second], 1, "Fixture", "baseline", "console-test",
            catalog, Workflow(catalog, fixture, fixture, fixture), default, output);
        var text = output.ToString();
        Assert.All(results, result => Assert.Equal("PASS", result.Outcome));
        Assert.Contains("Question [remote]: remote access", text);
        Assert.Contains("Question [missing]: lunar shuttle fare", text);
        Assert.True(text.IndexOf("Question [remote]", StringComparison.Ordinal) <
            text.IndexOf("remote: PASS", StringComparison.Ordinal));
        Assert.True(text.IndexOf("Question [missing]", StringComparison.Ordinal) <
            text.IndexOf("missing: PASS", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Blocker_preserves_every_remaining_repetition_in_the_denominator()
    {
        var first = new EvaluationCase("first", "development", "employee", "remote access", "public", "answered", [], []);
        var provider = new WorkflowTests.RecordingProvider { FailEmbedding = true };
        var catalog = Catalog();
        using var output = new StringWriter();
        var results = await EvaluationExecution.RunAsync([first, first with { Id = "second" }],
            3, "Azure", "baseline", "bounded-run", catalog, Workflow(catalog, provider, provider, provider), default, output);
        Assert.Equal(6, results.Length);
        Assert.Equal("BLOCKED", results[0].Outcome);
        Assert.Equal(["first-r1", "first-r2", "first-r3", "second-r1", "second-r2", "second-r3"],
            results.Select(r => r.CaseId));
        Assert.All(results.Skip(1), r => Assert.Equal("NOT RUN", r.Outcome));
        Assert.Equal(1, provider.TotalCalls);
        Assert.Equal("INCOMPLETE", EvaluationScoring.Decision("Azure", results, [], true));
        Assert.Contains("Question [first-r1]: remote access", output.ToString());
        Assert.Contains("first-r1: BLOCKED", output.ToString());
        Assert.DoesNotContain("Question [first-r2]", output.ToString());
        Assert.DoesNotContain("Question [second-r1]", output.ToString());
    }

    [Fact]
    public void Live_pass_requires_two_independent_raters_and_no_low_score()
    {
        var test = new EvaluationCase("answer", "development", "employee", "question", null, "answered", [], []);
        var result = EvaluationScoring.Score(test, null, new("valid", "Azure"), "answered", null, 1, new HashSet<string>());
        var first = new HumanScore("answer", "reviewer-a", 4, 4, 4);
        var second = first with { Rater = "reviewer-b" };
        Assert.Equal("PENDING HUMAN REVIEW", EvaluationScoring.Decision("Azure", [result], [first], true));
        Assert.Equal("POC GATES PASSED - NOT PRODUCTION APPROVAL",
            EvaluationScoring.Decision("Azure", [result], [first, second], true));
        Assert.Equal("NO-GO", EvaluationScoring.Decision("Azure", [result], [first with { Completeness = 3 }], true));
    }

    [Fact]
    public void Unauthorized_source_ids_and_invalid_output_fail_security_gate()
    {
        var test = new EvaluationCase("unsafe", "development", "employee", "question", null, "answered", [], []);
        var metrics = new RunMetrics("unsafe", "Azure") { RetrievedSourceIds = ["restricted-1"] };
        var result = EvaluationScoring.Score(test, null, metrics, "invalid_output", "source_integrity_failed", 1,
            new HashSet<string> { "public-1" });
        Assert.False(result.SecurityGatePassed);
        Assert.Equal("FAIL", result.Outcome);
    }

    [Fact]
    public void Reports_encode_untrusted_text_and_do_not_turn_missing_usage_into_zero()
    {
        var directory = Path.Combine(Path.GetTempPath(), "policy-rag-report-" + Guid.NewGuid().ToString("N"));
        try
        {
            var test = new EvaluationCase("=formula", "development", "employee", "<script>unsafe()</script>",
                null, "answered", [], []);
            var result = EvaluationScoring.Score(test, null, new("safe", "Fixture"), "answered", null, 1,
                new HashSet<string>());
            var report = new EvaluationReport("1", "test", "Fixture", "baseline", "prompt", "dataset", "corpus",
                "revision", true, "fixture", DateTimeOffset.UtcNow, [], [result], [], "CONTROLS ONLY");
            EvaluationReports.Write(directory, report);
            var html = File.ReadAllText(Path.Combine(directory, "report.html"));
            Assert.DoesNotContain("<script>unsafe()", html);
            Assert.Contains("&lt;script&gt;unsafe()", html);
            Assert.Contains("unknown / unknown / unknown", html);
            Assert.Contains("\"'=formula\"", File.ReadAllText(Path.Combine(directory, "results.csv")));
            var saved = JsonSerializer.Deserialize<EvaluationReport>(
                File.ReadAllText(Path.Combine(directory, "report.json")), EvaluationReports.Json);
            Assert.Equal(result.Question, saved!.Results[0].Question);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Denial_with_provider_calls_fails_hard_gate()
    {
        var test = new EvaluationCase("deny", "development", "employee", "question", "restricted", "denied", [], []);
        var metrics = new RunMetrics("denied", "Azure") { Authorization = "Denied", SearchCalls = 1 };
        var result = EvaluationScoring.Score(test, null, metrics, "denied", "document_access_denied", 1,
            new HashSet<string>());
        Assert.False(result.SecurityGatePassed);
        Assert.Equal("NO-GO", EvaluationScoring.Decision("Azure", [result], [], true));
    }

    [Fact]
    public void Prompt_variants_preserve_contract_and_are_differently_versioned()
    {
        Assert.NotEqual(PolicyPrompts.Hash("baseline"), PolicyPrompts.Hash("candidate"));
        Assert.StartsWith(PolicyPrompts.Get("baseline"), PolicyPrompts.Get("candidate"));
        Assert.Contains("insufficient_evidence", PolicyPrompts.Get("candidate"));
        Assert.Contains("Never invent a citation", PolicyPrompts.Get("candidate"));
        Assert.Throws<InvalidOperationException>(() => PolicyPrompts.Get("unrecognized"));
        Assert.Throws<InvalidOperationException>(() => StartupValidation.Validate(
            new() { PromptVariant = "unrecognized" }, true));
    }

    [Fact]
    public void Evaluation_telemetry_exports_counts_not_questions_or_answers()
    {
        Activity? summary = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == WorkflowTracing.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "policy.evaluation_run") summary = activity;
            }
        };
        ActivitySource.AddActivityListener(listener);
        var test = new EvaluationCase("test", "development", "employee", "private-test-question", null,
            "insufficient_evidence", [], []);
        var result = EvaluationScoring.Score(test, null, new("test", "Fixture"), "answered", null, 1, new HashSet<string>());
        var report = new EvaluationReport("1", "safe-experiment-id", "Fixture", "baseline", "prompt", "data",
            "corpus", "revision", true, "fixture", DateTimeOffset.UtcNow, [], [result], [], "NO-GO");
        EvaluationTelemetry.Record(report);
        Assert.NotNull(summary);
        Assert.Equal("deterministic", summary.GetTagItem("rag.evaluation.kind"));
        Assert.Equal(1, summary.GetTagItem("rag.evaluation.total"));
        Assert.Equal(1, summary.GetTagItem("rag.evaluation.fail"));
        Assert.Equal(0, summary.GetTagItem("rag.evaluation.blocked"));
        Assert.Equal(ActivityStatusCode.Ok, summary.Status);
        Assert.DoesNotContain("private-test-question", JsonSerializer.Serialize(summary.TagObjects));
    }

    [Fact]
    public void Unknown_telemetry_credential_is_rejected_explicitly()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telemetry:Enabled"] = "true", ["Telemetry:Credential"] = "unrecognized"
        }).Build();
        var error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddWorkflowTelemetry(config));
        Assert.Contains("Telemetry:Credential", error.Message);
    }

    [Fact]
    public void Telemetry_is_explicit_opt_in_and_requires_configuration()
    {
        new ServiceCollection().AddWorkflowTelemetry(new ConfigurationBuilder().Build());
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telemetry:Enabled"] = "true"
        }).Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddWorkflowTelemetry(config));
    }

    [Fact]
    public void Demo_telemetry_disables_rate_sampling_and_records_all_workflow_traces()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telemetry:Enabled"] = "true",
            ["Telemetry:Credential"] = "AzureCliCredential",
            ["APPLICATIONINSIGHTS_CONNECTION_STRING"] =
                "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://localhost/"
        }).Build();
        using var services = new ServiceCollection().AddWorkflowTelemetry(config).BuildServiceProvider();
        _ = services.GetRequiredService<TracerProvider>();
        var exporter = services.GetRequiredService<IOptionsMonitor<AzureMonitorExporterOptions>>().CurrentValue;
        Assert.Null(exporter.TracesPerSecond);
        Assert.Equal(1.0F, exporter.SamplingRatio);
        Assert.True(exporter.DisableOfflineStorage);
        Assert.False(exporter.EnableLiveMetrics);
    }

    [Theory]
    [InlineData("Azure", "PENDING HUMAN REVIEW")]
    [InlineData("Fixture", "CONTROLS ONLY - NOT A LIVE RELEASE")]
    public void Missing_quality_scores_are_not_a_live_pass(string mode, string expected)
    {
        var test = new EvaluationCase("answer", "development", "employee", "question", null, "answered", [], []);
        var metrics = new RunMetrics("valid", mode);
        var result = EvaluationScoring.Score(test, null, metrics, "answered", null, 1, new HashSet<string>());
        Assert.Equal(expected, EvaluationScoring.Decision(mode, [result], [], true));
    }
}
