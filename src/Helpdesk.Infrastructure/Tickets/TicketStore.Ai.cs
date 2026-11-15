using Dapper;
using Helpdesk.Core.Tickets;

namespace Helpdesk.Infrastructure.Tickets;

/// <summary>What the AI contributes to a ticket: its category, and a summary.</summary>
public sealed partial class TicketStore
{
    /// <summary>
    /// Records the result of classification and puts the ticket in a team queue.
    /// <paramref name="accepted"/> is the category to give the ticket: null when the AI was unsure,
    /// could not decide, or failed, in which case the ticket goes to the default team unclassified.
    /// <paramref name="aiCategory"/> and <paramref name="confidence"/> are what the AI said, kept
    /// whether or not it was accepted. A category an agent already chose is never overwritten.
    /// </summary>
    public async Task ApplyClassificationAsync(
        int id,
        TicketCategory? accepted,
        TicketCategory? aiCategory,
        double? confidence,
        bool failed,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var current = await LockAsync(connection, transaction, id, cancellationToken);
        if (current is null)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.Tickets
            SET ClassificationStatus = @status, AiCategory = @aiCategory, AiConfidence = @confidence
            WHERE Id = @id
            """,
            new
            {
                id,
                status = (failed ? ClassificationStatus.Failed : ClassificationStatus.Done).ToString(),
                aiCategory = aiCategory?.ToString(),
                confidence = confidence is { } value ? Math.Round((decimal)value, 3) : (decimal?)null,
            },
            transaction,
            cancellationToken: cancellationToken));

        if (TicketStatusRules.AcceptsChanges(current.Status))
        {
            var category = current.Category;
            if (category is null && accepted is not null)
            {
                category = accepted;
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE dbo.Tickets SET Category = @category, UpdatedAt = @now WHERE Id = @id",
                    new { id, category = category.ToString(), now },
                    transaction,
                    cancellationToken: cancellationToken));

                await AddEventAsync(
                    connection, transaction, id, TicketEventType.CategoryChanged, null, null, category.ToString(), now, cancellationToken);
            }

            var team = await ResolveTeamAsync(connection, transaction, category, cancellationToken);
            if (team != current.TeamId)
            {
                await MoveToTeamAsync(connection, transaction, id, current.TeamId, team, current.AssigneeId, now, cancellationToken);
            }
            else if (current.AssigneeId is null)
            {
                await AssignNextAsync(connection, transaction, id, team, null, now, cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Counts a classification attempt and returns how many there have now been.</summary>
    public async Task<int> CountClassificationAttemptAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            UPDATE dbo.Tickets SET ClassificationAttempts = ClassificationAttempts + 1
            OUTPUT INSERTED.ClassificationAttempts
            WHERE Id = @id
            """,
            new { id },
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Tickets that have waited for classification since before the given moment. Something went
    /// wrong with the job that should have classified them, so the caller routes them without it.
    /// </summary>
    public async Task<IReadOnlyList<int>> ListStuckInClassificationAsync(DateTimeOffset requestedBefore, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        var ids = await connection.QueryAsync<int>(new CommandDefinition(
            """
            SELECT Id FROM dbo.Tickets
            WHERE ClassificationStatus = N'Pending' AND ClassificationRequestedAt < @requestedBefore
            ORDER BY Id
            """,
            new { requestedBefore },
            cancellationToken: cancellationToken));
        return ids.AsList();
    }

    /// <summary>
    /// Closes tickets that were resolved before the given moment and have stayed resolved since.
    /// A requester who has not written back in that time is taken to be satisfied. Returns how many were closed.
    /// </summary>
    public async Task<int> CloseResolvedBeforeAsync(DateTimeOffset resolvedBefore, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        var ids = (await connection.QueryAsync<int>(new CommandDefinition(
            "SELECT Id FROM dbo.Tickets WHERE Status = N'Resolved' AND ResolvedAt < @resolvedBefore ORDER BY Id",
            new { resolvedBefore },
            cancellationToken: cancellationToken))).AsList();

        var closed = 0;
        foreach (var id in ids)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            // Checked again under the lock: the requester may have replied a moment ago.
            var current = await LockAsync(connection, transaction, id, cancellationToken);
            var stillDue = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.Tickets WHERE Id = @id AND ResolvedAt < @resolvedBefore) THEN 1 ELSE 0 END",
                new { id, resolvedBefore },
                transaction,
                cancellationToken: cancellationToken));

            if (current?.Status == TicketStatus.Resolved && stillDue)
            {
                await SetStatusAsync(
                    connection, transaction, id, TicketStatus.Resolved, TicketStatus.Closed, null, timeProvider.GetUtcNow(), cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                closed++;
            }
        }

        return closed;
    }

    /// <summary>Stores the summary and how many messages it covers.</summary>
    public async Task SaveSummaryAsync(int id, string summary, int messageCount, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.Tickets SET Summary = @summary, SummaryMessageCount = @messageCount, SummaryUpdatedAt = @now WHERE Id = @id",
            new { id, summary, messageCount, now = timeProvider.GetUtcNow() },
            cancellationToken: cancellationToken));
    }
}
