namespace Helpdesk.Infrastructure.Email;

/// <summary>The shared support mailbox. The only part of the system that talks to the mail provider.</summary>
public interface IMailbox
{
    /// <summary>
    /// Returns the emails that arrived since the given cursor, and a new cursor to pass next time.
    /// With no cursor, returns emails received at or after <paramref name="since"/>.
    /// </summary>
    Task<MailboxChanges> GetNewMessagesAsync(string? cursor, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>Sends an email from the mailbox, as a reply in an existing thread when one is given.</summary>
    Task<SentEmail> SendAsync(OutgoingEmail email, CancellationToken cancellationToken);
}

public sealed record MailboxChanges(IReadOnlyList<InboundEmail> Messages, string? Cursor);

/// <param name="ProviderMessageId">The provider's id for this message, used to reply to it.</param>
/// <param name="InternetMessageId">The RFC 5322 Message-ID header.</param>
/// <param name="ConversationId">The provider's thread id, shared by every message in a thread.</param>
/// <param name="Headers">Internet message headers, with case-insensitive names.</param>
public sealed record InboundEmail(
    string ProviderMessageId,
    string InternetMessageId,
    string? ConversationId,
    string FromEmail,
    string? FromName,
    string Subject,
    string? BodyHtml,
    string? BodyText,
    DateTimeOffset ReceivedAt,
    IReadOnlyDictionary<string, string> Headers)
{
    /// <summary>The Message-IDs this email says it replies to, from In-Reply-To and References.</summary>
    public IReadOnlyList<string> ReferencedMessageIds()
    {
        var ids = new List<string>();
        foreach (var header in new[] { "In-Reply-To", "References" })
        {
            if (!Headers.TryGetValue(header, out var value))
            {
                continue;
            }

            foreach (System.Text.RegularExpressions.Match match in
                     System.Text.RegularExpressions.Regex.Matches(value, "<[^<>\\s]+>"))
            {
                if (!ids.Contains(match.Value))
                {
                    ids.Add(match.Value);
                }
            }
        }

        return ids;
    }
}

/// <param name="ReplyToProviderMessageId">
/// The message to reply to, which keeps the email in the requester's existing thread.
/// Null starts a new thread.
/// </param>
public sealed record OutgoingEmail(
    string ToEmail,
    string? ToName,
    string Subject,
    string BodyText,
    string? ReplyToProviderMessageId);

public sealed record SentEmail(string? ProviderMessageId, string? InternetMessageId, string? ConversationId);
