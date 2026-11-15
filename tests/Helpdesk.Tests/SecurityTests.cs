using System.Net;
using System.Net.Http.Json;
using Helpdesk.Api.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Tests;

[Collection(ApiCollection.Name)]
public class SecurityTests(AuthApiFactory factory)
{
    private sealed record Route(string Pattern, IReadOnlyList<string> Methods, bool Anonymous, IReadOnlyList<string> Policies);

    private List<Route> Routes() =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => new Route(
                "/" + endpoint.RoutePattern.RawText!.TrimStart('/'),
                endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [],
                endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null,
                endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(data => data.Policy ?? "").ToList()))
            .ToList();

    /// <summary>
    /// The authorization review, kept as a test: a new endpoint that is public or admin-only by
    /// accident fails here until this list is updated on purpose.
    /// </summary>
    [Fact]
    public void Only_health_and_sign_in_are_open_to_anonymous_callers()
    {
        var anonymous = Routes().Where(route => route.Anonymous).Select(route => route.Pattern).Order().ToList();

        Assert.Equal(["/api/auth/login", "/api/health", "/api/health/live"], anonymous);
    }

    [Fact]
    public void Admin_only_endpoints_carry_the_admin_policy()
    {
        var routes = Routes();
        Assert.True(routes.Count > 25, "Expected the application's endpoints to be listed.");

        bool MustBeAdminOnly(Route route) =>
            route.Pattern.StartsWith("/api/users", StringComparison.Ordinal)
            || route.Pattern.StartsWith("/api/dashboard", StringComparison.Ordinal)
            || route.Pattern.StartsWith("/api/system", StringComparison.Ordinal)
            || (route.Pattern.StartsWith("/api/teams", StringComparison.Ordinal) && !route.Methods.Contains("GET"));

        var adminOnly = routes.Where(route => route.Policies.Contains("Admin")).ToList();
        var shouldBe = routes.Where(MustBeAdminOnly).ToList();

        Assert.NotEmpty(shouldBe);
        Assert.Equal(
            shouldBe.Select(route => $"{string.Join(',', route.Methods)} {route.Pattern}").Order(),
            adminOnly.Select(route => $"{string.Join(',', route.Methods)} {route.Pattern}").Order());
    }

    [Fact]
    public async Task Every_endpoint_that_is_not_public_rejects_an_anonymous_caller()
    {
        var anonymous = factory.CreateBrowser();

        foreach (var route in Routes().Where(route => !route.Anonymous))
        {
            // Route parameters are filled with a harmless value; authorization runs before binding.
            var path = System.Text.RegularExpressions.Regex.Replace(route.Pattern, @"\{[^}]+\}", "1");
            var method = new HttpMethod(route.Methods.FirstOrDefault() ?? "GET");

            var response = await anonymous.SendAsync(new HttpRequestMessage(method, path));

            Assert.True(
                response.StatusCode == HttpStatusCode.Unauthorized,
                $"{method} {path} answered {(int)response.StatusCode} to an anonymous caller.");
        }
    }

    [Fact]
    public async Task Responses_carry_security_headers()
    {
        var response = await factory.CreateBrowser().GetAsync("/api/health/live");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("same-origin", response.Headers.GetValues("Referrer-Policy").Single());
        var policy = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("script-src 'self'", policy);
        Assert.Contains("frame-ancestors 'none'", policy);
        Assert.DoesNotContain("unsafe-eval", policy);
    }

    [Fact]
    public async Task System_status_is_for_admins_and_reports_each_part()
    {
        var admin = await factory.SignInAsAdminAsync();
        var agent = await factory.CreateAgentAsync(admin);
        var agentClient = await factory.SignInAsync(agent.Email, agent.Password);

        Assert.Equal(HttpStatusCode.Forbidden, (await agentClient.GetAsync("/api/system/status")).StatusCode);
        var status = await admin.GetFromJsonAsync<StatusDto>("/api/system/status");

        // The test host runs without background jobs, with the fake mailbox and the fake AI.
        Assert.False(status!.BackgroundJobs.Enabled);
        Assert.True(status.Email.Enabled);
        Assert.Equal(FakeMailbox.Address, status.Email.Mailbox);
        Assert.True(status.Ai.Enabled);
        Assert.Equal("Off", status.Ai.AutoSendMode);
    }

    private sealed record StatusDto(bool Healthy, JobsDto BackgroundJobs, EmailDto Email, AiDto Ai);

    private sealed record JobsDto(bool Enabled, bool Healthy, long Failed);

    private sealed record EmailDto(bool Enabled, bool Healthy, string? Mailbox, DateTimeOffset? LastReadAt, int RepliesFailedToSend);

    private sealed record AiDto(bool Enabled, bool Healthy, string AutoSendMode, int CallsLastHour, int FailedLastHour);
}

