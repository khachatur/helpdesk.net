using System.Data.Common;
using Dapper;
using Helpdesk.Core.Tickets;

namespace Helpdesk.Infrastructure.Tickets;

/// <summary>A reply the AI drafted, with the articles it was written from.</summary>
public sealed class TicketDraft
{
    public long Id { get; init; }
    public int TicketId { get; init; }
    public string Body { get; init; } = "";
    public decimal Confidence { get; init; }
    public DraftStatus Status { get; init; }
    public AutoSendDecision GateDecision { get; init; }

    /// <summary>Why the reply was not sent automatically, one reason per line.</summary>
    public string? GateReasons { get; init; }

    public int BasedOnMessageCount { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public IReadOnlyList<DraftSource> Sources { get; set; } = [];
}

public sealed class DraftSource
{
    public long DraftId { get; init; }
    public int ArticleId { get; init; }
    public string Title { get; init; } = "";
    public bool IsPublished { get; init; }
    public bool AutoSendAllowed { get; init; }
}

/// <summary>AI-drafted replies: storing them, what agents do with them, and sending them unreviewed.</summary>
public sealed partial class TicketStore
{
    /// <summary>
    /// Stores a new draft as the ticket's suggested reply. Any earlier draft still waiting for an
    /// agent is superseded, so a ticket has at most one pending draft. Returns the new draft's id.
    /// </summary>
    public async Task<long> SaveDraftAsync(
        int ticketId,
        string body,
        double confidence,
        IReadOnlyCollection<int> sourceArticleIds,
        int basedOnMessageCount,
        AutoSendResult gate,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();

        await SupersedePendingDraftsAsync(connection, transaction, ticketId, now, cancellationToken);

        var draftId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO dbo.TicketDrafts (TicketId, Body, Confidence, Status, GateDecision, GateReasons, BasedOnMessageCount, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@ticketId, @body, @confidence, N'Pending', @decision, @reasons, @basedOnMessageCount, @now)
            """,
            new
            {
                ticketId,
                body,
                confidence = Math.Round((decimal)confidence, 3),
                decision = gate.Decision.ToString(),
                reasons = gate.Reasons.Count == 0 ? null : string.Join('\n', gate.Reasons),
                basedOnMessageCount,
                now,
            },
            transaction,
            cancellationToken: cancellationToken));

        foreach (var articleId in sourceArticleIds.Distinct())
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO dbo.TicketDraftSources (DraftId, ArticleId) VALUES (@draftId, @articleId)",
                new { draftId, articleId },
                transaction,
                cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        return draftId;
    }

    /// <summary>The draft waiting for an agent on this ticket, if there is one.</summary>
    public async Task<TicketDraft?> GetPendingDraftAsync(int ticketId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1) Id, TicketId, Body, Confidence, Status, GateDecision, GateReasons, BasedOnMessageCount, CreatedAt
            FROM dbo.TicketDrafts
            WHERE TicketId = @ticketId AND Status = N'Pending'
            ORDER BY Id DESC;

            SELECT s.DraftId, s.ArticleId, a.Title, a.IsPublished, a.AutoSendAllowed
            FROM dbo.TicketDraftSources s
            JOIN dbo.Articles a ON a.Id = s.ArticleId
            WHERE s.DraftId = (SELECT TOP (1) Id FROM dbo.TicketDrafts WHERE TicketId = @ticketId AND Status = N'Pending' ORDER BY Id DESC)
            ORDER BY a.Title;
            """;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var results = await connection.QueryMultipleAsync(
            new CommandDefinition(sql, new { ticketId }, cancellationToken: cancellationToken));

        var draft = await results.ReadSingleOrDefaultAsync<TicketDraft>();
        if (draft is not null)
        {
            draft.Sources = (await results.ReadAsync<DraftSource>()).AsList();
        }

        return draft;
    }

