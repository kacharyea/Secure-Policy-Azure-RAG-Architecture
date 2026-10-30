using System.Xml.Linq;

namespace SecurePolicyRag.Tests;

public sealed class GatewayPolicyTests
{
    private static XElement Policy => XElement.Load(Path.Combine(AppContext.BaseDirectory, "gateway-active-policy.xml"));

    [Fact]
    public void Gateway_requires_signed_application_role_token_for_its_own_tenant_and_audience()
    {
        var jwt = Policy.Element("inbound")!.Element("validate-jwt")!;
        Assert.Equal("true", (string?)jwt.Attribute("require-signed-tokens"));
        Assert.Equal("true", (string?)jwt.Attribute("require-expiration-time"));
        Assert.Equal("401", (string?)jwt.Attribute("failed-validation-httpcode"));
        Assert.Equal("__GATEWAY_AUDIENCE__", jwt.Element("audiences")!.Element("audience")!.Value);
        Assert.Equal("https://login.microsoftonline.com/__TENANT_ID__/v2.0",
            jwt.Element("issuers")!.Element("issuer")!.Value);
        var claim = Assert.Single(jwt.Element("required-claims")!.Elements("claim"));
        Assert.Equal("roles", (string?)claim.Attribute("name"));
        Assert.Equal("ModelGateway.Invoke", claim.Element("value")!.Value);
    }

    [Fact]
    public void Gateway_uses_its_own_identity_for_models_and_limits_routing_to_approved_operations()
    {
        var inbound = Policy.Element("inbound")!;
        var identity = inbound.Element("authentication-managed-identity")!;
        Assert.Equal("https://cognitiveservices.azure.com", (string?)identity.Attribute("resource"));
        Assert.Equal("false", (string?)identity.Attribute("ignore-error"));
        var routes = inbound.Element("choose")!;
        Assert.Equal(2, routes.Elements("when").Count());
        Assert.Equal("404", (string?)routes.Element("otherwise")!.Element("return-response")!
            .Element("set-status")!.Attribute("code"));
        Assert.Contains(inbound.Elements("set-header"), header =>
            (string?)header.Attribute("name") == "api-key" &&
            (string?)header.Attribute("exists-action") == "delete");
    }
}
