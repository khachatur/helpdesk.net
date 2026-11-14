using Helpdesk.Api.Auth;
using Helpdesk.Core.Tickets;
using Helpdesk.Infrastructure.Teams;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Helpdesk.Api.Teams;

/// <summary>Teams and category routing. Everyone can list teams; only an admin can change them.</summary>
public static class TeamEndpoints
{
    public sealed record TeamResponse(
        int Id, string Name, bool IsDefault, IReadOnlyList<TicketCategory> Categories, int MemberCount, int ActiveMemberCount)
    {
        public static TeamResponse From(Team team) =>
            new(team.Id, team.Name, team.IsDefault, team.Categories, team.MemberCount, team.ActiveMemberCount);
    }

    public sealed record CreateTeamRequest(string? Name);

    public sealed record UpdateTeamRequest(string? Name, TicketCategory[]? Categories, bool IsDefault);

    public sealed record TeamCreatedResponse(int Id);

    public static IEndpointRouteBuilder MapTeamEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/teams").WithTags("Teams");

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync).RequireAuthorization(Policies.Admin);
        group.MapPut("/{id:int}", UpdateAsync).RequireAuthorization(Policies.Admin);
        group.MapDelete("/{id:int}", DeleteAsync).RequireAuthorization(Policies.Admin);

        return routes;
    }

    private static async Task<Ok<IEnumerable<TeamResponse>>> ListAsync(TeamStore store, CancellationToken cancellationToken) =>
        TypedResults.Ok((await store.ListAsync(cancellationToken)).Select(TeamResponse.From));

    private static async Task<Results<Created<TeamCreatedResponse>, ValidationProblem>> CreateAsync(
        CreateTeamRequest request, TeamStore store, CancellationToken cancellationToken)
    {
        if (ValidateName(request.Name, out var name) is { } invalid)
        {
            return invalid;
        }

        var (result, id) = await store.CreateAsync(name, cancellationToken);
        return result == TeamResult.Ok
            ? TypedResults.Created($"/api/teams/{id}", new TeamCreatedResponse(id!.Value))
            : NameTaken();
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        int id, UpdateTeamRequest request, TeamStore store, CancellationToken cancellationToken)
    {
        if (ValidateName(request.Name, out var name) is { } invalid)
        {
            return invalid;
        }

        var result = await store.UpdateAsync(id, name, request.Categories ?? [], request.IsDefault, cancellationToken);
        return result switch
        {
            TeamResult.Ok => TypedResults.NoContent(),
            TeamResult.NotFound => TypedResults.NotFound(),
            TeamResult.NameTaken => NameTaken(),
            _ => TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "A default team is required",
                detail: "Make another team the default first. Tickets with no category go to the default team."),
        };
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> DeleteAsync(
        int id, TeamStore store, CancellationToken cancellationToken)
    {
        var result = await store.DeleteAsync(id, cancellationToken);
        return result switch
        {
            TeamResult.Ok => TypedResults.NoContent(),
            TeamResult.NotFound => TypedResults.NotFound(),
            _ => TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Team is in use",
                detail: "A team can be deleted only when it is not the default and has no members, no tickets, and no categories routed to it."),
        };
    }

    private static ValidationProblem? ValidateName(string? value, out string name)
    {
        name = value?.Trim() ?? "";
        if (name.Length == 0)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Enter a team name."] });
        }

        return name.Length > 100
            ? TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["The name must be 100 characters or fewer."] })
            : null;
    }

    private static ValidationProblem NameTaken() =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["A team with this name already exists."] });
}
