namespace Helpdesk.Core.Users;

public static class Roles
{
    /// <summary>Created when the system is deployed. Manages agents.</summary>
    public const string Admin = "Admin";

    /// <summary>Created by the admin. Works on tickets.</summary>
    public const string Agent = "Agent";
}
