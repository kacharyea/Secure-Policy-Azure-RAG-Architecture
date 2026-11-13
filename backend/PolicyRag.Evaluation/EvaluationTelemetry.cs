using System.Diagnostics;
using PolicyRag.Application;

namespace PolicyRag.Evaluation;

public static class EvaluationTelemetry
{
    public static void Record(EvaluationReport report)
    {
        using var activity = WorkflowTracing.Source.StartActivity("policy.evaluation_run");
        activity?.SetTag("rag.mode", report.Mode);
        activity?.SetTag("rag.synthetic", report.Mode == "Fixture");
        activity?.SetTag("rag.prompt_variant", report.Variant);
        activity?.SetTag("rag.experiment_id", report.RunId);
        activity?.SetTag("rag.evaluation.kind", "deterministic");
        activity?.SetTag("rag.evaluation.decision", report.Decision);
        activity?.SetTag("rag.evaluation.total", report.Results.Length);
        activity?.SetTag("rag.evaluation.pass", report.Results.Count(r => r.Outcome == "PASS"));
        activity?.SetTag("rag.evaluation.fail", report.Results.Count(r => r.Outcome == "FAIL"));
        activity?.SetTag("rag.evaluation.blocked", report.Results.Count(r => r.Outcome == "BLOCKED"));
        activity?.SetTag("rag.evaluation.not_run", report.Results.Count(r => r.Outcome == "NOT RUN"));
        activity?.SetTag("rag.evaluation.security_failures", report.Results.Count(r => !r.SecurityGatePassed));
        activity?.SetStatus(ActivityStatusCode.Ok);
    }
}
