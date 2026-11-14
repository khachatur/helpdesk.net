using Helpdesk.Core.Tickets;
using Helpdesk.Infrastructure.Tickets;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Infrastructure.Email;

/// <summary>Hands a newly stored agent reply to whatever sends it in the background.</summary>
public interface IReplyQueue
{
    void Enqueue(long messageId);
}

/// <summary>Used when background jobs are switched off. Replies stay pending.</summary>
public sealed class NoReplyQueue : IReplyQueue
{
    public void Enqueue(long messageId)
    {
    }
}

/// <summary>Emails a stored agent reply to the requester and records how it went.</summary>
public sealed class ReplySender(IMailbox mailbox, TicketStore ticketStore, ILogger<ReplySender> logger)
{
    /// <summary>After this many failed attempts the reply is marked as failed and waits for an agent to retry.</summary>
    public const int MaxAttempts = 5;

    /// <summary>
    /// Sends the reply if it is still pending. Throws when an attempt fails and more attempts
    /// remain, so the caller's retry mechanism tries again; the last failure does not throw.
    /// </summary>
    public async Task SendAsync(long messageId, CancellationToken cancellationToken)
    {
        var reply = await ticketStore.GetOutboundAsync(messageId, cancellationToken);
        if (reply is null || reply.SendStatus != MessageSendStatus.Pending)
        {
            return;
        }

        var subject = reply.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)
            ? reply.Subject
            : "Re: " + reply.Subject;

        try
        {
            var sent = await mailbox.SendAsync(
                new OutgoingEmail(reply.RequesterEmail, reply.RequesterName, subject, reply.BodyText, reply.ReplyToProviderMessageId),
                cancellationToken);

            await ticketStore.MarkSentAsync(messageId, sent, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var final = reply.SendAttempts + 1 >= MaxAttempts;
            await ticketStore.RecordSendFailureAsync(messageId, ex.Message, final, cancellationToken);
            logger.LogWarning(ex, "Sending reply {MessageId} on ticket {TicketId} failed (attempt {Attempt})",
                messageId, reply.TicketId, reply.SendAttempts + 1);

            if (!final)
            {
                throw;
            }
        }
    }
}
