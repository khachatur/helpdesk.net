namespace Helpdesk.Infrastructure.Email;

public enum EmailMode
{
    /// <summary>No mailbox. Nothing is read, and agent replies are stored but not sent.</summary>
    None,

    /// <summary>A Microsoft 365 shared mailbox through Microsoft Graph.</summary>
    Graph,
}

/// <summary>The "Email" configuration section.</summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public EmailMode Mode { get; set; } = EmailMode.None;

    /// <summary>The address of the shared support mailbox.</summary>
    public string Mailbox { get; set; } = "";

    public string TenantId { get; set; } = "";

    public string ClientId { get; set; } = "";

    /// <summary>A secret. Set it with user secrets or an environment variable, never in a settings file.</summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>
    /// On the very first read of the mailbox, only mail received from this moment is turned into tickets.
    /// Defaults to the time of that first read, so existing mail in the inbox is left alone.
    /// </summary>
    public DateTimeOffset? IngestFrom { get; set; }

    public bool IsEnabled => Mode != EmailMode.None;
}
