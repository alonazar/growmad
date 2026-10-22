using System.Net.Http.Headers;
using System.Text.Json;

namespace NomadInbox.Infrastructure.Knowledge;

public sealed record NomadUpload(string FileName, string FilePath);

/// <summary>A file in NOMAD's knowledge base as listed by <c>GET /api/rag/files</c>.</summary>
public sealed record NomadFile(string Source, string FileName, string State, int Chunks, string? Collection);

public sealed class NomadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The parts of NOMAD's knowledge base API the sync needs; faked in tests.</summary>
public interface INomadKnowledgeBase
{
    Task<bool> IsAvailableAsync(string baseUrl, CancellationToken ct);
    Task<NomadUpload> UploadAsync(string baseUrl, string fileName, string contentType, Stream content, string? collection, CancellationToken ct);
    Task<IReadOnlyList<NomadFile>> ListFilesAsync(string baseUrl, CancellationToken ct);
    Task DeleteAsync(string baseUrl, string source, CancellationToken ct);
}

/// <summary>
/// Client for NOMAD Command Center's knowledge base endpoints (the AI Assistant's RAG store). NOMAD stores uploads
/// under storage/kb_uploads with a random suffix, embeds them into Qdrant in the background, and identifies each file
/// by its full path ("source"). The API has no authentication; it's meant for the local network.
/// </summary>
public sealed class NomadKnowledgeClient(HttpClient http) : INomadKnowledgeBase
{
    public async Task<bool> IsAvailableAsync(string baseUrl, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(Url(baseUrl, "api/rag/health"), ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    public async Task<NomadUpload> UploadAsync(string baseUrl, string fileName, string contentType, Stream content, string? collection, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(content);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        form.Add(file, "file", fileName);
        if (!string.IsNullOrWhiteSpace(collection)) form.Add(new StringContent(collection), "collection");

        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Post, Url(baseUrl, "api/rag/upload")) { Content = form }, ct);
        using var json = await ReadJsonAsync(response, ct);
        var root = json.RootElement;
        return new NomadUpload(
            root.TryGetProperty("fileName", out var n) ? n.GetString() ?? fileName : fileName,
            root.TryGetProperty("filePath", out var p) ? p.GetString() ?? "" : "");
    }

    public async Task<IReadOnlyList<NomadFile>> ListFilesAsync(string baseUrl, CancellationToken ct)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, Url(baseUrl, "api/rag/files")), ct);
        using var json = await ReadJsonAsync(response, ct);
        var files = json.RootElement.ValueKind == JsonValueKind.Array ? json.RootElement
            : json.RootElement.TryGetProperty("files", out var f) ? f : default;
        if (files.ValueKind != JsonValueKind.Array) return [];

        return files.EnumerateArray().Select(x => new NomadFile(
            Str(x, "source") ?? "",
            Str(x, "fileName") ?? "",
            Str(x, "state") ?? "unknown",
            x.TryGetProperty("chunksEmbedded", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0,
            Str(x, "collection"))).Where(x => x.Source.Length > 0).ToList();
    }

    public async Task DeleteAsync(string baseUrl, string source, CancellationToken ct)
    {
        var url = Url(baseUrl, $"api/rag/files?source={Uri.EscapeDataString(source)}");
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Delete, url), ct);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        {
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new NomadException($"Can't reach NOMAD at {request.RequestUri?.GetLeftPart(UriPartial.Authority)}: {ex.Message}", ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new NomadException("NOMAD did not answer in time.", ex);
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                response.Dispose();
                var message = body;
                try
                {
                    using var json = JsonDocument.Parse(body);
                    message = Str(json.RootElement, "error") ?? Str(json.RootElement, "message") ?? body;
                }
                catch (JsonException) { }
                throw new NomadException($"NOMAD: {Shorten(message)} ({(int)response.StatusCode})");
            }
            return response;
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        }
        catch (JsonException ex)
        {
            throw new NomadException("NOMAD answered with something that isn't JSON. Is the address the Command Center?", ex);
        }
    }

    private static Uri Url(string baseUrl, string path) =>
        Uri.TryCreate(baseUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var root) && root.Scheme is "http" or "https"
            ? new Uri(root, path)
            : throw new NomadException($"\"{baseUrl}\" is not a valid NOMAD address (expected e.g. http://host.docker.internal:8080).");

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Shorten(string s) => s.Length > 200 ? s[..200] + "…" : s;
}
