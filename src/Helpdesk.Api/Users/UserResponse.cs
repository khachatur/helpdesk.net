using Helpdesk.Core.Users;

namespace Helpdesk.Api.Users;

public sealed record UserResponse(
    Guid Id,
    string Email,
    string DisplayName,
    string Role,
    bool IsActive,
    bool IsLockedOut,
    DateTimeOffset CreatedAt,
    int? TeamId,
    string? TeamName)
{
    /// <param name="teamName">Filled in for lists. Null where only the team id is known.</param>
    public static UserResponse From(User user, string? teamName = null) => new(
        user.Id,
        user.Email,
        user.DisplayName,
        user.Role,
        user.IsActive,
        user.LockoutEnd > DateTimeOffset.UtcNow,
        user.CreatedAt,
        user.TeamId,
        teamName);
}
