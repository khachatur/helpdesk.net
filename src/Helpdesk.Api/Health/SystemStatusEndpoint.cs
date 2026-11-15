using Dapper;
using Hangfire;
using Helpdesk.Api.Auth;
using Helpdesk.Core.Tickets;
using Helpdesk.Infrastructure.Ai;
using Helpdesk.Infrastructure.Data;
using Helpdesk.Infrastructure.Email;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace Helpdesk.Api.Health;

/// <summary>
/// The detailed state of the moving parts, for admins and for whatever monitors the system:
/// is mail being read, are background jobs failing, is the AI erroring. Each part says whether
/// it is healthy, so an alert only has to watch the "healthy" flags.
/// </summary>
public static class SystemStatusEndpoint
{
    /// <summary>The mailbox is read every minute; this long without a read means polling has stalled.</summary>
    public static readonly TimeSpan MailboxStalledAfter = TimeSpan.FromMinutes(10);

    /// <summary>More than this share of AI calls failing in the last hour is unhealthy.</summary>
    public const double AiFailureRateLimit = 0.25;

    public sealed record SystemStatusResponse(
        bool Healthy, JobsStatus BackgroundJobs, EmailStatus Email, AiStatus Ai);

    /// <param name="Failed">Jobs that used up their retries and need someone to look at them.</param>
    public sealed record JobsStatus(bool Enabled, bool Healthy, long Failed, long Queued, int Servers);

    /// <param name="LastReadAt">When the mailbox was last read successfully. Null if it never has been.</param>
    public sealed record EmailStatus(bool Enabled, bool Healthy, string? Mailbox, DateTimeOffset? LastReadAt, int RepliesFailedToSend);

    public sealed record AiStatus(
        bool Enabled, bool Healthy, AutoSendMode AutoSendMode, int CallsLastHour, int FailedLastHour);

    public static IEndpointRouteBuilder MapSystemStatusEndpoint(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/system/status", GetAsync).WithTags("System").RequireAuthorization(Policies.Admin);
        return routes;
    }

    private static async Task<Ok<SystemStatusResponse>> GetAsync(
        IConfiguration configuration,
        IOptions<EmailOptions> email,
        IOptions<AiOptions> ai,
        IDbConnectionFactory connectionFactory,
        IServiceProvider services,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        var data = await connection.QuerySingleAsync<(DateTimeOffset? LastReadAt, int FailedSends, int AiCalls, int AiFailures)>(
            new CommandDefinition(
                """
                SELECT
                    (SELECT MAX(UpdatedAt) FROM dbo.MailboxState WHERE Mailbox = @mailbox),
                    (SELECT COUNT(*) FROM dbo.TicketMessages WHERE SendStatus = N'Failed'),
                    (SELECT COUNT(*) FROM dbo.AiRuns WHERE CreatedAt >= @hourAgo),
                    (SELECT COUNT(*) FROM dbo.AiRuns WHERE CreatedAt >= @hourAgo AND Outcome = N'Failed')
                """,
                new { mailbox = email.Value.Mailbox, hourAgo = now.AddHours(-1) },
                cancellationToken: cancellationToken));

        var jobs = new JobsStatus(Enabled: false, Healthy: true, 0, 0, 0);
        if (Jobs.BackgroundJobs.AreEnabled(configuration))
        {
            var statistics = services.GetRequiredService<JobStorage>().GetMonitoringApi().GetStatistics();
            jobs = new JobsStatus(
                Enabled: true,
                Healthy: statistics.Failed == 0 && statistics.Servers > 0,
                statistics.Failed,
                statistics.Enqueued,
                (int)statistics.Servers);
        }

        var emailEnabled = email.Value.IsEnabled;
        var emailStatus = new EmailStatus(
            emailEnabled,
            // Not yet read at all is fine for the first minutes after switching email on.
            Healthy: !emailEnabled || (data.FailedSends == 0 && (data.LastReadAt is null || now - data.LastReadAt < MailboxStalledAfter)),
            emailEnabled ? email.Value.Mailbox : null,
            data.LastReadAt,
            data.FailedSends);

        var aiEnabled = ai.Value.IsEnabled;
        var aiStatus = new AiStatus(
            aiEnabled,
            Healthy: !aiEnabled || data.AiCalls == 0 || (double)data.AiFailures / data.AiCalls <= AiFailureRateLimit,
            ai.Value.AutoSendMode,
            data.AiCalls,
            data.AiFailures);

        return TypedResults.Ok(new SystemStatusResponse(
            jobs.Healthy && emailStatus.Healthy && aiStatus.Healthy, jobs, emailStatus, aiStatus));
    }
}
