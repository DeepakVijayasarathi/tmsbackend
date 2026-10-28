using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using Tms.Api.Common;

namespace Tms.Api.Modules.Ai;

public record ClaudeReply(string Text, bool Refused, int InputTokens, int OutputTokens);
public record ChatTurn(string Role, string Content);

public interface ILlmClient
{
    Task<ClaudeReply> CompleteAsync(string system, IReadOnlyList<ChatTurn> turns, IReadOnlyDictionary<string, JsonElement>? jsonSchema, CancellationToken ct);
    /// <summary>False when no credentials are set, so callers can fall back instead of failing.</summary>
    bool IsConfigured => true;
}

/// <summary>
/// Thin wrapper over the Anthropic SDK. Uses the server-side refusal fallback so a policy decline
/// on the primary model is retried on a fallback model inside the same call.
/// Credentials come from the environment (ANTHROPIC_API_KEY or an `ant auth login` profile).
/// </summary>
public class ClaudeClient(IConfiguration config, ILogger<ClaudeClient> log) : ILlmClient
{
    private readonly Lazy<AnthropicClient> _client = new(() => new AnthropicClient());
    private string Model => config["Ai:Model"] ?? "claude-opus-5-5";

    public async Task<ClaudeReply> CompleteAsync(string system, IReadOnlyList<ChatTurn> turns,
        IReadOnlyDictionary<string, JsonElement>? jsonSchema, CancellationToken ct)
    {
        var parameters = new MessageCreateParams
        {
            Model = Model,
            MaxTokens = 16000,
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(), // "default": the API picks the fallback model by refusal category
            System = system,
            OutputConfig = jsonSchema is null
                ? new BetaOutputConfig { Effort = Effort.Medium }
                : new BetaOutputConfig { Effort = Effort.Medium, Format = new BetaJsonOutputFormat { Schema = jsonSchema } },
            Messages = turns.Select(t => new BetaMessageParam
            {
                Role = t.Role == "assistant" ? Role.Assistant : Role.User,
                Content = t.Content,
            }).ToList(),
        };

        try
        {
            var response = await _client.Value.Beta.Messages.Create(parameters, ct);
            var text = string.Concat(response.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
            var refused = response.StopReason == BetaStopReason.Refusal;
            return new ClaudeReply(text, refused, (int)response.Usage.InputTokens, (int)response.Usage.OutputTokens);
        }
        catch (AnthropicUnauthorizedException ex)
        {
            log.LogError(ex, "Claude rejected the credentials — set ANTHROPIC_API_KEY");
            throw new AppException(StatusCodes.Status503ServiceUnavailable, "The AI assistant is not configured on this server.");
        }
        catch (AnthropicRateLimitException ex)
        {
            log.LogWarning(ex, "Claude rate limited");
            throw new AppException(StatusCodes.Status429TooManyRequests, "The AI assistant is busy. Try again in a moment.");
        }
        catch (AnthropicApiException ex)
        {
            log.LogError(ex, "Claude API error");
            throw new AppException(StatusCodes.Status502BadGateway, "The AI assistant could not answer right now.");
        }
        catch (Exception ex) when (ex is not AppException and not OperationCanceledException)
        {
            log.LogError(ex, "Claude call failed (is ANTHROPIC_API_KEY configured?)");
            throw new AppException(StatusCodes.Status503ServiceUnavailable, "The AI assistant is not configured on this server.");
        }
    }
}
