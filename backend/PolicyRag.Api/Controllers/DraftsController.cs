using Microsoft.AspNetCore.Mvc;
using PolicyRag.Application;
using PolicyRag.Application.Interfaces;
using PolicyRag.Application.Services;

namespace PolicyRag.Api.Controllers;

[ApiController]
[Route("api/drafts")]
[ServiceFilter(typeof(CallerContextFilter))]
public sealed class DraftsController(IReviewStore store, DocumentAuthorizationService authorization) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public DraftView Get(Guid id) =>
        DraftView.From(store.Get(id, (Caller)HttpContext.Items["caller"]!, authorization));

    [HttpPost("{id:guid}/review")]
    public DraftView Review(Guid id, ReviewRequest request) =>
        DraftView.From(store.Review(id, (Caller)HttpContext.Items["caller"]!, authorization, request.Decision));
}
