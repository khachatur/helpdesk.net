using Helpdesk.Core.Users;

namespace Helpdesk.Api.Users;

public sealed record UserResponse(
    Guid Id,
    string Email,
    string DisplayName,
    string Role,
    bool IsActive,
    bool IsLockedOut,
    DateTimeOffset CreatedAt)
{
    public static UserResponse From(User user) => new(
        user.Id,
        user.Email,
        user.DisplayName,
        user.Role,
        user.IsActive,
        user.LockoutEnd > DateTimeOffset.UtcNow,
        user.CreatedAt);
}
