using Dapper;
using Helpdesk.Core.Tickets;
using Helpdesk.Infrastructure.Data;
using Microsoft.Data.SqlClient;

namespace Helpdesk.Infrastructure.Teams;

public sealed class Team
{
    public int Id { get; init; }
    public string Name { get; init; } = "";

    /// <summary>The default team takes tickets with no category, or whose category has no route.</summary>
    public bool IsDefault { get; init; }

    public int MemberCount { get; init; }
    public int ActiveMemberCount { get; init; }

    /// <summary>The categories whose tickets go to this team's queue.</summary>
    public IReadOnlyList<TicketCategory> Categories { get; set; } = [];
}

public enum TeamResult
{
    Ok,
    NotFound,
    NameTaken,

    /// <summary>The change would leave no default team.</summary>
    MustHaveDefault,

    /// <summary>The team still has members, tickets, or routed categories, or is the default.</summary>
    InUse,
}

/// <summary>Teams and the routes that send each category to a team.</summary>
public sealed class TeamStore(IDbConnectionFactory connectionFactory)
{
    private const int UniqueIndexViolation = 2601;
    private const int UniqueConstraintViolation = 2627;

    public async Task<IReadOnlyList<Team>> ListAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT t.Id, t.Name, t.IsDefault,
                   (SELECT COUNT(*) FROM dbo.Users u WHERE u.TeamId = t.Id) AS MemberCount,
                   (SELECT COUNT(*) FROM dbo.Users u WHERE u.TeamId = t.Id AND u.IsActive = 1) AS ActiveMemberCount
            FROM dbo.Teams t
            ORDER BY t.Name;

            SELECT TeamId, Category FROM dbo.CategoryRoutes;
            """;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var results = await connection.QueryMultipleAsync(new CommandDefinition(sql, cancellationToken: cancellationToken));

        var teams = (await results.ReadAsync<Team>()).AsList();
        var routes = (await results.ReadAsync<(int TeamId, string Category)>()).ToLookup(
            route => route.TeamId, route => Enum.Parse<TicketCategory>(route.Category));

        foreach (var team in teams)
        {
            team.Categories = routes[team.Id].Order().ToList();
        }

        return teams;
    }

    public async Task<bool> ExistsAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.Teams WHERE Id = @id) THEN 1 ELSE 0 END",
            new { id },
            cancellationToken: cancellationToken));
    }

    public async Task<(TeamResult Result, int? Id)> CreateAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken);
            var id = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "INSERT INTO dbo.Teams (Name) OUTPUT INSERTED.Id VALUES (@name)",
                new { name },
                cancellationToken: cancellationToken));
            return (TeamResult.Ok, id);
        }
        catch (SqlException ex) when (ex.Number is UniqueIndexViolation or UniqueConstraintViolation)
        {
            return (TeamResult.NameTaken, null);
        }
    }

    /// <summary>
    /// Renames the team, sets which categories route to it, and can make it the default.
    /// A category given to this team is taken from whichever team had it. Existing tickets stay
    /// where they are; routes apply to new tickets and to category changes.
    /// </summary>
    public async Task<TeamResult> UpdateAsync(
        int id, string name, IReadOnlyCollection<TicketCategory> categories, bool isDefault, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var wasDefault = await connection.ExecuteScalarAsync<bool?>(new CommandDefinition(
            "SELECT IsDefault FROM dbo.Teams WITH (UPDLOCK, ROWLOCK) WHERE Id = @id",
            new { id },
            transaction,
            cancellationToken: cancellationToken));

        if (wasDefault is null)
        {
            return TeamResult.NotFound;
        }

        if (wasDefault == true && !isDefault)
        {
            return TeamResult.MustHaveDefault;
        }

        try
        {
            if (isDefault && wasDefault == false)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE dbo.Teams SET IsDefault = 0 WHERE IsDefault = 1",
                    transaction: transaction,
                    cancellationToken: cancellationToken));
            }

            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE dbo.Teams SET Name = @name, IsDefault = @isDefault WHERE Id = @id",
                new { id, name, isDefault },
                transaction,
                cancellationToken: cancellationToken));
        }
        catch (SqlException ex) when (ex.Number is UniqueIndexViolation or UniqueConstraintViolation)
        {
            return TeamResult.NameTaken;
        }

        var names = categories.Select(category => category.ToString()).Distinct().ToArray();
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.CategoryRoutes WHERE TeamId = @id OR Category IN @names",
            // An empty IN list is not valid SQL, so pass a value no category can equal.
            new { id, names = names.Length == 0 ? [""] : names },
            transaction,
            cancellationToken: cancellationToken));

        foreach (var category in names)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO dbo.CategoryRoutes (Category, TeamId) VALUES (@category, @id)",
                new { category, id },
                transaction,
                cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        return TeamResult.Ok;
    }

    /// <summary>Deletes a team that nothing depends on any more.</summary>
    public async Task<TeamResult> DeleteAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var state = await connection.QuerySingleOrDefaultAsync<(bool IsDefault, bool InUse)?>(new CommandDefinition(
            """
            SELECT t.IsDefault,
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.Users WHERE TeamId = t.Id)
                               OR EXISTS (SELECT 1 FROM dbo.Tickets WHERE TeamId = t.Id)
                               OR EXISTS (SELECT 1 FROM dbo.CategoryRoutes WHERE TeamId = t.Id)
                             THEN 1 ELSE 0 END AS BIT) AS InUse
            FROM dbo.Teams t WITH (UPDLOCK, ROWLOCK)
            WHERE t.Id = @id
            """,
            new { id },
            transaction,
            cancellationToken: cancellationToken));

        if (state is null)
        {
            return TeamResult.NotFound;
        }

        if (state.Value.IsDefault || state.Value.InUse)
        {
            return TeamResult.InUse;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.Teams WHERE Id = @id", new { id }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return TeamResult.Ok;
    }
}
