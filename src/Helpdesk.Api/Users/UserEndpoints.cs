using System.Security.Claims;
using Helpdesk.Api.Auth;
using Helpdesk.Core.Users;
using Helpdesk.Infrastructure.Identity;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;

namespace Helpdesk.Api.Users;

/// <summary>Agent management. Admin only.</summary>
public static class UserEndpoints
{
    public sealed record CreateAgentRequest(string? Email, string? DisplayName, string? Password);

    public sealed record SetPasswordRequest(string? Password);

    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/users").WithTags("Users").RequireAuthorization(Policies.Admin);

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAgentAsync);
        group.MapPost("/{id:guid}/activate", (Guid id, UserManager<User> users, ClaimsPrincipal caller) =>
            SetActiveAsync(id, isActive: true, users, caller));
        group.MapPost("/{id:guid}/deactivate", (Guid id, UserManager<User> users, ClaimsPrincipal caller) =>
            SetActiveAsync(id, isActive: false, users, caller));
        group.MapPost("/{id:guid}/password", SetPasswordAsync);

        return routes;
    }

    private static async Task<Ok<IEnumerable<UserResponse>>> ListAsync(
        UserQueries queries, CancellationToken cancellationToken)
    {
        var users = await queries.ListAsync(cancellationToken);
        return TypedResults.Ok(users.Select(UserResponse.From));
    }

    private static async Task<Results<Created<UserResponse>, ValidationProblem>> CreateAgentAsync(
        CreateAgentRequest request, UserManager<User> userManager)
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

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var user = new User { Email = email, DisplayName = displayName, Role = Roles.Agent };
        var result = await userManager.CreateAsync(user, request.Password!);

        return result.Succeeded
            ? TypedResults.Created($"/api/users/{user.Id}", UserResponse.From(user))
            : TypedResults.ValidationProblem(result.Errors.ToValidationErrors());
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> SetActiveAsync(
        Guid id, bool isActive, UserManager<User> userManager, ClaimsPrincipal caller)
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

        return result.Succeeded ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem>> SetPasswordAsync(
        Guid id, SetPasswordRequest request, UserManager<User> userManager)
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

        return result.Succeeded ? TypedResults.NoContent() : TypedResults.NotFound();
    }
}
