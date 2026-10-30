using Microsoft.AspNetCore.Mvc;
using PolicyRag.Application;
using PolicyRag.Application.Services;

namespace PolicyRag.Api.Controllers;

[ApiController]
[Route("api/ask")]
[ServiceFilter(typeof(CallerContextFilter))]
public sealed class QuestionsController(
    RagOrchestrationService orchestration, ILogger<QuestionsController> logger) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<DraftView>> Ask(AskRequest request)
    {
        var metrics = (RunMetrics)HttpContext.Items["metrics"]!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var draft = await orchestration.AskAsync(
            (Caller)HttpContext.Items["caller"]!, request, metrics, timeout.Token);
        logger.LogInformation(
            "Policy workflow: {Mode}, {Authorization}, {Validation}, calls {Embedding}/{Generation}, correlation {CorrelationId}",
            metrics.Mode, metrics.Authorization, metrics.Validation,
            metrics.EmbeddingCalls, metrics.GenerationCalls, metrics.CorrelationId);
        return Ok(DraftView.From(draft));
    }
}
