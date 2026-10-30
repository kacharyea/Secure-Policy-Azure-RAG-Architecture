using Microsoft.AspNetCore.Mvc;
using PolicyRag.Application;
using PolicyRag.Application.Services;

namespace PolicyRag.Api.Controllers;

[ApiController]
[Route("api/documents")]
[ServiceFilter(typeof(CallerContextFilter))]
public sealed class DocumentsController(DocumentAuthorizationService authorization) : ControllerBase
{
    [HttpGet]
    public IEnumerable<DocumentSummary> List() =>
        authorization.Authorized((Caller)HttpContext.Items["caller"]!)
            .Select(p => new DocumentSummary(p.Id, p.Title, p.Version));

    [HttpGet("{id}")]
    public PolicyDocument Get(string id) =>
        authorization.RequireDocument((Caller)HttpContext.Items["caller"]!, id);
}
