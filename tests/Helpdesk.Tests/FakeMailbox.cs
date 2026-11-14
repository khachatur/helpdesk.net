using Helpdesk.Infrastructure.Email;

namespace Helpdesk.Tests;

/// <summary>An in-memory mailbox: tests put emails in the inbox and look at what was sent.</summary>
public sealed class FakeMailbox : IMailbox
{
    public const string Address = "support@helpdesk.test";

    private int _reads;

    /// <summary>Emails waiting to be read. A read takes them all.</summary>
    public List<InboundEmail> Inbox { get; } = [];

    public List<OutgoingEmail> Sent { get; } = [];

    /// <summary>The cursor and start time passed to each read.</summary>
    public List<(string? Cursor, DateTimeOffset Since)> Reads { get; } = [];

    /// <summary>When set, every send throws this.</summary>
    public Exception? SendFailure { get; set; }

    public void Reset()
    {
        Inbox.Clear();
        Sent.Clear();
        Reads.Clear();
        SendFailure = null;
    }

    public Task<MailboxChanges> GetNewMessagesAsync(string? cursor, DateTimeOffset since, CancellationToken cancellationToken)
    {
        Reads.Add((cursor, since));
        var messages = Inbox.ToList();
        Inbox.Clear();
        return Task.FromResult(new MailboxChanges(messages, $"cursor-{++_reads}"));
    }

    public Task<SentEmail> SendAsync(OutgoingEmail email, CancellationToken cancellationToken)
    {
        if (SendFailure is not null)
        {
            throw SendFailure;
        }

        Sent.Add(email);
        var id = Guid.NewGuid().ToString("N");
        return Task.FromResult(new SentEmail($"sent-{id}", $"<{id}@helpdesk.test>", null));
    }

    /// <summary>Builds an incoming email with unique ids.</summary>
    public static InboundEmail Email(
        string subject = "I need help",
        string from = "student@example.com",
        string? text = "Hello, I need help.",
        string? html = null,
        string? conversationId = null,
        string? inReplyTo = null,
        Dictionary<string, string>? headers = null,
        DateTimeOffset? receivedAt = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var allHeaders = new Dictionary<string, string>(headers ?? [], StringComparer.OrdinalIgnoreCase);
        if (inReplyTo is not null)
        {
            allHeaders["In-Reply-To"] = inReplyTo;
        }

        return new InboundEmail(
            $"provider-{id}",
            $"<{id}@mail.example.com>",
            conversationId ?? $"conversation-{id}",
            from,
            "Test Student",
            subject,
            html,
            text,
            receivedAt ?? DateTimeOffset.UtcNow,
            allHeaders);
    }
}

/// <summary>Records which replies were queued for sending, in place of the background job queue.</summary>
public sealed class RecordingReplyQueue : IReplyQueue
{
    public List<long> Enqueued { get; } = [];

    public void Enqueue(long messageId) => Enqueued.Add(messageId);
}
