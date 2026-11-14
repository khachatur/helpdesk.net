using Dapper;
using Helpdesk.Core.Users;
using Helpdesk.Infrastructure.Data;

namespace Helpdesk.Infrastructure.Identity;

/// <summary>Read queries over users that ASP.NET Core Identity's store interfaces do not cover.</summary>
public sealed class UserQueries(IDbConnectionFactory connectionFactory)
{
    public async Task<IReadOnlyList<UserWithTeam>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        var users = await connection.QueryAsync<UserWithTeam>(new CommandDefinition(
            """
            SELECT u.*, team.Name AS TeamName
            FROM dbo.Users u
            LEFT JOIN dbo.Teams team ON team.Id = u.TeamId
            ORDER BY u.DisplayName, u.Email
            """,
            cancellationToken: cancellationToken));
        return users.AsList();
    }

    public async Task<bool> AnyAdminAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.Users WHERE Role = @role) THEN 1 ELSE 0 END",
            new { role = Roles.Admin },
            cancellationToken: cancellationToken));
    }
}

/// <summary>A user with the name of their team, for lists.</summary>
public sealed class UserWithTeam : User
{
    public string? TeamName { get; set; }
}
