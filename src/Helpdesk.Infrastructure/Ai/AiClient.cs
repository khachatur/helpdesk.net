using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Helpdesk.Core.Tickets;
using Microsoft.Extensions.Options;

namespace Helpdesk.Infrastructure.Ai;

public enum AiMode
{
    /// <summary>No AI. Tickets are routed without classification and get no summary.</summary>
    None,

    /// <summary>Claude through the Anthropic API.</summary>
    Anthropic,
}

/// <summary>The "Ai" configuration section.</summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    public AiMode Mode { get; set; } = AiMode.None;

    /// <summary>A secret. Set it with user secrets or an environment variable, never in a settings file.</summary>
    public string ApiKey { get; set; } = "";

    public string ClassificationModel { get; set; } = "claude-haiku-5-5";

    public string SummaryModel { get; set; } = "claude-haiku-5-5";

    public string DraftModel { get; set; } = "claude-sonnet-5-5";

    /// <summary>Whether AI replies can go out without an agent reading them. Off unless set.</summary>
    public AutoSendMode AutoSendMode { get; set; } = AutoSendMode.Off;

    /// <summary>The AI must be at least this sure of a reply for it to be sent without review.</summary>
    public double AutoSendConfidenceThreshold { get; set; } = 0.85;

    /// <summary>A draft the AI is less sure of than this marks the ticket as needing human review.</summary>
    public double EscalationConfidenceThreshold { get; set; } = 0.5;

    /// <summary>
    /// A classification the AI is less sure of than this is not applied: the ticket stays
    /// unclassified and goes to the default team for an agent to categorise.
    /// </summary>
    public double ConfidenceThreshold { get; set; } = 0.7;

    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Retries inside one call, for rate limits, server errors and network failures.</summary>
    public int MaxRetries { get; set; } = 2;

    public bool IsEnabled => Mode != AiMode.None;
}

/// <param name="JsonSchema">When set, the reply is JSON that matches this schema.</param>
public sealed record AiRequest(
    string Model,
    string System,
    string UserContent,
    int MaxTokens,
    IReadOnlyDictionary<string, JsonElement>? JsonSchema = null,
    // Sorting and summarising are quick, well-defined tasks. Writing a reply deserves more care.
    bool Careful = false);

public sealed record AiResponse(string Text, int InputTokens, int OutputTokens, bool Refused = false, bool Truncated = false);

/// <summary>A failed AI call.</summary>
/// <param name="isRetryable">
/// True for rate limits, server errors and network failures, which may succeed later.
/// False for a rejected request (bad key, bad request), which will fail the same way again.
/// </param>
public sealed class AiException(string message, bool isRetryable, Exception? inner = null) : Exception(message, inner)
{
    public bool IsRetryable { get; } = isRetryable;
}

/// <summary>The AI provider. The only part of the system that talks to it.</summary>
public interface IAiClient
{
    /// <exception cref="AiException">The call failed.</exception>
    Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken);
}

public sealed class AnthropicAiClient : IAiClient
{
    private readonly AnthropicClient _client;

    public AnthropicAiClient(IOptions<AiOptions> options)
    {
        var settings = options.Value;
        _client = new AnthropicClient
        {
            ApiKey = settings.ApiKey,
            Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds),
            MaxRetries = settings.MaxRetries,
        };
    }

    public async Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken)
    {
        var effort = request.Careful ? Effort.Medium : Effort.Low;
        var parameters = new MessageCreateParams
        {
            Model = request.Model,
            MaxTokens = request.MaxTokens,
            System = request.System,
            Messages = [new UserMessageParam { Content = request.UserContent }],
            OutputConfig = request.JsonSchema is null
                ? new OutputConfig { Effort = effort }
                : new OutputConfig { Effort = effort, Format = new JsonOutputFormat { Schema = request.JsonSchema } },
        };

        Message message;
        try
        {
            message = await _client.Messages.Create(parameters, cancellationToken: cancellationToken);
        }
        catch (AnthropicRateLimitException ex)
        {
            throw new AiException("The AI provider is rate limiting requests.", isRetryable: true, ex);
        }
        catch (Anthropic5xxException ex)
        {
            throw new AiException("The AI provider had a server error.", isRetryable: true, ex);
        }
        catch (AnthropicIOException ex)
        {
            throw new AiException("The AI provider could not be reached.", isRetryable: true, ex);
        }
        catch (Anthropic4xxException ex)
        {
            // A bad key, an unknown model, or a malformed request. Trying again cannot help.
            throw new AiException("The AI provider rejected the request: " + ex.Message, isRetryable: false, ex);
        }
        catch (AnthropicApiException ex)
        {
            throw new AiException("The AI call failed: " + ex.Message, isRetryable: true, ex);
        }

        var text = string.Concat(message.Content.Select(block => block.Value).OfType<TextBlock>().Select(block => block.Text));
        return new AiResponse(
            text,
            (int)message.Usage.InputTokens,
            (int)message.Usage.OutputTokens,
            Refused: message.StopReason == "refusal",
            Truncated: message.StopReason == "max_tokens");
    }
}

/// <summary>Stands in when AI is off. Nothing should call it; if something does, it says why it failed.</summary>
public sealed class NoAiClient : IAiClient
{
    public Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken) =>
        throw new AiException("AI is not configured (Ai:Mode is None).", isRetryable: false);
}
