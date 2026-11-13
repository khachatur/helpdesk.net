using Dapper;
using Helpdesk.Core.Users;
using Helpdesk.Infrastructure.Data;

namespace Helpdesk.Infrastructure.Identity;

/// <summary>Read queries over users that ASP.NET Core Identity's store interfaces do not cover.</summary>
public sealed class UserQueries(IDbConnectionFactory connectionFactory)
{
    public async Task<IReadOnlyList<User>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        var users = await connection.QueryAsync<User>(new CommandDefinition(
            $"SELECT {DapperUserStore.Columns} FROM dbo.Users ORDER BY DisplayName, Email",
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
