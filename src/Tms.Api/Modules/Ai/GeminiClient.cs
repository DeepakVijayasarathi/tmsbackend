using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tms.Api.Common;

namespace Tms.Api.Modules.Ai;

/// <summary>
/// Google Gemini over its REST API (generateContent). The key comes from GEMINI_API_KEY or Gemini:ApiKey.
/// Tries the primary model, then the fallback, while failures are transient (overloaded, rate-limited,
/// unreachable) — the same approach the Meet to Manage backend uses.
/// </summary>
public class GeminiClient(IConfiguration config, IHttpClientFactory http, ILogger<GeminiClient> log) : ILlmClient
{
    private string? ApiKey => config["Gemini:ApiKey"] is { Length: > 0 } k ? k : Environment.GetEnvironmentVariable("GEMINI_API_KEY");
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    public async Task<ClaudeReply> CompleteAsync(string system, IReadOnlyList<ChatTurn> turns,
        IReadOnlyDictionary<string, JsonElement>? jsonSchema, CancellationToken ct)
    {
        if (!IsConfigured) throw new AppException(StatusCodes.Status503ServiceUnavailable, "The AI assistant is not configured on this server. Set GEMINI_API_KEY.");

        var generation = new JsonObject { ["temperature"] = 0.4, ["maxOutputTokens"] = config.GetValue("Gemini:MaxOutputTokens", 8192) };
        if (jsonSchema is not null)
        {
            generation["responseMimeType"] = "application/json";
            generation["responseJsonSchema"] = JsonSerializer.SerializeToNode(jsonSchema);
        }
        var contents = new JsonArray();
        foreach (var t in turns)
            contents.Add(new JsonObject
            {
                ["role"] = t.Role == "assistant" ? "model" : "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = t.Content }),
            });
        var payload = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system }) },
            ["contents"] = contents,
            ["generationConfig"] = generation,
        }.ToJsonString();

        var primary = Environment.GetEnvironmentVariable("GEMINI_MODEL") is { Length: > 0 } envModel ? envModel : config["Gemini:Model"] ?? "gemini-flash-latest";
        var models = new[] { primary, config["Gemini:FallbackModel"] ?? "gemini-flash-lite-latest" }.Distinct();
        var lastStatus = 0;
        foreach (var model in models)
        {
            var (status, body) = await SendAsync(model, payload, ct);
            lastStatus = status;
            if (status == 200) return Parse(body, model);
            log.LogWarning("Gemini {Model} failed: {Status} {Body}", model, status, body.Length > 500 ? body[..500] : body);
            if (status is not (0 or 429 or 500 or 503 or 504)) break;
        }

        throw lastStatus switch
        {
            400 or 401 or 403 => new AppException(StatusCodes.Status503ServiceUnavailable, "The AI assistant rejected the server's Gemini key. Check GEMINI_API_KEY."),
            429 => new AppException(StatusCodes.Status429TooManyRequests, "The AI assistant is busy. Try again in a moment."),
            _ => new AppException(StatusCodes.Status502BadGateway, "The AI assistant could not answer right now."),
        };
    }

    private ClaudeReply Parse(string body, string model)
    {
        var root = JsonNode.Parse(body);
        var candidate = root?["candidates"]?[0];
        var text = string.Concat((candidate?["content"]?["parts"] as JsonArray ?? [])
            .Where(p => p?["thought"]?.GetValue<bool>() != true)
            .Select(p => p?["text"]?.GetValue<string>() ?? ""));
        var finish = candidate?["finishReason"]?.GetValue<string>();
        var blocked = root?["promptFeedback"]?["blockReason"] is not null || finish is "SAFETY" or "PROHIBITED_CONTENT" or "BLOCKLIST" or "RECITATION";
        var usage = root?["usageMetadata"];
        if (string.IsNullOrEmpty(text) && !blocked) log.LogWarning("Gemini {Model} returned no text: {Body}", model, body);
        return new ClaudeReply(text, blocked, usage?["promptTokenCount"]?.GetValue<int>() ?? 0, usage?["candidatesTokenCount"]?.GetValue<int>() ?? 0);
    }

    private async Task<(int Status, string Body)> SendAsync(string model, string payload, CancellationToken ct)
    {
        try
        {
            var client = http.CreateClient(nameof(GeminiClient));
            client.Timeout = TimeSpan.FromSeconds(60);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent");
            req.Headers.Add("x-goog-api-key", ApiKey);
            req.Content = new StringContent(payload, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
            using var res = await client.SendAsync(req, ct);
            return ((int)res.StatusCode, await res.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Gemini {Model} unreachable", model);
            return (0, ex.Message);
        }
    }
}
