using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NomadInbox.Infrastructure.Ai;

public sealed record OllamaModel(string Name, long Size, string? ParameterSize);

public sealed class OllamaException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The parts of Ollama the pipeline needs; faked in tests.</summary>
public interface IOllamaApi
{
    Task<string> VersionAsync(string baseUrl, CancellationToken ct);
    Task<string> ChatAsync(string baseUrl, string model, string system, string user, int contextTokens, CancellationToken ct);
}

/// <summary>Minimal client for the Ollama HTTP API (/api/version, /api/tags, /api/chat).</summary>
public sealed class OllamaClient(HttpClient http) : IOllamaApi
{
    public async Task<string> VersionAsync(string baseUrl, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, Url(baseUrl, "api/version"), null, TimeSpan.FromSeconds(5), ct);
        return json?["version"]?.GetValue<string>() ?? "unknown";
    }

    public async Task<IReadOnlyList<OllamaModel>> ListModelsAsync(string baseUrl, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, Url(baseUrl, "api/tags"), null, TimeSpan.FromSeconds(10), ct);
        return json?["models"]?.AsArray()
                   .Select(m => new OllamaModel(
                       m?["name"]?.GetValue<string>() ?? "",
                       m?["size"]?.GetValue<long>() ?? 0,
                       m?["details"]?["parameter_size"]?.GetValue<string>()))
                   .Where(m => m.Name.Length > 0)
                   .OrderBy(m => m.Name)
                   .ToList()
               ?? [];
    }

    public async Task<string> ChatAsync(string baseUrl, string model, string system, string user, int contextTokens, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["stream"] = false,
            ["think"] = false, // reasoning models (qwen3, …) answer directly; others ignore it
            ["keep_alive"] = "10m",
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user }),
            ["options"] = new JsonObject { ["num_ctx"] = contextTokens, ["temperature"] = 0.2 },
        };

        JsonNode? json;
        try
        {
            json = await SendAsync(HttpMethod.Post, Url(baseUrl, "api/chat"), body, TimeSpan.FromMinutes(10), ct);
        }
        catch (OllamaException ex) when (ex.Message.Contains("think", StringComparison.OrdinalIgnoreCase))
        {
            // Some Ollama versions reject the "think" option for models without a thinking mode.
            body.Remove("think");
            json = await SendAsync(HttpMethod.Post, Url(baseUrl, "api/chat"), body, TimeSpan.FromMinutes(10), ct);
        }

        var content = json?["message"]?["content"]?.GetValue<string>() ?? "";
        return StripThinking(content).Trim();
    }

    /// <summary>Removes &lt;think&gt;…&lt;/think&gt; blocks some models emit even when asked not to.</summary>
    public static string StripThinking(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"<think>.*?</think>", "",
            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private async Task<JsonNode?> SendAsync(HttpMethod method, Uri url, JsonNode? body, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cts.Token);
        }
        catch (HttpRequestException ex)
        {
            throw new OllamaException($"Can't reach Ollama at {url.GetLeftPart(UriPartial.Authority)}: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new OllamaException($"Ollama did not answer within {timeout.TotalMinutes:0.#} min.", ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            JsonNode? json = null;
            try { json = JsonNode.Parse(text); } catch (JsonException) { }

            if (!response.IsSuccessStatusCode)
            {
                var error = json?["error"]?.GetValue<string>() ?? text;
                throw new OllamaException($"Ollama: {error} ({(int)response.StatusCode})");
            }
            return json;
        }
    }

    private static Uri Url(string baseUrl, string path) =>
        Uri.TryCreate(baseUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var root) && root.Scheme is "http" or "https"
            ? new Uri(root, path)
            : throw new OllamaException($"\"{baseUrl}\" is not a valid Ollama address (expected e.g. http://host.docker.internal:11434).");

    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
}
