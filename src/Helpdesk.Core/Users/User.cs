namespace Helpdesk.Core.Users;

public class User
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The sign-in name. There is no separate user name.</summary>
    public string Email { get; set; } = "";

    public string NormalizedEmail { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string? PasswordHash { get; set; }

    /// <summary>Changes whenever credentials or access change, which invalidates existing sessions.</summary>
    public string SecurityStamp { get; set; } = "";

    /// <summary>One of <see cref="Roles"/>.</summary>
    public string Role { get; set; } = Roles.Agent;

    /// <summary>A deactivated user cannot sign in and keeps their history.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>The one team the user belongs to, if any. Tickets are assigned within a team.</summary>
    public int? TeamId { get; set; }

    public int AccessFailedCount { get; set; }

    public DateTimeOffset? LockoutEnd { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
