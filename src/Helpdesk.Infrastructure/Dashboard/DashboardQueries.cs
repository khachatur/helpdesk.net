using Dapper;
using Helpdesk.Infrastructure.Data;

namespace Helpdesk.Infrastructure.Dashboard;

public sealed record StatusCount(string Status, int Count);

public sealed record TeamLoad(string Team, int Active, int Unassigned);

public sealed record DayVolume(DateTime Day, int Created);

public sealed record TicketLink(int Id, string Subject);

/// <summary>What needs someone's attention right now.</summary>
public sealed record AttentionItems(
    int UnassignedTickets,
    int FailedSends,
    IReadOnlyList<TicketLink> FailedSendTickets,
    int StuckInClassification,
    int ShadowRepliesAwaitingReview,
    IReadOnlyList<TicketLink> ShadowReplyTickets);

/// <summary>Tickets created in the period, and how they were handled.</summary>
/// <param name="MedianFirstResponseSeconds">Null when no ticket in the period has been answered.</param>
/// <param name="MedianResolutionSeconds">Null when no ticket in the period has been resolved.</param>
public sealed record TicketMetrics(
    int Created,
    int Answered,
    double? MedianFirstResponseSeconds,
    int Resolved,
    double? MedianResolutionSeconds);

/// <summary>How the AI did on tickets created in the period, and on drafts written in it.</summary>
public sealed record AiMetrics(
    int ClassifiedByAi,
    int ClassificationsOverridden,
    int ClassificationsFailed,
    int DraftsSentUnchanged,
    int DraftsSentEdited,
    int DraftsDiscarded,
    int AnsweredAutomatically,
    int WouldHaveBeenAnsweredAutomatically,
    int Escalated);

public sealed record AiUsage(string Purpose, int Calls, int Failures, long InputTokens, long OutputTokens);

public sealed record DashboardData(
    IReadOnlyList<StatusCount> OpenByStatus,
    IReadOnlyList<TeamLoad> ActiveByTeam,
    AttentionItems Attention,
    TicketMetrics Tickets,
    IReadOnlyList<DayVolume> DailyVolume,
    AiMetrics Ai,
    IReadOnlyList<AiUsage> AiUsage);

/// <summary>The numbers behind the dashboard. Read-only.</summary>
public sealed class DashboardQueries(IDbConnectionFactory connectionFactory, TimeProvider timeProvider)
{
    private const int LinkLimit = 10;

