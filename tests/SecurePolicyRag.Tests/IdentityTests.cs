using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using PolicyRag.Api;

namespace SecurePolicyRag.Tests;

public sealed class IdentityTests
{
    [Fact]
    public void Azure_ignores_demo_identity_and_requires_validated_identity()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Demo-Profile"] = "sam";
        var error = Assert.Throws<WorkflowException>(() =>
            CallerResolver.Resolve(context, new() { Mode = "Azure" }, new ConfigurationBuilder().Build()));
        Assert.Equal(401, error.StatusCode);
    }

    [Fact]
    public void Azure_uses_only_trusted_group_mappings_and_required_scope()
    {
        var group = Guid.NewGuid().ToString();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"Rag:Azure:GroupMappings:{group}:0"] = "employees"
        }).Build();
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new("oid", "trusted-user"), new("scp", "PolicyReview.Invoke"),
                 new("groups", group), new("groups", "security-reviewers")], "ValidatedTestIdentity"))
        };
        context.Request.Headers["X-Demo-Profile"] = "sam";
        context.Request.Headers["X-User-Groups"] = "security-reviewers";
        var caller = CallerResolver.Resolve(context, new() { Mode = "Azure" }, configuration);
        Assert.Equal("trusted-user", caller.Id);
        Assert.Equal(["employees"], caller.Groups);
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new("oid", "trusted-user"), new("groups", group)], "ValidatedTestIdentity"));
        Assert.Equal("scope_required", Assert.Throws<WorkflowException>(() =>
            CallerResolver.Resolve(context, new() { Mode = "Azure" }, configuration)).Code);
    }

    [Fact]
    public void Group_overage_without_resolver_fails_closed()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new("oid", "trusted-user"), new("scp", "PolicyReview.Invoke"), new("hasgroups", "true")],
                "ValidatedTestIdentity"))
        };
        Assert.Equal("no_entitlements", Assert.Throws<WorkflowException>(() =>
            CallerResolver.Resolve(context, new() { Mode = "Azure" }, new ConfigurationBuilder().Build())).Code);
    }
}
