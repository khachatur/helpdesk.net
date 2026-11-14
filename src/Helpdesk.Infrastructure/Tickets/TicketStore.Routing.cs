using System.Data.Common;
using Dapper;
using Helpdesk.Core.Tickets;

namespace Helpdesk.Infrastructure.Tickets;

/// <summary>
/// Routing a ticket to a team queue and giving it an owner. A ticket's team follows its category;
/// its owner is the next active member of that team in round-robin order.
/// </summary>
public sealed partial class TicketStore
{
    /// <summary>The team that handles a category: its route, or the default team when there is none.</summary>
    private static Task<int?> ResolveTeamAsync(
        DbConnection connection, DbTransaction transaction, TicketCategory? category, CancellationToken cancellationToken) =>
        connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            """
            SELECT COALESCE(
                (SELECT TeamId FROM dbo.CategoryRoutes WHERE Category = @category),
                (SELECT Id FROM dbo.Teams WHERE IsDefault = 1))
            """,
            new { category = category?.ToString() },
            transaction,
            cancellationToken: cancellationToken));

    /// <summary>
    /// Picks the team's next active member after the one who got its previous ticket, and moves the
    /// team's position on. The team row is locked until the transaction ends, so two tickets created
    /// at the same moment cannot both go to the same member. Null when the team has no active member.
    /// </summary>
    private static async Task<Guid?> NextAssigneeAsync(
        DbConnection connection, DbTransaction transaction, int teamId, CancellationToken cancellationToken)
    {
        var userId = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            """
            DECLARE @last UNIQUEIDENTIFIER =
                (SELECT LastAssignedUserId FROM dbo.Teams WITH (UPDLOCK, ROWLOCK) WHERE Id = @teamId);

            SELECT TOP (1) Id
            FROM dbo.Users
            WHERE TeamId = @teamId AND IsActive = 1
            ORDER BY CASE WHEN @last IS NULL OR Id > @last THEN 0 ELSE 1 END, Id;
            """,
            new { teamId },
            transaction,
            cancellationToken: cancellationToken));

        if (userId is not null)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE dbo.Teams SET LastAssignedUserId = @userId WHERE Id = @teamId",
                new { teamId, userId },
                transaction,
                cancellationToken: cancellationToken));
        }

        return userId;
    }

    /// <summary>Whether the user can still own tickets in the team: active, and a member of it.</summary>
    private static Task<bool> CanOwnAsync(
        DbConnection connection, DbTransaction transaction, Guid userId, int? teamId, CancellationToken cancellationToken) =>
        connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.Users WHERE Id = @userId AND IsActive = 1 AND TeamId = @teamId) THEN 1 ELSE 0 END",
            new { userId, teamId },
            transaction,
            cancellationToken: cancellationToken));

    /// <summary>Sets the ticket's owner (or clears it) and records the change. Does nothing if the owner is unchanged.</summary>
    private static async Task SetAssigneeAsync(
        DbConnection connection,
        DbTransaction transaction,
        int ticketId,
        Guid? from,
        Guid? to,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (from == to)
        {
            return;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.Tickets SET AssigneeId = @to, UpdatedAt = @now WHERE Id = @ticketId",
            new { ticketId, to, now },
            transaction,
            cancellationToken: cancellationToken));

        var names = (await connection.QueryAsync<(Guid Id, string DisplayName)>(new CommandDefinition(
            "SELECT Id, DisplayName FROM dbo.Users WHERE Id IN (@from, @to)",
            new { from, to },
            transaction,
            cancellationToken: cancellationToken))).ToDictionary(user => user.Id, user => user.DisplayName);

        await AddEventAsync(
            connection, transaction, ticketId, TicketEventType.Assigned, null,
            from is { } fromId ? names.GetValueOrDefault(fromId) : null,
            to is { } toId ? names.GetValueOrDefault(toId) : null,
            now, cancellationToken);
    }

    /// <summary>Gives the ticket to the next member of its team. Leaves it unassigned when the team has nobody active.</summary>
    private static async Task AssignNextAsync(
        DbConnection connection,
        DbTransaction transaction,
        int ticketId,
        int? teamId,
        Guid? currentAssignee,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var next = teamId is { } team ? await NextAssigneeAsync(connection, transaction, team, cancellationToken) : null;
        await SetAssigneeAsync(connection, transaction, ticketId, currentAssignee, next, now, cancellationToken);
    }

    /// <summary>Moves the ticket to another team's queue, records it, and gives it an owner there.</summary>
    private static async Task MoveToTeamAsync(
        DbConnection connection,
        DbTransaction transaction,
        int ticketId,
        int? fromTeam,
        int? toTeam,
        Guid? currentAssignee,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.Tickets SET TeamId = @toTeam, UpdatedAt = @now WHERE Id = @ticketId",
            new { ticketId, toTeam, now },
            transaction,
            cancellationToken: cancellationToken));

        var names = (await connection.QueryAsync<(int Id, string Name)>(new CommandDefinition(
            "SELECT Id, Name FROM dbo.Teams WHERE Id IN (@fromTeam, @toTeam)",
            new { fromTeam, toTeam },
            transaction,
            cancellationToken: cancellationToken))).ToDictionary(team => team.Id, team => team.Name);

        await AddEventAsync(
            connection, transaction, ticketId, TicketEventType.TeamChanged, null,
            fromTeam is { } fromId ? names.GetValueOrDefault(fromId) : null,
            toTeam is { } toId ? names.GetValueOrDefault(toId) : null,
            now, cancellationToken);

        await AssignNextAsync(connection, transaction, ticketId, toTeam, currentAssignee, now, cancellationToken);
    }

    /// <summary>
    /// Hands a user's active tickets to other members of each ticket's team. Used when the user is
    /// deactivated, or leaves a team (then <paramref name="keepInTeamId"/> is the team they are in now,
    /// whose tickets they keep). Resolved tickets are left alone: they are reassigned only if reopened.
    /// Returns how many tickets changed owner.
    /// </summary>
    public async Task<int> ReassignFromUserAsync(Guid userId, int? keepInTeamId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var tickets = (await connection.QueryAsync<(int Id, int? TeamId)>(new CommandDefinition(
            """
            SELECT Id, TeamId
            FROM dbo.Tickets WITH (UPDLOCK, ROWLOCK)
            WHERE AssigneeId = @userId AND Status IN (N'Open', N'NeedsHumanReview')
              AND (@keepInTeamId IS NULL OR TeamId IS NULL OR TeamId <> @keepInTeamId)
            ORDER BY Id
            """,
            new { userId, keepInTeamId },
            transaction,
            cancellationToken: cancellationToken))).AsList();

        var now = timeProvider.GetUtcNow();
        foreach (var ticket in tickets)
        {
            await AssignNextAsync(connection, transaction, ticket.Id, ticket.TeamId, userId, now, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return tickets.Count;
    }

    /// <summary>
    /// Gives owners to the team's active tickets that have none. Used when a team that had nobody
    /// active gets a member. Returns how many tickets were assigned.
    /// </summary>
    public async Task<int> AssignUnassignedAsync(int teamId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var ticketIds = (await connection.QueryAsync<int>(new CommandDefinition(
            """
            SELECT Id
            FROM dbo.Tickets WITH (UPDLOCK, ROWLOCK)
            WHERE TeamId = @teamId AND AssigneeId IS NULL AND Status IN (N'Open', N'NeedsHumanReview')
            ORDER BY Id
            """,
            new { teamId },
            transaction,
            cancellationToken: cancellationToken))).AsList();

        var now = timeProvider.GetUtcNow();
        foreach (var ticketId in ticketIds)
        {
            await AssignNextAsync(connection, transaction, ticketId, teamId, null, now, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return ticketIds.Count;
    }
}
