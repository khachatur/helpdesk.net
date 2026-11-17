using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Helpdesk.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace Helpdesk.Infrastructure.Identity;

/// <summary>
/// Keeps sign-in sessions in the database instead of in the cookie. The cookie carries only a
/// random session id (encrypted, like any authentication cookie); the session itself is a row in
/// dbo.UserSessions, keyed by a hash of that id.
///
/// This makes a session something the server can end: signing out deletes the row, so a copied
/// cookie stops working at once, and deactivating a user or resetting their password deletes all
/// of their sessions.
/// </summary>
public sealed class DatabaseTicketStore(IDbConnectionFactory connectionFactory, TimeProvider timeProvider) : ITicketStore
{
    /// <summary>Where the session key of the current request is kept, for <see cref="CurrentKey"/>.</summary>
    private const string CurrentKeyItem = "Helpdesk.SessionKey";

    /// <summary>Used only if the cookie handler did not set an expiry, which it always should.</summary>
    private static readonly TimeSpan FallbackLifetime = TimeSpan.FromHours(8);

    /// <summary>
    /// The key of the session the current request was signed in with, or null. The cookie handler
    /// keeps the key to itself, so the store notes it as the handler reads or creates the session.
    /// </summary>
    public static string? CurrentKey(HttpContext context) => context.Items[CurrentKeyItem] as string;

    public async Task<string> StoreAsync(AuthenticationTicket ticket, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var key = await StoreAsync(ticket, cancellationToken);
        httpContext.Items[CurrentKeyItem] = key;
        return key;
    }

    public Task<AuthenticationTicket?> RetrieveAsync(string key, HttpContext httpContext, CancellationToken cancellationToken)
    {
        httpContext.Items[CurrentKeyItem] = key;
        return RetrieveAsync(key, cancellationToken);
    }

    public Task<string> StoreAsync(AuthenticationTicket ticket) => StoreAsync(ticket, CancellationToken.None);

    public async Task<string> StoreAsync(AuthenticationTicket ticket, CancellationToken cancellationToken)
    {
        var userId = UserIdOf(ticket)
            ?? throw new InvalidOperationException("A session can only be stored for a signed-in user.");

        // 256 random bits: the id cannot be guessed, and only its hash is stored.
        var key = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var now = timeProvider.GetUtcNow();

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO dbo.UserSessions (KeyHash, UserId, Ticket, CreatedAt, ExpiresAt)
            VALUES (@keyHash, @userId, @ticket, @now, @expiresAt)
            """,
            new { keyHash = Hash(key), userId, ticket = Serialize(ticket), now, expiresAt = ExpiresAt(ticket, now) },
            cancellationToken: cancellationToken));

        return key;
    }

    public Task RenewAsync(string key, AuthenticationTicket ticket) => RenewAsync(key, ticket, CancellationToken.None);

    public async Task RenewAsync(string key, AuthenticationTicket ticket, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.UserSessions SET Ticket = @ticket, ExpiresAt = @expiresAt WHERE KeyHash = @keyHash",
            new { keyHash = Hash(key), ticket = Serialize(ticket), expiresAt = ExpiresAt(ticket, timeProvider.GetUtcNow()) },
            cancellationToken: cancellationToken));
    }

    public Task<AuthenticationTicket?> RetrieveAsync(string key) => RetrieveAsync(key, CancellationToken.None);

    public async Task<AuthenticationTicket?> RetrieveAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        var bytes = await connection.QuerySingleOrDefaultAsync<byte[]>(new CommandDefinition(
            "SELECT Ticket FROM dbo.UserSessions WHERE KeyHash = @keyHash AND ExpiresAt > @now",
            new { keyHash = Hash(key), now = timeProvider.GetUtcNow() },
            cancellationToken: cancellationToken));

        return bytes is null ? null : TicketSerializer.Default.Deserialize(bytes);
    }

    public Task RemoveAsync(string key) => RemoveAsync(key, CancellationToken.None);

    public async Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.UserSessions WHERE KeyHash = @keyHash",
            new { keyHash = Hash(key) },
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Ends all of a user's sessions, except the one named by <paramref name="exceptKey"/> when given.
    /// Returns how many were ended.
    /// </summary>
    public async Task<int> RemoveForUserAsync(Guid userId, string? exceptKey, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.UserSessions WHERE UserId = @userId AND (@exceptHash IS NULL OR KeyHash <> @exceptHash)",
            new { userId, exceptHash = exceptKey is null ? null : Hash(exceptKey) },
            cancellationToken: cancellationToken));
    }

    /// <summary>Deletes sessions that have expired. They are already refused; this only tidies the table.</summary>
    public async Task<int> RemoveExpiredAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.UserSessions WHERE ExpiresAt <= @now",
            new { now = timeProvider.GetUtcNow() },
            cancellationToken: cancellationToken));
    }

    private static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    private static byte[] Serialize(AuthenticationTicket ticket) => TicketSerializer.Default.Serialize(ticket);

    private static DateTimeOffset ExpiresAt(AuthenticationTicket ticket, DateTimeOffset now) =>
        ticket.Properties.ExpiresUtc ?? now + FallbackLifetime;

    private static Guid? UserIdOf(AuthenticationTicket ticket) =>
        Guid.TryParse(ticket.Principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
