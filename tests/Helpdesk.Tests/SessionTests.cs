using System.Net;
using System.Net.Http.Json;
using Dapper;
using Helpdesk.Infrastructure.Data;
using Helpdesk.Infrastructure.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Tests;

/// <summary>
/// Sign-in sessions are kept in the database. These tests hold the session cookie by hand, the way
/// someone who copied it would, to show the server can end a session the cookie still names.
/// </summary>
[Collection(ApiCollection.Name)]
public class SessionTests(AuthApiFactory factory)
{
    private HttpClient RawClient() => factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        HandleCookies = false,
    });

    /// <summary>Signs in and returns the session cookie as a "name=value" pair.</summary>
    private async Task<string> SignInAsync(string email, string password)
    {
        var response = await RawClient().PostAsJsonAsync("/api/auth/login", new { email, password });
        response.EnsureSuccessStatusCode();
        return response.Headers.GetValues("Set-Cookie").Single(cookie => cookie.StartsWith("helpdesk.auth=")).Split(';')[0];
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string cookie)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", cookie);
        return await RawClient().SendAsync(request);
    }

    private int SessionCount(Guid userId)
    {
        using var connection = factory.Services.GetRequiredService<IDbConnectionFactory>().OpenAsync().GetAwaiter().GetResult();
        return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.UserSessions WHERE UserId = @userId", new { userId });
    }

    private async Task<(Guid Id, string Email, string Password)> NewAgentAsync() =>
        await factory.CreateAgentAsync(await factory.SignInAsAdminAsync());

    [Fact]
    public async Task Signing_in_creates_a_session_row_holding_only_a_hash_of_the_id()
    {
        var agent = await NewAgentAsync();

        var cookie = await SignInAsync(agent.Email, agent.Password);

        Assert.Equal(1, SessionCount(agent.Id));
        using var connection = await factory.Services.GetRequiredService<IDbConnectionFactory>().OpenAsync();
        var row = connection.QuerySingle<(byte[] KeyHash, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt)>(
            "SELECT KeyHash, CreatedAt, ExpiresAt FROM dbo.UserSessions WHERE UserId = @Id", new { agent.Id });
        Assert.Equal(32, row.KeyHash.Length);
        // Eight hours, as the cookie settings say.
        Assert.InRange(row.ExpiresAt - row.CreatedAt, TimeSpan.FromHours(7.9), TimeSpan.FromHours(8.1));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/api/auth/me", cookie)).StatusCode);
    }

    [Fact]
    public async Task A_copied_cookie_stops_working_once_its_owner_signs_out()
    {
        var agent = await NewAgentAsync();
        var cookie = await SignInAsync(agent.Email, agent.Password);

        var signOut = await SendAsync(HttpMethod.Post, "/api/auth/logout", cookie);

        Assert.Equal(HttpStatusCode.NoContent, signOut.StatusCode);
        Assert.Equal(0, SessionCount(agent.Id));
        // The same cookie, replayed. With the session only in the cookie this would still be accepted.
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/api/auth/me", cookie)).StatusCode);
    }

    [Fact]
    public async Task Each_sign_in_is_its_own_session_and_signing_out_ends_only_that_one()
    {
        var agent = await NewAgentAsync();
        var laptop = await SignInAsync(agent.Email, agent.Password);
        var phone = await SignInAsync(agent.Email, agent.Password);
        Assert.Equal(2, SessionCount(agent.Id));

        await SendAsync(HttpMethod.Post, "/api/auth/logout", laptop);

        Assert.Equal(1, SessionCount(agent.Id));
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/api/auth/me", laptop)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/api/auth/me", phone)).StatusCode);
    }

    [Fact]
    public async Task Deactivating_a_user_deletes_all_their_sessions_at_once()
    {
        var admin = await factory.SignInAsAdminAsync();
        var agent = await factory.CreateAgentAsync(admin);
        var cookie = await SignInAsync(agent.Email, agent.Password);
        await SignInAsync(agent.Email, agent.Password);

        await admin.PostAsync($"/api/users/{agent.Id}/deactivate", null);

        Assert.Equal(0, SessionCount(agent.Id));
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/api/auth/me", cookie)).StatusCode);
    }

    [Fact]
    public async Task An_admin_password_reset_deletes_all_the_users_sessions()
    {
        var admin = await factory.SignInAsAdminAsync();
        var agent = await factory.CreateAgentAsync(admin);
        var cookie = await SignInAsync(agent.Email, agent.Password);

        var response = await admin.PostAsJsonAsync($"/api/users/{agent.Id}/password", new { password = "Reset-By-Admin-9" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, SessionCount(agent.Id));
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/api/auth/me", cookie)).StatusCode);
    }

    [Fact]
    public async Task Changing_your_password_keeps_you_signed_in_here_and_ends_every_other_session()
    {
        var agent = await NewAgentAsync();
        var other = await SignInAsync(agent.Email, agent.Password);
        // This browser handles cookies, so it picks up the fresh session the change hands out.
        var here = await factory.SignInAsync(agent.Email, agent.Password);
        Assert.Equal(2, SessionCount(agent.Id));

        var response = await here.PostAsJsonAsync("/api/auth/change-password", new
        {
            currentPassword = agent.Password,
            newPassword = "Brand-New-Pass-2",
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1, SessionCount(agent.Id));
        Assert.Equal(HttpStatusCode.OK, (await here.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/api/auth/me", other)).StatusCode);
    }

    [Fact]
    public async Task An_expired_session_is_refused_and_then_tidied_away()
    {
        var agent = await NewAgentAsync();
        var cookie = await SignInAsync(agent.Email, agent.Password);
        using (var connection = await factory.Services.GetRequiredService<IDbConnectionFactory>().OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE dbo.UserSessions SET ExpiresAt = DATEADD(MINUTE, -1, SYSDATETIMEOFFSET()) WHERE UserId = @Id", new { agent.Id });
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/api/auth/me", cookie)).StatusCode);

        var removed = await factory.Services.GetRequiredService<DatabaseTicketStore>().RemoveExpiredAsync(CancellationToken.None);

        Assert.True(removed >= 1);
        Assert.Equal(0, SessionCount(agent.Id));
    }

    [Fact]
    public async Task A_made_up_session_cookie_is_refused()
    {
        var response = await SendAsync(HttpMethod.Get, "/api/auth/me", "helpdesk.auth=not-a-real-session");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