    /// <summary>An agent rejects the suggested reply. False if it is not this ticket's pending draft.</summary>
    public async Task<bool> DiscardDraftAsync(int ticketId, long draftId, Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        var rows = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.TicketDrafts
            SET Status = N'Discarded', ResolvedAt = @now, ResolvedByUserId = @userId
            WHERE Id = @draftId AND TicketId = @ticketId AND Status = N'Pending'
            """,
            new { ticketId, draftId, userId, now = timeProvider.GetUtcNow() },
            cancellationToken: cancellationToken));
        return rows == 1;
    }

    /// <summary>
    /// Sends a draft without review: adds it to the conversation as an AI reply waiting to be
    /// emailed, and resolves the ticket. Nothing is sent, and null is returned, if the ticket
    /// changed since the draft was written - a new message arrived, or it is no longer open.
    /// The caller has already run the auto-send check; this guards against the time in between.
    /// </summary>
    public async Task<long?> AutoSendDraftAsync(int ticketId, long draftId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var current = await LockAsync(connection, transaction, ticketId, cancellationToken);
        var draft = await connection.QuerySingleOrDefaultAsync<TicketDraft>(new CommandDefinition(
            "SELECT Id, Body, Status, BasedOnMessageCount FROM dbo.TicketDrafts WHERE Id = @draftId AND TicketId = @ticketId",
            new { draftId, ticketId },
            transaction,
            cancellationToken: cancellationToken));
        var state = await connection.QuerySingleAsync<(int MessageCount, bool AutoSendDisabled)>(new CommandDefinition(
            """
            SELECT (SELECT COUNT(*) FROM dbo.TicketMessages WHERE TicketId = @ticketId),
                   (SELECT AutoSendDisabled FROM dbo.Tickets WHERE Id = @ticketId)
            """,
            new { ticketId },
            transaction,
            cancellationToken: cancellationToken));

        if (current is null || draft is null
            || current.Status != TicketStatus.Open
            || draft.Status != DraftStatus.Pending
            || state.AutoSendDisabled
            || state.MessageCount != draft.BasedOnMessageCount)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var messageId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO dbo.TicketMessages (TicketId, Direction, BodyText, CreatedAt, SendStatus, IsAiGenerated)
            OUTPUT INSERTED.Id
            VALUES (@ticketId, N'Outbound', @Body, @now, N'Pending', 1)
            """,
            new { ticketId, draft.Body, now },
            transaction,
            cancellationToken: cancellationToken));

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.TicketDrafts SET Status = N'AutoSent', ResolvedAt = @now, SentMessageId = @messageId WHERE Id = @draftId",
            new { draftId, messageId, now },
            transaction,
            cancellationToken: cancellationToken));

        await AddEventAsync(connection, transaction, ticketId, TicketEventType.AiReplySent, null, null, null, now, cancellationToken);

        // Answered, and waiting to see whether the requester needs more. If they write back,
        // the ticket goes to a person.
        await SetStatusAsync(connection, transaction, ticketId, current.Status, TicketStatus.Resolved, null, now, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return messageId;
    }

    /// <summary>
    /// The AI hands an open ticket to a person: it becomes Needs Human Review, with the reason in
    /// its history. Does nothing if the ticket is not open.
    /// </summary>
    public async Task<bool> EscalateAsync(int ticketId, string reason, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var current = await LockAsync(connection, transaction, ticketId, cancellationToken);
        if (current is null || current.Status != TicketStatus.Open)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        await AddEventAsync(connection, transaction, ticketId, TicketEventType.Escalated, null, null, reason, now, cancellationToken);
        await SetStatusAsync(connection, transaction, ticketId, current.Status, TicketStatus.NeedsHumanReview, null, now, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Settles the ticket's pending drafts when an agent replies. The draft the agent started from
    /// is recorded as sent unchanged or sent with edits; any other pending draft was passed over,
    /// which counts as discarded.
    /// </summary>
    private async Task ResolveDraftsOnAgentReplyAsync(
        DbConnection connection,
        DbTransaction transaction,
        int ticketId,
        long? usedDraftId,
        string replyBody,
        long messageId,
        Guid userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (usedDraftId is { } draftId)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE dbo.TicketDrafts
                SET Status = CASE WHEN Body = @replyBody THEN N'Sent' ELSE N'Edited' END,
                    ResolvedAt = @now, ResolvedByUserId = @userId, SentMessageId = @messageId
                WHERE Id = @draftId AND TicketId = @ticketId AND Status = N'Pending'
                """,
                new { ticketId, draftId, replyBody, messageId, userId, now },
                transaction,
                cancellationToken: cancellationToken));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.TicketDrafts
            SET Status = N'Discarded', ResolvedAt = @now, ResolvedByUserId = @userId
            WHERE TicketId = @ticketId AND Status = N'Pending'
            """,
            new { ticketId, userId, now },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static Task SupersedePendingDraftsAsync(
        DbConnection connection, DbTransaction transaction, int ticketId, DateTimeOffset now, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.TicketDrafts SET Status = N'Superseded', ResolvedAt = @now WHERE TicketId = @ticketId AND Status = N'Pending'",
            new { ticketId, now },
            transaction,
            cancellationToken: cancellationToken));
}