    /// <summary>
    /// "Now" figures describe the current state of every ticket. Period figures cover tickets
    /// created from <paramref name="from"/> up to, not including, <paramref name="to"/>.
    /// </summary>
    public async Task<DashboardData> GetAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        const string sql = """
            -- 1. Tickets not yet closed, by status.
            SELECT Status, COUNT(*) AS Count FROM dbo.Tickets WHERE Status <> N'Closed' GROUP BY Status;

            -- 2. Tickets still needing work, by team queue.
            SELECT COALESCE(team.Name, N'Not in a queue yet') AS Team, COUNT(*) AS Active,
                   SUM(CASE WHEN t.AssigneeId IS NULL THEN 1 ELSE 0 END) AS Unassigned
            FROM dbo.Tickets t
            LEFT JOIN dbo.Teams team ON team.Id = t.TeamId
            WHERE t.Status IN (N'Open', N'NeedsHumanReview')
            GROUP BY team.Name
            ORDER BY Active DESC, Team;

            -- 3. Attention counts.
            SELECT
                (SELECT COUNT(*) FROM dbo.Tickets WHERE Status IN (N'Open', N'NeedsHumanReview') AND AssigneeId IS NULL) AS UnassignedTickets,
                (SELECT COUNT(*) FROM dbo.TicketMessages WHERE SendStatus = N'Failed') AS FailedSends,
                (SELECT COUNT(*) FROM dbo.Tickets WHERE ClassificationStatus = N'Pending' AND ClassificationRequestedAt < @stuckBefore) AS StuckInClassification,
                (SELECT COUNT(*) FROM dbo.TicketDrafts WHERE Status = N'Pending' AND GateDecision = N'WouldSend') AS ShadowRepliesAwaitingReview;

            -- 4. Tickets with a reply that could not be emailed.
            SELECT TOP (@linkLimit) t.Id, t.Subject
            FROM dbo.Tickets t
            WHERE EXISTS (SELECT 1 FROM dbo.TicketMessages m WHERE m.TicketId = t.Id AND m.SendStatus = N'Failed')
            ORDER BY t.Id DESC;

            -- 5. Tickets with a shadow-mode reply waiting for review.
            SELECT TOP (@linkLimit) t.Id, t.Subject
            FROM dbo.Tickets t
            WHERE EXISTS (SELECT 1 FROM dbo.TicketDrafts d WHERE d.TicketId = t.Id AND d.Status = N'Pending' AND d.GateDecision = N'WouldSend')
            ORDER BY t.Id DESC;

            -- 6. Tickets created in the period: how many, how fast.
            WITH period AS
            (
                SELECT t.Id, t.CreatedAt, COALESCE(t.ResolvedAt, t.ClosedAt) AS FinishedAt,
                       (SELECT MIN(m.CreatedAt) FROM dbo.TicketMessages m
                        WHERE m.TicketId = t.Id AND m.Direction = N'Outbound') AS FirstReplyAt
                FROM dbo.Tickets t
                WHERE t.CreatedAt >= @from AND t.CreatedAt < @to
            )
            SELECT
                (SELECT COUNT(*) FROM period) AS Created,
                (SELECT COUNT(*) FROM period WHERE FirstReplyAt IS NOT NULL) AS Answered,
                (SELECT TOP (1) PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY DATEDIFF(SECOND, CreatedAt, FirstReplyAt)) OVER ()
                 FROM period WHERE FirstReplyAt IS NOT NULL) AS MedianFirstResponseSeconds,
                (SELECT COUNT(*) FROM period WHERE FinishedAt IS NOT NULL) AS Resolved,
                (SELECT TOP (1) PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY DATEDIFF(SECOND, CreatedAt, FinishedAt)) OVER ()
                 FROM period WHERE FinishedAt IS NOT NULL) AS MedianResolutionSeconds;

            -- 7. New tickets per day (UTC days).
            SELECT CAST(SWITCHOFFSET(CreatedAt, '+00:00') AS DATE) AS Day, COUNT(*) AS Created
            FROM dbo.Tickets
            WHERE CreatedAt >= @from AND CreatedAt < @to
            GROUP BY CAST(SWITCHOFFSET(CreatedAt, '+00:00') AS DATE)
            ORDER BY Day;

            -- 8. The AI's part in tickets created, and drafts written, in the period.
            SELECT
                (SELECT COUNT(*) FROM dbo.Tickets WHERE CreatedAt >= @from AND CreatedAt < @to AND AiCategory IS NOT NULL) AS ClassifiedByAi,
                (SELECT COUNT(*) FROM dbo.Tickets WHERE CreatedAt >= @from AND CreatedAt < @to
                    AND AiCategory IS NOT NULL AND Category IS NOT NULL AND Category <> AiCategory) AS ClassificationsOverridden,
                (SELECT COUNT(*) FROM dbo.Tickets WHERE CreatedAt >= @from AND CreatedAt < @to AND ClassificationStatus = N'Failed') AS ClassificationsFailed,
                (SELECT COUNT(*) FROM dbo.TicketDrafts WHERE CreatedAt >= @from AND CreatedAt < @to AND Status = N'Sent') AS DraftsSentUnchanged,
                (SELECT COUNT(*) FROM dbo.TicketDrafts WHERE CreatedAt >= @from AND CreatedAt < @to AND Status = N'Edited') AS DraftsSentEdited,
                (SELECT COUNT(*) FROM dbo.TicketDrafts WHERE CreatedAt >= @from AND CreatedAt < @to AND Status = N'Discarded') AS DraftsDiscarded,
                (SELECT COUNT(*) FROM dbo.Tickets t WHERE t.CreatedAt >= @from AND t.CreatedAt < @to
                    AND EXISTS (SELECT 1 FROM dbo.TicketMessages m WHERE m.TicketId = t.Id AND m.IsAiGenerated = 1)) AS AnsweredAutomatically,
                (SELECT COUNT(DISTINCT d.TicketId) FROM dbo.TicketDrafts d JOIN dbo.Tickets t ON t.Id = d.TicketId
                    WHERE t.CreatedAt >= @from AND t.CreatedAt < @to AND d.GateDecision = N'WouldSend') AS WouldHaveBeenAnsweredAutomatically,
                (SELECT COUNT(*) FROM dbo.Tickets t WHERE t.CreatedAt >= @from AND t.CreatedAt < @to
                    AND EXISTS (SELECT 1 FROM dbo.TicketEvents e WHERE e.TicketId = t.Id AND e.Type = N'Escalated')) AS Escalated;

            -- 9. Calls to the AI provider in the period.
            SELECT Purpose, COUNT(*) AS Calls,
                   SUM(CASE WHEN Outcome <> N'Succeeded' THEN 1 ELSE 0 END) AS Failures,
                   COALESCE(SUM(CAST(InputTokens AS BIGINT)), 0) AS InputTokens,
                   COALESCE(SUM(CAST(OutputTokens AS BIGINT)), 0) AS OutputTokens
            FROM dbo.AiRuns
            WHERE CreatedAt >= @from AND CreatedAt < @to
            GROUP BY Purpose
            ORDER BY Purpose;
            """;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var results = await connection.QueryMultipleAsync(new CommandDefinition(
            sql,
            new { from, to, linkLimit = LinkLimit, stuckBefore = timeProvider.GetUtcNow().AddMinutes(-10) },
            cancellationToken: cancellationToken));

        var byStatus = (await results.ReadAsync<StatusCount>()).AsList();
        var byTeam = (await results.ReadAsync<TeamLoad>()).AsList();
        var counts = await results.ReadSingleAsync<(int Unassigned, int FailedSends, int Stuck, int Shadow)>();
        var failedSendTickets = (await results.ReadAsync<TicketLink>()).AsList();
        var shadowTickets = (await results.ReadAsync<TicketLink>()).AsList();
        var tickets = await results.ReadSingleAsync<TicketMetrics>();
        var daily = (await results.ReadAsync<DayVolume>()).AsList();
        var ai = await results.ReadSingleAsync<AiMetrics>();
        var usage = (await results.ReadAsync<AiUsage>()).AsList();

        return new DashboardData(
            byStatus,
            byTeam,
            new AttentionItems(counts.Unassigned, counts.FailedSends, failedSendTickets, counts.Stuck, counts.Shadow, shadowTickets),
            tickets,
            daily,
            ai,
            usage);
    }
}
