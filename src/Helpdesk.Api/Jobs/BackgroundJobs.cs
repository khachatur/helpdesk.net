using Hangfire;
using Hangfire.Dashboard;
using Helpdesk.Api.Auth;
using Helpdesk.Core.Users;
using Helpdesk.Infrastructure;
using Helpdesk.Infrastructure.Ai;
using Helpdesk.Infrastructure.Email;
using Microsoft.Extensions.Options;

namespace Helpdesk.Api.Jobs;

/// <summary>
/// Background work runs in Hangfire, with its state in the application database (the HangFire schema,
/// which Hangfire creates itself). Switched off with BackgroundJobs:Enabled = false.
/// </summary>
public static class BackgroundJobs
{
    public const string DashboardPath = "/hangfire";
    private const string PollMailboxJobId = "poll-mailbox";
    private const string RouteStuckTicketsJobId = "route-stuck-tickets";

    public static bool AreEnabled(IConfiguration configuration) =>
        configuration.GetValue("BackgroundJobs:Enabled", false);

    public static IServiceCollection AddHelpdeskBackgroundJobs(this IServiceCollection services, IConfiguration configuration)
    {
        if (!AreEnabled(configuration))
        {
            return services;
        }

        services.AddHangfire(hangfire => hangfire
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseSqlServerStorage(configuration.GetRequiredHelpdeskConnectionString()));
        services.AddHangfireServer();

        services.AddSingleton<IReplyQueue, HangfireReplyQueue>();
        services.AddSingleton<IAiJobQueue, HangfireAiJobQueue>();
        return services;
    }

    public static void UseHelpdeskBackgroundJobs(this WebApplication app)
    {
        if (!AreEnabled(app.Configuration))
        {
            return;
        }

        app.MapHangfireDashboard(DashboardPath, new DashboardOptions
            {
                // The route's policy below does the real check. This replaces Hangfire's default
                // filter, which only lets requests from the local machine through.
                Authorization = [new AdminOnlyDashboardFilter()],
                DashboardTitle = "Helpdesk jobs",
            })
            .RequireAuthorization(Policies.Admin);

        var recurringJobs = app.Services.GetRequiredService<IRecurringJobManager>();
        if (app.Services.GetRequiredService<IOptions<EmailOptions>>().Value.IsEnabled)
        {
            recurringJobs.AddOrUpdate<PollMailboxJob>(PollMailboxJobId, job => job.RunAsync(CancellationToken.None), Cron.Minutely());
        }
        else
        {
            recurringJobs.RemoveIfExists(PollMailboxJobId);
        }

        if (app.Services.GetRequiredService<IOptions<AiOptions>>().Value.IsEnabled)
        {
            recurringJobs.AddOrUpdate<RouteStuckTicketsJob>(RouteStuckTicketsJobId, job => job.RunAsync(CancellationToken.None), Cron.Minutely());
        }
        else
        {
            recurringJobs.RemoveIfExists(RouteStuckTicketsJobId);
        }
    }

    private sealed class AdminOnlyDashboardFilter : IDashboardAuthorizationFilter
    {
        public bool Authorize(DashboardContext context) => context.GetHttpContext().User.IsInRole(Roles.Admin);
    }
}

/// <summary>Reads new mail once a minute.</summary>
public sealed class PollMailboxJob(EmailIngestionService ingestion)
{
    // A poll that overruns the minute must not overlap the next one. A failed poll is not retried:
    // the next scheduled run picks up from the same position.
    [DisableConcurrentExecution(timeoutInSeconds: 5)]
    [AutomaticRetry(Attempts = 0)]
    public Task RunAsync(CancellationToken cancellationToken) => ingestion.PollAsync(cancellationToken);
}

/// <summary>Emails one agent reply. Hangfire retries it with increasing delays while attempts remain.</summary>
public sealed class SendReplyJob(ReplySender sender)
{
    [AutomaticRetry(Attempts = ReplySender.MaxAttempts)]
    public Task RunAsync(long messageId, CancellationToken cancellationToken) => sender.SendAsync(messageId, cancellationToken);
}

public sealed class HangfireReplyQueue(IBackgroundJobClient jobs) : IReplyQueue
{
    public void Enqueue(long messageId) =>
        jobs.Enqueue<SendReplyJob>(job => job.RunAsync(messageId, CancellationToken.None));
}

/// <summary>Classifies one new ticket. Hangfire retries it while attempts remain; the last failure routes the ticket anyway.</summary>
public sealed class ClassifyTicketJob(TicketClassifier classifier)
{
    [AutomaticRetry(Attempts = TicketClassifier.MaxAttempts)]
    public Task RunAsync(int ticketId, CancellationToken cancellationToken) => classifier.ClassifyAsync(ticketId, cancellationToken);
}

/// <summary>Writes or refreshes one ticket's summary. A summary is a convenience, so it is tried a few times and then given up.</summary>
public sealed class SummarizeTicketJob(TicketSummarizer summarizer)
{
    [AutomaticRetry(Attempts = 3, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public Task RunAsync(int ticketId, CancellationToken cancellationToken) => summarizer.SummarizeAsync(ticketId, cancellationToken);
}

/// <summary>Once a minute, routes tickets whose classification never completed, so none is left outside every queue.</summary>
public sealed class RouteStuckTicketsJob(TicketClassifier classifier)
{
    [DisableConcurrentExecution(timeoutInSeconds: 5)]
    [AutomaticRetry(Attempts = 0)]
    public Task RunAsync(CancellationToken cancellationToken) => classifier.RouteStuckTicketsAsync(cancellationToken);
}

public sealed class HangfireAiJobQueue(IBackgroundJobClient jobs) : IAiJobQueue
{
    public void EnqueueClassification(int ticketId) =>
        jobs.Enqueue<ClassifyTicketJob>(job => job.RunAsync(ticketId, CancellationToken.None));

    public void EnqueueSummary(int ticketId) =>
        jobs.Enqueue<SummarizeTicketJob>(job => job.RunAsync(ticketId, CancellationToken.None));
}
