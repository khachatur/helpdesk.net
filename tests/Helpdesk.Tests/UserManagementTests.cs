using System.Net;
using System.Net.Http.Json;

namespace Helpdesk.Tests;

[Collection(ApiCollection.Name)]
public class UserManagementTests(AuthApiFactory factory)
{
    [Fact]
    public async Task Admin_creates_an_agent_who_can_then_sign_in()
    {
        var admin = await factory.SignInAsAdminAsync();

        var agent = await factory.CreateAgentAsync(admin);
        var client = await factory.SignInAsync(agent.Email, agent.Password);
        var me = await client.GetFromJsonAsync<UserDto>("/api/auth/me");

        Assert.Equal("Agent", me!.Role);
        Assert.Equal("Test Agent", me.DisplayName);
    }

    [Fact]
    public async Task Agent_list_includes_new_agents()
    {
        var admin = await factory.SignInAsAdminAsync();
        var agent = await factory.CreateAgentAsync(admin);

        var users = await admin.GetFromJsonAsync<List<UserDto>>("/api/users");

        Assert.Contains(users!, user => user.Id == agent.Id && user is { Role: "Agent", IsActive: true });
        Assert.Contains(users!, user => user.Email == AuthApiFactory.AdminEmail && user.Role == "Admin");
    }

    [Fact]
    public async Task Agents_cannot_use_agent_management()
    {
        var admin = await factory.SignInAsAdminAsync();
        var agent = await factory.CreateAgentAsync(admin);
        var client = await factory.SignInAsync(agent.Email, agent.Password);

        var list = await client.GetAsync("/api/users");
        var create = await client.PostAsJsonAsync("/api/users", new
        {
            email = "sneaky@helpdesk.test",
            displayName = "Sneaky",
            password = "Sneaky-Pass-123",
        });
        var deactivate = await client.PostAsync($"/api/users/{agent.Id}/deactivate", null);

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, deactivate.StatusCode);
    }

    [Fact]
    public async Task Duplicate_email_is_rejected()
    {
        var admin = await factory.SignInAsAdminAsync();
        var agent = await factory.CreateAgentAsync(admin);

        var response = await admin.PostAsJsonAsync("/api/users", new
        {
            email = agent.Email.ToUpperInvariant(),
            displayName = "Same Person",
            password = "Another-Pass-123",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDto>();
        Assert.Equal(["An account with this email already exists."], problem!.Errors["email"]);
    }

    [Theory]
    [InlineData("", "Name", "Valid-Pass-123", "email")]
    [InlineData("not-an-email", "Name", "Valid-Pass-123", "email")]
    [InlineData("valid@helpdesk.test", "", "Valid-Pass-123", "displayName")]
    [InlineData("valid@helpdesk.test", "Name", "", "password")]
    [InlineData("valid@helpdesk.test", "Name", "short", "password")]
    [InlineData("valid@helpdesk.test", "Name", "alllowercase123", "password")]
    public async Task Invalid_agent_details_are_rejected(string email, string displayName, string password, string field)
    {
        var admin = await factory.SignInAsAdminAsync();

        var response = await admin.PostAsJsonAsync("/api/users", new { email, displayName, password });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDto>();
        Assert.Contains(field, problem!.Errors.Keys);
    }

    [Fact]
    public async Task Deactivating_an_agent_blocks_sign_in_and_ends_their_session()
    {
        var admin = await factory.SignInAsAdminAsync();
        var agent = await factory.CreateAgentAsync(admin);
        var agentSession = await factory.SignInAsync(agent.Email, agent.Password);

        var response = await admin.PostAsync($"/api/users/{agent.Id}/deactivate", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await agentSession.GetAsync("/api/auth/me")).StatusCode);
        var signIn = await factory.CreateBrowser()
            .PostAsJsonAsync("/api/auth/login", new { email = agent.Email, password = agent.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, signIn.StatusCode);
    }

    [Fact]
    public async Task Reactivating_an_agent_lets_them_sign_in_again()
    {
        var admin = await factory.SignInAsAdminAsync();
        var agent = await factory.CreateAgentAsync(admin);
        await admin.PostAsync($"/api/users/{agent.Id}/deactivate", null);

        var response = await admin.PostAsync($"/api/users/{agent.Id}/activate", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await factory.SignInAsync(agent.Email, agent.Password);
    }

    [Fact]
    public async Task Admin_cannot_deactivate_their_own_account()
    {
        var admin = await factory.SignInAsAdminAsync();
        var me = await admin.GetFromJsonAsync<UserDto>("/api/auth/me");

        var response = await admin.PostAsync($"/api/users/{me!.Id}/deactivate", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Unknown_user_returns_not_found()
    {
        var admin = await factory.SignInAsAdminAsync();

        var response = await admin.PostAsync($"/api/users/{Guid.NewGuid()}/deactivate", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Admin_password_reset_replaces_the_password_and_clears_a_lockout()
    {
        var admin = await factory.SignInAsAdminAsync();
        var agent = await factory.CreateAgentAsync(admin);
        var browser = factory.CreateBrowser();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await browser.PostAsJsonAsync("/api/auth/login", new { email = agent.Email, password = "wrong-password" });
        }

        const string newPassword = "Reset-By-Admin-9";
        var response = await admin.PostAsJsonAsync($"/api/users/{agent.Id}/password", new { password = newPassword });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await factory.SignInAsync(agent.Email, newPassword);
    }

    [Fact]
    public async Task Admin_password_reset_rejects_a_weak_password()
    {
        var admin = await factory.SignInAsAdminAsync();
        var agent = await factory.CreateAgentAsync(admin);

        var response = await admin.PostAsJsonAsync($"/api/users/{agent.Id}/password", new { password = "weak" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await factory.SignInAsync(agent.Email, agent.Password);
    }
}
