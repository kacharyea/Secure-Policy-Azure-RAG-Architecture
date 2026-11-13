using System.Diagnostics;
using Azure.Identity;
using PolicyRag.Application;
using PolicyRag.Application.Services;

namespace PolicyRag.Evaluation;

public static class EvaluationExecution
{
    public static async Task<CaseResult[]> RunAsync(EvaluationCase[] cases, int repeat,
        string mode, string variant, string runId, DocumentAuthorizationService catalog,
        RagOrchestrationService workflow, CancellationToken cancellationToken, TextWriter? output = null)
    {
        var console = output ?? Console.Out;
        var planned = cases.SelectMany(test => Enumerable.Range(1, repeat)
            .Select(iteration => repeat == 1 ? test : test with { Id = $"{test.Id}-r{iteration}" })).ToArray();
        var results = new List<CaseResult>();
        var stopped = false;
        foreach (var test in planned)
        {
            if (stopped)
            {
                results.Add(new(test.Id, test.Split, test.Role, test.Question, test.DocumentId,
                    test.Expected, "not_run", "NOT RUN", ["Stopped after dependency blocker; controls were not evaluated."],
                    "", [], [], 0, new RunMetrics("not-run", mode) { PromptVariant = variant }.Snapshot(), null, true));
                continue;
            }
            console.WriteLine($"Question [{test.Id}]: {test.Question}");
            var caller = test.Role == "employee"
                ? new Caller("evaluation-employee", "Trusted synthetic test role", ["employees"])
                : new Caller("evaluation-reviewer", "Trusted synthetic test role", ["employees", "security-reviewers"]);
            var metrics = new RunMetrics(Guid.NewGuid().ToString("N"), mode)
            {
                PromptVariant = variant, ExperimentId = runId
            };
            Draft? draft = null;
            string actual;
            string? code = null;
            var start = Stopwatch.GetTimestamp();
            try
            {
                using var perCase = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                perCase.CancelAfter(TimeSpan.FromSeconds(60));
                draft = await workflow.AskAsync(caller, new(test.Question, test.DocumentId), metrics, perCase.Token);
                actual = draft.Status;
            }
            catch (WorkflowException error)
            {
                code = error.Code;
                actual = error.Code is "document_access_denied" or "no_entitlements" ? "denied" :
                    error.Code is "output_validation_failed" or "invalid_generation_response" or
                        "source_integrity_failed" or "retrieval_scope_failed" ? "invalid_output" : "blocked";
            }
            catch (Exception error) when (error is HttpRequestException or AuthenticationFailedException or
                CredentialUnavailableException or OperationCanceledException)
            {
                code = error.GetType().Name;
                actual = "blocked";
            }
            var allowed = catalog.Authorized(caller).SelectMany(d => d.Chunks)
                .Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            var result = EvaluationScoring.Score(test, draft, metrics, actual, code,
                Math.Round(Stopwatch.GetElapsedTime(start).TotalMilliseconds, 3), allowed);
            results.Add(result);
            console.WriteLine($"{test.Id}: {result.Outcome}; expected={test.Expected}, actual={actual}; code={code ?? "none"}");
            console.WriteLine();
            stopped = actual == "blocked";
        }
        return results.ToArray();
    }
}
