using System.Net;
using System.Net.Http.Json;

namespace Helpdesk.Tests;

[Collection(ApiCollection.Name)]
public class AuthTests(AuthApiFactory factory)
{
    [Fact]
    public async Task Seeded_admin_can_sign_in_and_is_an_admin()
    {
        var admin = await factory.SignInAsAdminAsync();

        var me = await admin.GetFromJsonAsync<UserDto>("/api/auth/me");

        Assert.Equal(AuthApiFactory.AdminEmail, me!.Email);
        Assert.Equal("Admin", me.Role);
        Assert.True(me.IsActive);
    }

    [Fact]
    public async Task Email_is_matched_case_insensitively()
    {
        var client = factory.CreateBrowser();

        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email = AuthApiFactory.AdminEmail.ToUpperInvariant(),
            password = AuthApiFactory.AdminPassword,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(AuthApiFactory.AdminEmail, "wrong-password")]
    [InlineData("nobody@helpdesk.test", AuthApiFactory.AdminPassword)]
    [InlineData("", "")]
    public async Task Bad_credentials_are_rejected_with_the_same_answer(string email, string password)
    {
        var client = factory.CreateBrowser();

        var response = await client.PostAsJsonAsync("/api/auth/login", new { email, password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("The email or password is incorrect.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Endpoints_require_sign_in_by_default()
    {
        var anonymous = factory.CreateBrowser();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/users")).StatusCode);
    }

    [Fact]
    public async Task Unknown_route_returns_not_found_problem_details_when_signed_in()
    {
        var admin = await factory.SignInAsAdminAsync();

        var response = await admin.GetAsync("/api/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Sign_out_ends_the_session()
    {
        var admin = await factory.SignInAsAdminAsync();

        var signOut = await admin.PostAsync("/api/auth/logout", null);

        Assert.Equal(HttpStatusCode.NoContent, signOut.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Auth_cookie_is_http_only_secure_and_same_site_strict()
    {
        var client = factory.CreateBrowser();

        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email = AuthApiFactory.AdminEmail,
            password = AuthApiFactory.AdminPassword,
        });

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("helpdesk.auth="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Account_locks_after_five_failed_attempts()
    {
        var admin = await factory.SignInAsAdminAsync();
        var agent = await factory.CreateAgentAsync(admin);
        var client = factory.CreateBrowser();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await client.PostAsJsonAsync("/api/auth/login", new { email = agent.Email, password = "wrong-password" });
        }

        var response = await client.PostAsJsonAsync("/api/auth/login", new { email = agent.Email, password = agent.Password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Account locked", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Change_password_rejects_a_wrong_current_password()
    {
        var admin = await factory.SignInAsAdminAsync();
        var agent = await factory.CreateAgentAsync(admin);
        var client = await factory.SignInAsync(agent.Email, agent.Password);

        var response = await client.PostAsJsonAsync("/api/auth/change-password", new
        {
            currentPassword = "not-my-password",
            newPassword = "Brand-New-Pass-2",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDto>();
        Assert.Contains("currentPassword", problem!.Errors.Keys);
    }

    [Fact]
    public async Task Change_password_rejects_a_weak_new_password()
    {
        var admin = await factory.SignInAsAdminAsync();
        var agent = await factory.CreateAgentAsync(admin);
        var client = await factory.SignInAsync(agent.Email, agent.Password);

        var response = await client.PostAsJsonAsync("/api/auth/change-password", new
        {
            currentPassword = agent.Password,
            newPassword = "short",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDto>();
        Assert.Contains("newPassword", problem!.Errors.Keys);
    }

    [Fact]
    public async Task Change_password_keeps_this_session_and_ends_the_others()
    {
        var admin = await factory.SignInAsAdminAsync();
        var agent = await factory.CreateAgentAsync(admin);
        var thisBrowser = await factory.SignInAsync(agent.Email, agent.Password);
        var otherBrowser = await factory.SignInAsync(agent.Email, agent.Password);
        const string newPassword = "Brand-New-Pass-2";

        var response = await thisBrowser.PostAsJsonAsync("/api/auth/change-password", new
        {
            currentPassword = agent.Password,
            newPassword,
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await thisBrowser.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await otherBrowser.GetAsync("/api/auth/me")).StatusCode);

        var oldPassword = await factory.CreateBrowser()
            .PostAsJsonAsync("/api/auth/login", new { email = agent.Email, password = agent.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
        await factory.SignInAsync(agent.Email, newPassword);
    }
}
