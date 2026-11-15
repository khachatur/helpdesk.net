using Helpdesk.Api.Auth;
using Helpdesk.Api.Dashboard;
using Helpdesk.Api.Health;
using Helpdesk.Api.Hosting;
using Helpdesk.Api.Jobs;
using Helpdesk.Api.KnowledgeBase;
using System.Text.Json.Serialization;
using Helpdesk.Api.Migrations;
using Helpdesk.Api.Teams;
using Helpdesk.Api.Tickets;
using Helpdesk.Api.Users;
using Helpdesk.Infrastructure;
using Helpdesk.Migrations;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog((services, logger) => logger
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console());

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
// Enums travel as their names ("Open"), never as numbers.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false)));
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHelpdeskAuth(builder.Environment);
builder.Services.AddHelpdeskBackgroundJobs(builder.Configuration);
builder.Services.AddHelpdeskRateLimiting(builder.Configuration);
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

var app = builder.Build();

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    DatabaseMigrator.Migrate(
        app.Configuration.GetRequiredHelpdeskConnectionString(),
        new SerilogUpgradeLog(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Helpdesk.Migrations")));
}

await app.SeedAdminAsync();
await app.SeedSampleTicketsAsync();
await app.SeedSampleArticlesAsync();
await app.SeedSampleDraftsAsync();

// Unhandled exceptions and bare error status codes are returned as RFC 9457 problem details.
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging();
app.UseQuietClientDisconnects();
app.UseSecurityHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

// Before authorization: the pages and their scripts are public, the data behind them is not.
app.UseWebApp();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

// Liveness: the process is up. Readiness: its dependencies (the database) respond.
app.MapHealthChecks("/api/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/api/health", new HealthCheckOptions { ResponseWriter = HealthResponseWriter.WriteAsync })
    .AllowAnonymous();

app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapTicketEndpoints();
app.MapTeamEndpoints();
app.MapArticleEndpoints();
app.MapDashboardEndpoints();
app.MapSystemStatusEndpoint();
app.UseHelpdeskBackgroundJobs();

app.Run();

public partial class Program;
