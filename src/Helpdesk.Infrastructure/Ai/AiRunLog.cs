using Dapper;
using Helpdesk.Infrastructure.Data;

namespace Helpdesk.Infrastructure.Ai;

public enum AiPurpose
{
    Classification,
    Summary,
}

public enum AiRunOutcome
{
    Succeeded,

    /// <summary>The call errored.</summary>
    Failed,

    /// <summary>The model declined.</summary>
    Refused,

    /// <summary>The answer did not pass validation.</summary>
    Invalid,
}

/// <summary>Records every call to the AI provider: what it was for, what it cost in tokens and time, and how it went.</summary>
public sealed class AiRunLog(IDbConnectionFactory connectionFactory, TimeProvider timeProvider)
{
    public async Task WriteAsync(
        int? ticketId,
        AiPurpose purpose,
        string model,
        AiResponse? response,
        TimeSpan latency,
        AiRunOutcome outcome,
        string? error,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO dbo.AiRuns (TicketId, Purpose, Model, InputTokens, OutputTokens, LatencyMs, Outcome, Error, CreatedAt)
            VALUES (@ticketId, @Purpose, @model, @InputTokens, @OutputTokens, @LatencyMs, @Outcome, @Error, @now)
            """,
            new
            {
                ticketId,
                Purpose = purpose.ToString(),
                model,
                response?.InputTokens,
                response?.OutputTokens,
                LatencyMs = (int)Math.Min(latency.TotalMilliseconds, int.MaxValue),
                Outcome = outcome.ToString(),
                Error = error is null ? null : error.Length <= 1000 ? error : error[..1000],
                now = timeProvider.GetUtcNow(),
            },
            cancellationToken: cancellationToken));
    }
}
