using System.Security.Claims;
using Helpdesk.Api.Auth;
using Helpdesk.Core.Users;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Teams;
using Helpdesk.Infrastructure.Tickets;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;

namespace Helpdesk.Api.Users;

/// <summary>Agent management. Admin only.</summary>
public static class UserEndpoints
{
    public sealed record CreateAgentRequest(string? Email, string? DisplayName, string? Password, int? TeamId = null);

    public sealed record SetTeamRequest(int? TeamId);

    public sealed record SetPasswordRequest(string? Password);

    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/users").WithTags("Users").RequireAuthorization(Policies.Admin);

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAgentAsync);
        group.MapPost("/{id:guid}/activate",
            (Guid id, UserManager<User> users, ClaimsPrincipal caller, TicketStore tickets, DatabaseTicketStore sessions, CancellationToken cancellationToken) =>
                SetActiveAsync(id, isActive: true, users, caller, tickets, sessions, cancellationToken));
        group.MapPost("/{id:guid}/deactivate",
            (Guid id, UserManager<User> users, ClaimsPrincipal caller, TicketStore tickets, DatabaseTicketStore sessions, CancellationToken cancellationToken) =>
                SetActiveAsync(id, isActive: false, users, caller, tickets, sessions, cancellationToken));
        group.MapPut("/{id:guid}/team", SetTeamAsync);
        group.MapPost("/{id:guid}/password", SetPasswordAsync);

        return routes;
    }

    private static async Task<Ok<IEnumerable<UserResponse>>> ListAsync(
        UserQueries queries, CancellationToken cancellationToken)
    {
        var users = await queries.ListAsync(cancellationToken);
        return TypedResults.Ok(users.Select(user => UserResponse.From(user, user.TeamName)));
    }

    private static async Task<Results<Created<UserResponse>, ValidationProblem>> CreateAgentAsync(
        CreateAgentRequest request,
        UserManager<User> userManager,
        TeamStore teams,
        TicketStore tickets,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var email = request.Email?.Trim() ?? "";
        var displayName = request.DisplayName?.Trim() ?? "";

        if (email.Length == 0)
        {
            errors["email"] = ["Enter an email address."];
        }
        else if (email.Length > 256)
        {
            errors["email"] = ["The email address is too long."];
        }

        if (displayName.Length == 0)
        {
            errors["displayName"] = ["Enter a name."];
        }
        else if (displayName.Length > 200)
        {
            errors["displayName"] = ["The name must be 200 characters or fewer."];
        }

        if (string.IsNullOrEmpty(request.Password))
        {
            errors["password"] = ["Enter an initial password."];
        }

        if (request.TeamId is { } teamId && !await teams.ExistsAsync(teamId, cancellationToken))
        {
            errors["teamId"] = ["Choose an existing team."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var user = new User { Email = email, DisplayName = displayName, Role = Roles.Agent, TeamId = request.TeamId };
        var result = await userManager.CreateAsync(user, request.Password!);

        // A team that had nobody active may have tickets waiting for an owner.
        if (result.Succeeded && user.TeamId is { } joined)
        {
            await tickets.AssignUnassignedAsync(joined, cancellationToken);
        }

        return result.Succeeded
            ? TypedResults.Created($"/api/users/{user.Id}", UserResponse.From(user))
            : TypedResults.ValidationProblem(result.Errors.ToValidationErrors());
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> SetActiveAsync(
        Guid id,
        bool isActive,
        UserManager<User> userManager,
        ClaimsPrincipal caller,
        TicketStore tickets,
        DatabaseTicketStore sessions,
        CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return TypedResults.NotFound();
        }

        if (!isActive && userManager.GetUserId(caller) == user.Id.ToString())
        {
            // Also guarantees the system never ends up with no active admin.
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Cannot deactivate your own account",
                detail: "Ask another admin to deactivate this account.");
        }

        user.IsActive = isActive;
        // A new security stamp ends the user's existing sessions. This call also saves the user.
        var result = await userManager.UpdateSecurityStampAsync(user);
        if (!result.Succeeded)
        {
            return TypedResults.NotFound();
        }

        if (!isActive)
        {
            // Their open tickets go to other members of each ticket's team.
            // Signed out everywhere at once, not just at the next security-stamp check.
            await sessions.RemoveForUserAsync(user.Id, exceptKey: null, cancellationToken);
            await tickets.ReassignFromUserAsync(user.Id, keepInTeamId: null, cancellationToken);
        }
        else if (user.TeamId is { } teamId)
        {
            await tickets.AssignUnassignedAsync(teamId, cancellationToken);
        }

        return TypedResults.NoContent();
    }

    /// <summary>Moves a user to a team, or out of every team. Tickets they owned in the team they left are handed on.</summary>
    private static async Task<Results<NoContent, NotFound, ValidationProblem>> SetTeamAsync(
        Guid id,
        SetTeamRequest request,
        UserManager<User> userManager,
        TeamStore teams,
        TicketStore tickets,
        CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return TypedResults.NotFound();
        }

        if (request.TeamId is { } teamId && !await teams.ExistsAsync(teamId, cancellationToken))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["teamId"] = ["Choose an existing team."] });
        }

        if (user.TeamId == request.TeamId)
        {
            return TypedResults.NoContent();
        }

        user.TeamId = request.TeamId;
        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            return TypedResults.NotFound();
        }

        await tickets.ReassignFromUserAsync(user.Id, keepInTeamId: request.TeamId, cancellationToken);
        if (request.TeamId is { } joined && user.IsActive)
        {
            await tickets.AssignUnassignedAsync(joined, cancellationToken);
        }

        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem>> SetPasswordAsync(
        Guid id,
        SetPasswordRequest request,
        UserManager<User> userManager,
        DatabaseTicketStore sessions,
        CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return TypedResults.NotFound();
        }

        var password = request.Password ?? "";
        var errors = new List<IdentityError>();
        foreach (var validator in userManager.PasswordValidators)
        {
            var validation = await validator.ValidateAsync(userManager, user, password);
            errors.AddRange(validation.Errors);
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors.ToValidationErrors());
        }

        user.PasswordHash = userManager.PasswordHasher.HashPassword(user, password);
        // A reset also clears a lockout, since a locked-out agent is the usual reason for one.
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        var result = await userManager.UpdateSecurityStampAsync(user);
        if (!result.Succeeded)
        {
            return TypedResults.NotFound();
        }

        // Whoever knew the old password is signed out everywhere.
        await sessions.RemoveForUserAsync(user.Id, exceptKey: null, cancellationToken);
        return TypedResults.NoContent();
    }
}
