using System.Net.Http.Json;
using Dapper;
using Helpdesk.Core.Tickets;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Tickets;
using Helpdesk.Migrations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Tests;

/// <summary>
/// Runs the API against a real test database: Helpdesk_Test on the local SQL Server by default,
/// or whatever HELPDESK_TEST_CONNECTION points at (CI uses a SQL Server container).
/// The database is migrated and emptied once per test run, then the app seeds a known admin.
/// </summary>
public sealed class AuthApiFactory : WebApplicationFactory<Program>
{
    public const string AdminEmail = "admin@helpdesk.test";
    public const string AdminPassword = "Admin-Test-Pass-1";

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("HELPDESK_TEST_CONNECTION")
        ?? "Server=localhost;Database=Helpdesk_Test;Integrated Security=True;TrustServerCertificate=True";

    public AuthApiFactory()
    {
        DatabaseMigrator.Migrate(ConnectionString);

        using var connection = new SqlConnection(ConnectionString);
        connection.Execute("DELETE FROM dbo.InboundEmails; DELETE FROM dbo.MailboxState; DELETE FROM dbo.TicketEvents; DELETE FROM dbo.TicketMessages; DELETE FROM dbo.Tickets; DELETE FROM dbo.Users;");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Helpdesk", ConnectionString);
        builder.UseSetting("Database:MigrateOnStartup", "false");
        builder.UseSetting("Seed:Admin:Email", AdminEmail);
        builder.UseSetting("Seed:Admin:Password", AdminPassword);
        builder.UseSetting("Seed:Admin:DisplayName", "Test Admin");

        // No Hangfire in tests: replies are queued on a recording queue and tests run the sender themselves.
        builder.UseSetting("BackgroundJobs:Enabled", "false");
        builder.UseSetting("Email:Mode", "Graph");
        builder.UseSetting("Email:Mailbox", FakeMailbox.Address);

        // Re-check the security stamp on every request, so tests see a deactivation immediately
        // instead of after the production one-minute interval.
        builder.ConfigureTestServices(services =>
        {
            services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);
            services.AddSingleton<IMailbox>(Mailbox);
            services.AddSingleton<IReplyQueue>(ReplyQueue);
        });
    }

    /// <summary>A client with its own cookie jar. HTTPS, because the auth cookie is Secure outside development.</summary>
    public HttpClient CreateBrowser() =>
        CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

    public async Task<HttpClient> SignInAsync(string email, string password)
    {
        var client = CreateBrowser();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        response.EnsureSuccessStatusCode();
        return client;
    }

    public Task<HttpClient> SignInAsAdminAsync() => SignInAsync(AdminEmail, AdminPassword);

    public FakeMailbox Mailbox { get; } = new();

    public RecordingReplyQueue ReplyQueue { get; } = new();

    public TicketStore Tickets => Services.GetRequiredService<TicketStore>();

    /// <summary>Creates a ticket the way an incoming email will, and returns its number.</summary>
    public Task<int> CreateTicketAsync(
        string subject = "Test ticket",
        TicketCategory? category = null,
        string requesterEmail = "student@example.com",
        string? bodyHtml = null,
        DateTimeOffset? receivedAt = null) =>
        Tickets.CreateAsync(
            new NewTicket(subject, requesterEmail, "Test Student", "Hello, I need help.", bodyHtml, category, receivedAt),
            CancellationToken.None);

    /// <summary>Creates an active agent with a unique email and returns its credentials and id.</summary>
    public async Task<(Guid Id, string Email, string Password)> CreateAgentAsync(HttpClient admin)
    {
        var email = $"agent-{Guid.NewGuid():N}@helpdesk.test";
        const string password = "Agent-Test-Pass-1";

        var response = await admin.PostAsJsonAsync("/api/users", new { email, displayName = "Test Agent", password });
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<UserDto>();

        return (created!.Id, email, password);
    }
}

public sealed record UserDto(Guid Id, string Email, string DisplayName, string Role, bool IsActive, bool IsLockedOut);

public sealed record ValidationProblemDto(Dictionary<string, string[]> Errors);

/// <summary>Test classes in this collection share one API instance and one test database.</summary>
[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<AuthApiFactory>
{
    public const string Name = "Api";
}
