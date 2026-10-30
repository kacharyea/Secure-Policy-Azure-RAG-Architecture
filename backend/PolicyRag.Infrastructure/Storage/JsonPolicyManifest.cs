using System.Text.Json;
using PolicyRag.Application;
using PolicyRag.Application.Services;

namespace PolicyRag.Infrastructure.Storage;

public static class JsonPolicyManifest
{
    public static DocumentAuthorizationService Load(string path)
    {
        var policies = JsonSerializer.Deserialize<PolicyDocument[]>(
            File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("The policy manifest is empty.");
        return new DocumentAuthorizationService(policies);
    }
}
