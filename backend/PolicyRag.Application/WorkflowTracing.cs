using System.Diagnostics;

namespace PolicyRag.Application;

public static class WorkflowTracing
{
    public const string SourceName = "PolicyRag.Workflow";
    public static readonly ActivitySource Source = new(SourceName, "1.0.0");

    public static StageScope StartWorkflow(RunMetrics metrics)
    {
        var activity = Source.StartActivity("policy.workflow", ActivityKind.Server)
            ?? new Activity("policy.workflow").SetIdFormat(ActivityIdFormat.W3C).Start();
        metrics.TraceId = activity.TraceId.ToString();
        activity.SetTag("rag.mode", metrics.Mode);
        activity.SetTag("rag.prompt_variant", metrics.PromptVariant);
        activity.SetTag("rag.correlation_id", metrics.CorrelationId);
        if (metrics.ExperimentId.Length > 0) activity.SetTag("rag.experiment_id", metrics.ExperimentId);
        return new(metrics, "workflow", activity, false);
    }

    public static StageScope Stage(RunMetrics metrics, string name) =>
        new(metrics, name, Source.StartActivity("policy." + name),
            metrics.Mode == "Fixture" && name is "embedding" or "retrieval" or "generation");

    public sealed class StageScope : IDisposable
    {
        private readonly RunMetrics metrics;
        private readonly string name;
        private readonly Activity? activity;
        private readonly bool synthetic;
        private readonly long start = Stopwatch.GetTimestamp();
        private string outcome = "Failed";

        internal StageScope(RunMetrics metrics, string name, Activity? activity, bool synthetic)
        {
            this.metrics = metrics;
            this.name = name;
            this.activity = activity;
            this.synthetic = synthetic;
            activity?.SetTag("rag.mode", metrics.Mode);
            activity?.SetTag("rag.stage", name);
            activity?.SetTag("rag.synthetic", synthetic);
        }

        public void Complete() => outcome = "Completed";

        public void Dispose()
        {
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            metrics.Stages.Add(new(name, Math.Round(elapsed, 3), outcome, synthetic));
            activity?.SetTag("rag.outcome", outcome);
            activity?.SetTag("rag.duration_ms", elapsed);
            if (name == "embedding" && metrics.EmbeddingTokens is { } tokens)
                activity?.SetTag("gen_ai.usage.input_tokens", tokens);
            if (name == "generation")
            {
                if (metrics.InputTokens is { } input) activity?.SetTag("gen_ai.usage.input_tokens", input);
                if (metrics.OutputTokens is { } output) activity?.SetTag("gen_ai.usage.output_tokens", output);
            }
            if (name == "workflow")
            {
                activity?.SetTag("rag.authorization", metrics.Authorization);
                activity?.SetTag("rag.validation", metrics.Validation);
                activity?.SetTag("rag.answer_status", metrics.AnswerStatus);
                activity?.SetTag("rag.calls.embedding", metrics.EmbeddingCalls);
                activity?.SetTag("rag.calls.generation", metrics.GenerationCalls);
                activity?.SetTag("rag.calls.search", metrics.SearchCalls);
            }
            activity?.SetStatus(outcome == "Completed" ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
            activity?.Dispose();
        }
    }
}
