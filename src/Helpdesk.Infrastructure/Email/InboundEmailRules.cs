namespace Helpdesk.Infrastructure.Email;

/// <summary>Decides which incoming emails must not become tickets.</summary>
public static class InboundEmailRules
{
    private static readonly string[] BulkPrecedence = ["bulk", "junk", "list", "auto_reply"];
    private static readonly string[] SystemSenders = ["mailer-daemon", "postmaster"];

    /// <summary>
    /// True for emails a machine sent: out-of-office replies, bounces, and mailing-list posts.
    /// Skipping them stops two auto-responders from answering each other forever, and keeps an
    /// out-of-office reply from reopening a resolved ticket.
    /// </summary>
    public static bool IsAutomated(InboundEmail email)
    {
        var headers = email.Headers;

        // RFC 3834: anything other than "no" marks an automatic message.
        if (headers.TryGetValue("Auto-Submitted", out var autoSubmitted)
            && !autoSubmitted.Trim().StartsWith("no", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (headers.TryGetValue("Precedence", out var precedence)
            && BulkPrecedence.Contains(precedence.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (headers.ContainsKey("X-Autoreply") || headers.ContainsKey("X-Autorespond") || headers.ContainsKey("List-Id"))
        {
            return true;
        }

        // Bounces have an empty return path.
        if (headers.TryGetValue("Return-Path", out var returnPath) && returnPath.Trim() == "<>")
        {
            return true;
        }

        var localPart = email.FromEmail.Split('@')[0];
        return SystemSenders.Contains(localPart, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True when the mailbox itself sent the email, which would otherwise loop.</summary>
    public static bool IsFromMailbox(InboundEmail email, string mailbox) =>
        string.Equals(email.FromEmail.Trim(), mailbox.Trim(), StringComparison.OrdinalIgnoreCase);
}
