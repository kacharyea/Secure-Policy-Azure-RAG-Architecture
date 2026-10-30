using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Azure.Core;
using PolicyRag.Application;
using PolicyRag.Application.Services;
using PolicyRag.Infrastructure.Configuration;

namespace PolicyRag.Infrastructure.Storage;

public sealed class BlobPolicySources(HttpClient http, TokenCredential credential, AzureOptions options)
{
    public const int MaximumSourceBytes = 256 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly AzureHttpTransport transport = new(http, credential);

    public async Task<(int Uploaded, int Unchanged)> UploadAsync(
        DocumentAuthorizationService catalog, string sourceDirectory, CancellationToken cancellationToken)
    {
        var sources = new List<(PolicyDocument Policy, byte[] Bytes)>();
        foreach (var policy in catalog.ApprovedDocuments)
        {
            await using var file = File.OpenRead(Path.Combine(sourceDirectory, policy.Id + ".txt"));
            var bytes = await ReadBoundedAsync(file, cancellationToken);
            sources.Add((policy, ValidateContent(policy, bytes)));
        }

        var uploaded = 0;
        var unchanged = 0;
        foreach (var (policy, bytes) in sources)
        {
            using var request = Request(HttpMethod.Put, policy);
            request.Headers.Add("x-ms-blob-type", "BlockBlob");
            request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new("text/plain") { CharSet = "utf-8" };
            await transport.AuthenticateAsync(request, "https://storage.azure.com/.default", cancellationToken);
            using var response = await transport.SendAsync(request, cancellationToken, HttpStatusCode.PreconditionFailed);
            if (response.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                await ReadAsync(policy, cancellationToken);
                unchanged++;
            }
            else uploaded++;
        }
        return (uploaded, unchanged);
    }

    public async Task<DocumentAuthorizationService> ReadApprovedAsync(
        DocumentAuthorizationService catalog, CancellationToken cancellationToken)
    {
        var documents = new List<PolicyDocument>();
        foreach (var policy in catalog.ApprovedDocuments)
        {
            var bytes = await ReadAsync(policy, cancellationToken);
            var text = StrictUtf8.GetString(bytes);
            var offset = 0;
            var chunks = new List<PolicyChunk>();
            foreach (var chunk in policy.Chunks)
            {
                var canonicalChunk = Canonicalize(chunk.Text);
                var sourceChunk = text.Substring(offset, canonicalChunk.Length);
                if (sourceChunk != chunk.Text)
                    throw new WorkflowException(422, "source_integrity_failed",
                        "Blob indexing requires approved manifest chunks with canonical LF line endings.");
                chunks.Add(chunk with { Text = sourceChunk });
                offset += canonicalChunk.Length + 2;
            }
            documents.Add(policy with { Chunks = chunks.ToArray() });
        }
        return new(documents);
    }

    private async Task<byte[]> ReadAsync(PolicyDocument policy, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, policy);
        await transport.AuthenticateAsync(request, "https://storage.azure.com/.default", cancellationToken);
        using var response = await transport.SendAsync(request, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return ValidateContent(policy, await ReadBoundedAsync(stream, cancellationToken));
    }

    private HttpRequestMessage Request(HttpMethod method, PolicyDocument policy)
    {
        var expected = StrictUtf8.GetBytes(Canonicalize(string.Join("\n\n", policy.Chunks.Select(c => c.Text)) + "\n"));
        var hash = Convert.ToHexStringLower(SHA256.HashData(expected));
        var uri = $"{options.BlobEndpoint.TrimEnd('/')}/{options.BlobContainer}/{policy.Id}/{hash}.txt";
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("x-ms-version", "2023-11-03");
        request.Headers.Add("x-ms-date", DateTimeOffset.UtcNow.ToString("R", CultureInfo.InvariantCulture));
        return request;
    }

    private static byte[] ValidateContent(PolicyDocument policy, byte[] bytes)
    {
        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException)
        {
            throw new WorkflowException(422, "invalid_source_encoding", "Approved source files must be valid UTF-8 text.");
        }
        if (text.StartsWith('\uFEFF')) text = text[1..];
        var canonical = StrictUtf8.GetBytes(Canonicalize(text));
        var expected = StrictUtf8.GetBytes(Canonicalize(string.Join("\n\n", policy.Chunks.Select(c => c.Text)) + "\n"));
        if (!SHA256.HashData(canonical).AsSpan().SequenceEqual(SHA256.HashData(expected)))
            throw new WorkflowException(422, "source_integrity_failed",
                "A source file does not match the approved manifest. No permissions or approval were taken from Blob metadata.");
        return canonical;
    }

    private static string Canonicalize(string text) => text.ReplaceLineEndings("\n");

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaximumSourceBytes + 1];
        var count = await stream.ReadAtLeastAsync(buffer, buffer.Length, false, cancellationToken);
        if (count > MaximumSourceBytes)
            throw new WorkflowException(413, "source_too_large", "A source file exceeds the 256 KiB demo limit.");
        return buffer.AsSpan(0, count).ToArray();
    }
}
