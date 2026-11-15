using Helpdesk.Api.Auth;
using Helpdesk.Infrastructure.Dashboard;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Helpdesk.Api.Dashboard;

/// <summary>How the helpdesk is doing. Admin only.</summary>
public static class DashboardEndpoints
{
    public const int DefaultDays = 30;
    public const int MaxDays = 366;

    /// <param name="From">First day of the period.</param>
    /// <param name="To">Last day of the period, included.</param>
    public sealed record DashboardResponse(DateOnly From, DateOnly To, DashboardData Data);

    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/dashboard", GetAsync).WithTags("Dashboard").RequireAuthorization(Policies.Admin);
        return routes;
    }

    /// <summary>
    /// Current figures, plus figures for tickets created in a period of whole UTC days.
    /// Without dates, the period is the last 30 days up to today.
    /// </summary>
    private static async Task<Results<Ok<DashboardResponse>, ValidationProblem>> GetAsync(
        DateOnly? from, DateOnly? to, DashboardQueries queries, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var lastDay = to ?? DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var firstDay = from ?? lastDay.AddDays(-(DefaultDays - 1));

        if (firstDay > lastDay)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["from"] = ["The first day must not be after the last day."],
            });
        }

        if (lastDay.DayNumber - firstDay.DayNumber >= MaxDays)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["from"] = [$"The period can be at most {MaxDays} days."],
            });
        }

        var data = await queries.GetAsync(
            new DateTimeOffset(firstDay.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            new DateTimeOffset(lastDay.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            cancellationToken);

        return TypedResults.Ok(new DashboardResponse(firstDay, lastDay, data));
    }
}
