using Helpdesk.Api.Users;
using Helpdesk.Core.Users;
using Microsoft.AspNetCore.Http.HttpResults;
using Helpdesk.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace Helpdesk.Api.Auth;

public static class AuthEndpoints
{
    public sealed record LoginRequest(string? Email, string? Password);

    public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/login", LoginAsync).AllowAnonymous().RequireRateLimiting(Hosting.WebHosting.SignInRateLimit);
        group.MapPost("/logout", LogoutAsync);
        group.MapGet("/me", MeAsync);
        group.MapPost("/change-password", ChangePasswordAsync);

        return routes;
    }

    private static async Task<Results<Ok<UserResponse>, ProblemHttpResult>> LoginAsync(
        LoginRequest request, UserManager<User> userManager, SignInManager<User> signInManager)
    {
        // The same answer for an unknown email, a wrong password, and a deactivated account,
        // so the response does not reveal which accounts exist.
        var failed = TypedResults.Problem(
            statusCode: StatusCodes.Status401Unauthorized,
            title: "Sign-in failed",
            detail: "The email or password is incorrect.");

        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password))
        {
            return failed;
        }

        var user = await userManager.FindByEmailAsync(request.Email.Trim());
        if (user is null || !user.IsActive)
        {
            return failed;
        }

        var result = await signInManager.PasswordSignInAsync(
            user, request.Password, isPersistent: false, lockoutOnFailure: true);

        if (result.IsLockedOut)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Account locked",
                detail: "Too many failed sign-in attempts. Try again in 15 minutes, or ask an admin to reset your password.");
        }

        return result.Succeeded ? TypedResults.Ok(UserResponse.From(user)) : failed;
    }

    private static async Task<NoContent> LogoutAsync(SignInManager<User> signInManager)
    {
        await signInManager.SignOutAsync();
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<UserResponse>, UnauthorizedHttpResult>> MeAsync(
        HttpContext context, UserManager<User> userManager, SignInManager<User> signInManager)
    {
        var user = await userManager.GetUserAsync(context.User);
        if (user is null || !user.IsActive)
        {
            await signInManager.SignOutAsync();
            return TypedResults.Unauthorized();
        }

        return TypedResults.Ok(UserResponse.From(user));
    }

    private static async Task<Results<NoContent, ValidationProblem, UnauthorizedHttpResult>> ChangePasswordAsync(
        ChangePasswordRequest request,
        HttpContext context,
        UserManager<User> userManager,
        SignInManager<User> signInManager,
        DatabaseTicketStore sessions,
        CancellationToken cancellationToken)
    {
        var user = await userManager.GetUserAsync(context.User);
        if (user is null || !user.IsActive)
        {
            return TypedResults.Unauthorized();
        }

        var result = await userManager.ChangePasswordAsync(
            user, request.CurrentPassword ?? "", request.NewPassword ?? "");
        if (!result.Succeeded)
        {
            return TypedResults.ValidationProblem(result.Errors.ToValidationErrors(passwordField: "newPassword"));
        }

        // This browser's session is brought up to date and kept; every other session of the user is
        // ended, so anyone still signed in with the old password is out at once.
        await signInManager.RefreshSignInAsync(user);
        await sessions.RemoveForUserAsync(user.Id, DatabaseTicketStore.CurrentKey(context), cancellationToken);
        return TypedResults.NoContent();
    }
}
