using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Helpdesk.Api.Health;

public static class HealthResponseWriter
{
    /// <summary>Writes the health report as JSON. Exception details are logged, never returned.</summary>
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        var response = new HealthResponse(
            report.Status.ToString(),
            report.Entries.ToDictionary(
                entry => entry.Key,
                entry => new HealthCheckEntry(entry.Value.Status.ToString(), entry.Value.Description)));

        return context.Response.WriteAsJsonAsync(response);
    }
}

public sealed record HealthResponse(string Status, IReadOnlyDictionary<string, HealthCheckEntry> Checks);

public sealed record HealthCheckEntry(string Status, string? Description);
