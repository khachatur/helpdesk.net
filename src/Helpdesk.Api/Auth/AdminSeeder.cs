using Helpdesk.Core.Users;
using Helpdesk.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace Helpdesk.Api.Auth;

/// <summary>
/// Creates the first admin from the Seed:Admin configuration section when no admin exists yet.
/// Once an admin exists the section is ignored, so changing it never changes an existing account.
/// </summary>
public static class AdminSeeder
{
    public static async Task SeedAdminAsync(this WebApplication app)
    {
        var section = app.Configuration.GetSection("Seed:Admin");
        var email = section["Email"];
        if (string.IsNullOrWhiteSpace(email))
        {
            return;
        }

        await using var scope = app.Services.CreateAsyncScope();
        var queries = scope.ServiceProvider.GetRequiredService<UserQueries>();
        if (await queries.AnyAdminAsync(CancellationToken.None))
        {
            return;
        }

        var password = section["Password"];
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                "No admin account exists and Seed:Admin:Password is not set. Set it with " +
                "'dotnet user-secrets set \"Seed:Admin:Password\" \"<password>\" --project src/Helpdesk.Api' " +
                "or the Seed__Admin__Password environment variable.");
        }

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var admin = new User
        {
            Email = email,
            DisplayName = section["DisplayName"] ?? "Administrator",
            Role = Roles.Admin,
        };

        var result = await userManager.CreateAsync(admin, password);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                "Could not create the admin account: " + string.Join(" ", result.Errors.Select(e => e.Description)));
        }

        app.Logger.LogInformation("Created the admin account {Email}", email);
    }
}
