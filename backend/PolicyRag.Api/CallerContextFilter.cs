using Microsoft.AspNetCore.Mvc.Filters;
using PolicyRag.Application;

namespace PolicyRag.Api;

public sealed class CallerContextFilter(RagOptions options, IConfiguration configuration) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        context.HttpContext.Items["caller"] = CallerResolver.Resolve(context.HttpContext, options, configuration);
        await next();
    }
}