/// <summary>Sign-in rate limiting, on its own host so the limit can be low.</summary>
[Collection(ApiCollection.Name)]
public class SignInRateLimitTests(AuthApiFactory shared, SignInRateLimitTests.LimitedFactory factory)
    : IClassFixture<SignInRateLimitTests.LimitedFactory>
{
    [Fact]
    public async Task Sign_in_attempts_beyond_the_limit_are_refused_for_a_while()
    {
        // The shared fixture has migrated the database this host connects to.
        Assert.NotNull(shared);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var attempt = new { email = "nobody@helpdesk.test", password = "wrong-password" };

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", attempt)).StatusCode);
        }

        var refused = await client.PostAsJsonAsync("/api/auth/login", attempt);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("application/problem+json", refused.Content.Headers.ContentType?.MediaType);
        Assert.Equal("60", refused.Headers.GetValues("Retry-After").Single());
        Assert.Contains("Too many sign-in attempts", await refused.Content.ReadAsStringAsync());
        // Only sign-in is limited.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health/live")).StatusCode);
    }

    public sealed class LimitedFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Helpdesk", AuthApiFactory.TestConnectionString);
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("BackgroundJobs:Enabled", "false");
            builder.UseSetting("Auth:SignInAttemptsPerMinute", "3");
        }
    }
}

/// <summary>Serving the built web app from the API, against a stand-in build folder.</summary>
public class WebAppHostingTests : IClassFixture<WebAppHostingTests.WebAppFactory>
{
    private readonly HttpClient _client;

    public WebAppHostingTests(WebAppFactory factory) => _client = factory.CreateClient();

    [Theory]
    [InlineData("/")]
    [InlineData("/login")]
    [InlineData("/tickets/1042")]
    [InlineData("/knowledge-base/new")]
    public async Task Any_page_path_gets_the_app_shell_without_signing_in(string path)
    {
        var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<div id=\"root\"></div>", await response.Content.ReadAsStringAsync());
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        Assert.Contains("script-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Hashed_assets_are_served_and_cached_for_good()
    {
        var response = await _client.GetAsync("/assets/app-abc123.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("console.log('app')", await response.Content.ReadAsStringAsync());
        Assert.Contains("immutable", response.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("/api/does-not-exist")]
    [InlineData("/api")]
    [InlineData("/api/tickets")]
    public async Task Api_paths_are_never_answered_with_the_app_shell(string path)
    {
        var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    public sealed class WebAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "helpdesk-webroot-" + Guid.NewGuid().ToString("N"));

        public WebAppFactory()
        {
            Directory.CreateDirectory(Path.Combine(_root, "assets"));
            File.WriteAllText(Path.Combine(_root, "index.html"), "<!doctype html><html><body><div id=\"root\"></div></body></html>");
            File.WriteAllText(Path.Combine(_root, "assets", "app-abc123.js"), "console.log('app')");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Web:Root", _root);
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("BackgroundJobs:Enabled", "false");
            builder.UseSetting(
                "ConnectionStrings:Helpdesk",
                "Server=127.0.0.1,1;Database=Helpdesk;User Id=nobody;Password=nothing;Connect Timeout=1;Encrypt=False");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp folder is harmless.
            }
        }
    }
}
