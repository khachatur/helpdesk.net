using Dapper;
using Helpdesk.Core.Users;
using Helpdesk.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;

namespace Helpdesk.Infrastructure.Identity;

/// <summary>
/// ASP.NET Core Identity user store backed by the Users table through Dapper.
/// Identity changes the <see cref="User"/> object through the Set* methods and then calls
/// <see cref="UpdateAsync"/>, which writes every column.
/// </summary>
public sealed class DapperUserStore(IDbConnectionFactory connectionFactory) :
    IUserPasswordStore<User>,
    IUserEmailStore<User>,
    IUserLockoutStore<User>,
    IUserSecurityStampStore<User>
{
    internal const string Columns =
        "Id, Email, NormalizedEmail, DisplayName, PasswordHash, SecurityStamp, Role, IsActive, AccessFailedCount, LockoutEnd, CreatedAt";

    private const int UniqueIndexViolation = 2601;
    private const int UniqueConstraintViolation = 2627;

    public async Task<IdentityResult> CreateAsync(User user, CancellationToken cancellationToken)
    {
        const string sql = $"""
            INSERT INTO dbo.Users ({Columns})
            VALUES (@Id, @Email, @NormalizedEmail, @DisplayName, @PasswordHash, @SecurityStamp, @Role, @IsActive,
                    @AccessFailedCount, @LockoutEnd, @CreatedAt)
            """;

        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(sql, user, cancellationToken: cancellationToken));
            return IdentityResult.Success;
        }
        catch (SqlException ex) when (ex.Number is UniqueIndexViolation or UniqueConstraintViolation)
        {
            return IdentityResult.Failed(new IdentityError
            {
                Code = "DuplicateEmail",
                Description = $"Email '{user.Email}' is already taken.",
            });
        }
    }

    public async Task<IdentityResult> UpdateAsync(User user, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.Users
            SET Email = @Email,
                NormalizedEmail = @NormalizedEmail,
                DisplayName = @DisplayName,
                PasswordHash = @PasswordHash,
                SecurityStamp = @SecurityStamp,
                Role = @Role,
                IsActive = @IsActive,
                AccessFailedCount = @AccessFailedCount,
                LockoutEnd = @LockoutEnd
            WHERE Id = @Id
            """;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        var rows = await connection.ExecuteAsync(new CommandDefinition(sql, user, cancellationToken: cancellationToken));

        return rows == 1
            ? IdentityResult.Success
            : IdentityResult.Failed(new IdentityError { Code = "UserNotFound", Description = "The user no longer exists." });
    }

    public async Task<IdentityResult> DeleteAsync(User user, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.Users WHERE Id = @Id", new { user.Id }, cancellationToken: cancellationToken));
        return IdentityResult.Success;
    }

    public async Task<User?> FindByIdAsync(string userId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(userId, out var id))
        {
            return null;
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<User>(new CommandDefinition(
            $"SELECT {Columns} FROM dbo.Users WHERE Id = @id", new { id }, cancellationToken: cancellationToken));
    }

    public async Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<User>(new CommandDefinition(
            $"SELECT {Columns} FROM dbo.Users WHERE NormalizedEmail = @normalizedEmail",
            new { normalizedEmail },
            cancellationToken: cancellationToken));
    }

    // The email address is the user name.
    public Task<User?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) =>
        FindByEmailAsync(normalizedUserName, cancellationToken);

    public Task<string> GetUserIdAsync(User user, CancellationToken cancellationToken) =>
        Task.FromResult(user.Id.ToString());

    public Task<string?> GetUserNameAsync(User user, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(user.Email);

    public Task SetUserNameAsync(User user, string? userName, CancellationToken cancellationToken)
    {
        user.Email = userName ?? "";
        return Task.CompletedTask;
    }

    public Task<string?> GetNormalizedUserNameAsync(User user, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(user.NormalizedEmail);

    public Task SetNormalizedUserNameAsync(User user, string? normalizedName, CancellationToken cancellationToken)
    {
        user.NormalizedEmail = normalizedName ?? "";
        return Task.CompletedTask;
    }

    public Task<string?> GetEmailAsync(User user, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(user.Email);

    public Task SetEmailAsync(User user, string? email, CancellationToken cancellationToken)
    {
        user.Email = email ?? "";
        return Task.CompletedTask;
    }

    public Task<string?> GetNormalizedEmailAsync(User user, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(user.NormalizedEmail);

    public Task SetNormalizedEmailAsync(User user, string? normalizedEmail, CancellationToken cancellationToken)
    {
        user.NormalizedEmail = normalizedEmail ?? "";
        return Task.CompletedTask;
    }

    // Accounts are created by the admin, so there is no email confirmation step.
    public Task<bool> GetEmailConfirmedAsync(User user, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task SetEmailConfirmedAsync(User user, bool confirmed, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<string?> GetPasswordHashAsync(User user, CancellationToken cancellationToken) =>
        Task.FromResult(user.PasswordHash);

    public Task SetPasswordHashAsync(User user, string? passwordHash, CancellationToken cancellationToken)
    {
        user.PasswordHash = passwordHash;
        return Task.CompletedTask;
    }

    public Task<bool> HasPasswordAsync(User user, CancellationToken cancellationToken) =>
        Task.FromResult(user.PasswordHash is not null);

    public Task<string?> GetSecurityStampAsync(User user, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(user.SecurityStamp);

    public Task SetSecurityStampAsync(User user, string stamp, CancellationToken cancellationToken)
    {
        user.SecurityStamp = stamp;
        return Task.CompletedTask;
    }

    public Task<DateTimeOffset?> GetLockoutEndDateAsync(User user, CancellationToken cancellationToken) =>
        Task.FromResult(user.LockoutEnd);

    public Task SetLockoutEndDateAsync(User user, DateTimeOffset? lockoutEnd, CancellationToken cancellationToken)
    {
        user.LockoutEnd = lockoutEnd;
        return Task.CompletedTask;
    }

    public Task<int> IncrementAccessFailedCountAsync(User user, CancellationToken cancellationToken) =>
        Task.FromResult(++user.AccessFailedCount);

    public Task ResetAccessFailedCountAsync(User user, CancellationToken cancellationToken)
    {
        user.AccessFailedCount = 0;
        return Task.CompletedTask;
    }

    public Task<int> GetAccessFailedCountAsync(User user, CancellationToken cancellationToken) =>
        Task.FromResult(user.AccessFailedCount);

    // Lockout applies to every account and cannot be switched off per user.
    public Task<bool> GetLockoutEnabledAsync(User user, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task SetLockoutEnabledAsync(User user, bool enabled, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public void Dispose()
    {
    }
}
