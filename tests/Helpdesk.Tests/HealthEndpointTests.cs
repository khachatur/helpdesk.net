using System.Net;
using System.Net.Http.Json;
using Helpdesk.Api.Health;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Helpdesk.Tests;

public class HealthEndpointTests(HealthEndpointTests.ApiFactory factory)
    : IClassFixture<HealthEndpointTests.ApiFactory>
{
    [Fact]
    public async Task Liveness_returns_ok_without_a_database()
    {
        var response = await factory.CreateClient().GetAsync("/api/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_reports_unhealthy_when_the_database_is_unreachable()
    {
        var response = await factory.CreateClient().GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.Equal("Unhealthy", body!.Status);
        Assert.Equal("Unhealthy", body.Checks["database"].Status);
    }

    [Fact]
    public async Task Unknown_route_does_not_reveal_itself_to_anonymous_callers()
    {
        // The sign-in requirement applies to unmatched routes too, so an anonymous caller
        // cannot tell which routes exist.
        var response = await factory.CreateClient().GetAsync("/api/does-not-exist");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>Runs the API with no migrations and a connection string that points at nothing.</summary>
    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("BackgroundJobs:Enabled", "false");
            builder.UseSetting(
                "ConnectionStrings:Helpdesk",
                "Server=127.0.0.1,1;Database=Helpdesk;User Id=nobody;Password=nothing;Connect Timeout=1;Encrypt=False");
        }
    }
}
