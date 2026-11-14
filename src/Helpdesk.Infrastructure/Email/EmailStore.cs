using Dapper;
using Helpdesk.Infrastructure.Data;

namespace Helpdesk.Infrastructure.Email;

public enum InboundOutcome
{
    CreatedTicket,
    AppendedToTicket,
    ReopenedTicket,

    /// <summary>A reply to a closed ticket, which became a new ticket linked to it.</summary>
    CreatedFollowUpTicket,
    SkippedAutomated,
    SkippedOwnMessage,
    Failed,
}

public sealed record MailboxState(string? Cursor, DateTimeOffset IngestFrom);

/// <summary>Remembers how far the mailbox has been read and what was done with each email.</summary>
public sealed class EmailStore(IDbConnectionFactory connectionFactory, TimeProvider timeProvider)
{
    public async Task<MailboxState?> GetStateAsync(string mailbox, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<MailboxState>(new CommandDefinition(
            "SELECT [Cursor], IngestFrom FROM dbo.MailboxState WHERE Mailbox = @mailbox",
            new { mailbox },
            cancellationToken: cancellationToken));
    }

    public async Task SaveStateAsync(string mailbox, MailboxState state, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            MERGE dbo.MailboxState WITH (HOLDLOCK) AS target
            USING (SELECT @mailbox AS Mailbox) AS source ON target.Mailbox = source.Mailbox
            WHEN MATCHED THEN UPDATE SET [Cursor] = @Cursor, UpdatedAt = @now
            WHEN NOT MATCHED THEN INSERT (Mailbox, [Cursor], IngestFrom, UpdatedAt) VALUES (@mailbox, @Cursor, @IngestFrom, @now);
            """,
            new { mailbox, state.Cursor, state.IngestFrom, now = timeProvider.GetUtcNow() },
            cancellationToken: cancellationToken));
    }

    /// <summary>Whether this email has been seen before, as a logged email or as a ticket message.</summary>
    public async Task<bool> IsProcessedAsync(string internetMessageId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.InboundEmails WHERE InternetMessageId = @internetMessageId)
                          OR EXISTS (SELECT 1 FROM dbo.TicketMessages WHERE InternetMessageId = @internetMessageId)
                        THEN 1 ELSE 0 END
            """,
            new { internetMessageId },
            cancellationToken: cancellationToken));
    }

    public async Task LogAsync(
        InboundEmail email, InboundOutcome outcome, int? ticketId, string? error, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO dbo.InboundEmails (InternetMessageId, FromEmail, Subject, ReceivedAt, Outcome, TicketId, Error, ProcessedAt)
            SELECT @InternetMessageId, @FromEmail, @Subject, @ReceivedAt, @Outcome, @ticketId, @Error, @now
            WHERE NOT EXISTS (SELECT 1 FROM dbo.InboundEmails WHERE InternetMessageId = @InternetMessageId)
            """,
            new
            {
                InternetMessageId = Limit(email.InternetMessageId, 450),
                FromEmail = Limit(email.FromEmail, 320),
                Subject = Limit(email.Subject, 500),
                email.ReceivedAt,
                Outcome = outcome.ToString(),
                ticketId,
                Error = error is null ? null : Limit(error, 1000),
                now = timeProvider.GetUtcNow(),
            },
            cancellationToken: cancellationToken));
    }

    internal static string Limit(string value, int length) => value.Length <= length ? value : value[..length];
}
