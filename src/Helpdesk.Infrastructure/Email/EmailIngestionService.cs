using Helpdesk.Infrastructure.Tickets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Helpdesk.Infrastructure.Email;

public sealed record IngestionSummary(int Read, int Tickets, int Replies, int Skipped, int Failed);

/// <summary>Reads new mail from the support mailbox and turns it into tickets and ticket replies.</summary>
public sealed class EmailIngestionService(
    IMailbox mailbox,
    EmailStore emailStore,
    TicketStore ticketStore,
    IOptions<EmailOptions> options,
    TimeProvider timeProvider,
    ILogger<EmailIngestionService> logger)
{
    private readonly EmailOptions _options = options.Value;

    /// <summary>
    /// Reads everything that arrived since the last call. Safe to repeat: an email that was
    /// already handled is recognised by its Message-ID and ignored.
    /// </summary>
    public async Task<IngestionSummary> PollAsync(CancellationToken cancellationToken)
    {
        if (!_options.IsEnabled)
        {
            return new IngestionSummary(0, 0, 0, 0, 0);
        }

        // The first read starts from now, so mail already sitting in the inbox is not turned into tickets.
        var state = await emailStore.GetStateAsync(_options.Mailbox, cancellationToken)
            ?? new MailboxState(null, _options.IngestFrom ?? timeProvider.GetUtcNow());

        var changes = await mailbox.GetNewMessagesAsync(state.Cursor, state.IngestFrom, cancellationToken);

        int tickets = 0, replies = 0, skipped = 0, failed = 0;
        foreach (var email in changes.Messages.OrderBy(message => message.ReceivedAt))
        {
            InboundOutcome? outcome;
            try
            {
                outcome = await IngestAsync(email, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad email must not block the mailbox. It is logged as failed and the rest continue.
                logger.LogError(ex, "Could not process email {InternetMessageId} from {From}", email.InternetMessageId, email.FromEmail);
                await emailStore.LogAsync(email, InboundOutcome.Failed, null, ex.Message, cancellationToken);
                outcome = InboundOutcome.Failed;
            }

            switch (outcome)
            {
                case InboundOutcome.CreatedTicket or InboundOutcome.CreatedFollowUpTicket:
                    tickets++;
                    break;
                case InboundOutcome.AppendedToTicket or InboundOutcome.ReopenedTicket:
                    replies++;
                    break;
                case InboundOutcome.Failed:
                    failed++;
                    break;
                default:
                    skipped++;
                    break;
            }
        }

        await emailStore.SaveStateAsync(_options.Mailbox, state with { Cursor = changes.Cursor }, cancellationToken);

        var summary = new IngestionSummary(changes.Messages.Count, tickets, replies, skipped, failed);
        if (summary.Read > 0)
        {
            logger.LogInformation("Mailbox poll: {@Summary}", summary);
        }

        return summary;
    }

    /// <summary>Handles one email. Returns null when it had been processed before.</summary>
    public async Task<InboundOutcome?> IngestAsync(InboundEmail email, CancellationToken cancellationToken)
    {
        if (await emailStore.IsProcessedAsync(email.InternetMessageId, cancellationToken))
        {
            return null;
        }

        if (InboundEmailRules.IsFromMailbox(email, _options.Mailbox))
        {
            await emailStore.LogAsync(email, InboundOutcome.SkippedOwnMessage, null, null, cancellationToken);
            return InboundOutcome.SkippedOwnMessage;
        }

        if (InboundEmailRules.IsAutomated(email))
        {
            await emailStore.LogAsync(email, InboundOutcome.SkippedAutomated, null, null, cancellationToken);
            return InboundOutcome.SkippedAutomated;
        }

        var fullText = email.BodyText ?? (email.BodyHtml is null ? "" : EmailText.HtmlToText(email.BodyHtml));
        var subject = string.IsNullOrWhiteSpace(email.Subject) ? "(no subject)" : EmailStore.Limit(email.Subject.Trim(), 500);
        var fromName = email.FromName is null ? null : EmailStore.Limit(email.FromName, 200);

        var existingTicketId = await ticketStore.FindForEmailAsync(
            email.ConversationId, email.ReferencedMessageIds(), cancellationToken);

        if (existingTicketId is { } ticketId)
        {
            var message = new InboundReply(
                email.FromEmail,
                fromName,
                // In a reply, what matters is what the requester just wrote, not the quoted history.
                EmailText.StripQuotedReply(fullText),
                email.BodyHtml,
                email.ReceivedAt,
                email.InternetMessageId,
                email.ProviderMessageId,
                email.ConversationId);

            var result = await ticketStore.AppendInboundAsync(ticketId, message, cancellationToken);
            if (result is AppendResult.Appended or AppendResult.Reopened)
            {
                var outcome = result == AppendResult.Reopened ? InboundOutcome.ReopenedTicket : InboundOutcome.AppendedToTicket;
                await emailStore.LogAsync(email, outcome, ticketId, null, cancellationToken);
                return outcome;
            }

            // The ticket is closed, and closed is final: the reply becomes a new ticket linked to it.
        }

        var newTicketId = await ticketStore.CreateAsync(
            new NewTicket(
                subject,
                email.FromEmail,
                fromName,
                fullText,
                email.BodyHtml,
                ReceivedAt: email.ReceivedAt,
                OriginalTicketId: existingTicketId,
                InternetMessageId: email.InternetMessageId,
                ProviderMessageId: email.ProviderMessageId,
                ConversationId: email.ConversationId),
            cancellationToken);

        var created = existingTicketId is null ? InboundOutcome.CreatedTicket : InboundOutcome.CreatedFollowUpTicket;
        await emailStore.LogAsync(email, created, newTicketId, null, cancellationToken);
        return created;
    }
}
