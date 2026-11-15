namespace Helpdesk.Core.Tickets;

public enum AutoSendMode
{
    /// <summary>Every reply is reviewed by an agent.</summary>
    Off,

    /// <summary>Nothing is sent automatically, but replies that would have been are recorded for review.</summary>
    Shadow,

    /// <summary>Replies that pass every check are sent without review.</summary>
    On,
}

public enum AutoSendDecision
{
    /// <summary>An agent must review the reply.</summary>
    Hold,

    /// <summary>Shadow mode: the reply passed every check and was not sent.</summary>
    WouldSend,

    Send,
}

/// <summary>Subjects that always need a person, whatever the knowledge base says.</summary>
public enum SensitiveTopic
{
    None,
    RefundOrBilling,
    Complaint,
    PolicyException,
    AccountOwnership,
    Other,
}

/// <param name="IsPublished">Whether the article is still published.</param>
/// <param name="AutoSendAllowed">Whether an admin approved the article for auto-send.</param>
public readonly record struct DraftSourceApproval(bool IsPublished, bool AutoSendAllowed);

/// <summary>Everything the auto-send decision depends on.</summary>
public sealed record AutoSendInput(
    AutoSendMode Mode,
    bool EmailEnabled,
    TicketStatus Status,
    TicketCategory? Category,
    bool AutoSendDisabledOnTicket,
    bool HasEarlierReplies,
    bool CanAnswer,
    SensitiveTopic SensitiveTopic,
    double Confidence,
    double ConfidenceThreshold,
    IReadOnlyList<DraftSourceApproval> Sources);

public sealed record AutoSendResult(AutoSendDecision Decision, IReadOnlyList<string> Reasons);

/// <summary>
/// Decides whether an AI-drafted reply may be sent without an agent reading it.
///
/// This is code, not a prompt, on purpose: the model writes the reply, but it never decides
/// whether the reply goes out. The decisive rule is the allow-list - every article the reply
/// was written from must be one an admin approved for auto-send. The model's confidence and
/// its reading of the topic are extra checks on top; they can only stop a reply, never release one.
/// </summary>
public static class AutoSendGate
{
    public static AutoSendResult Evaluate(AutoSendInput input)
    {
        var reasons = new List<string>();

        if (input.Status != TicketStatus.Open)
        {
            reasons.Add("The ticket is not open.");
        }

        if (input.AutoSendDisabledOnTicket)
        {
            reasons.Add("The requester already replied to an automatic answer on this ticket.");
        }

        if (input.HasEarlierReplies)
        {
            reasons.Add("The ticket has already been answered once; only a first reply is sent automatically.");
        }

        if (input.Category is null)
        {
            reasons.Add("The ticket has no category.");
        }
        else if (input.Category == TicketCategory.RefundRequest)
        {
            reasons.Add("Refund requests are always handled by a person.");
        }

        if (!input.CanAnswer)
        {
            reasons.Add("The AI could not answer from the knowledge base.");
        }

        if (input.SensitiveTopic != SensitiveTopic.None)
        {
            reasons.Add("The message touches a subject that always needs a person.");
        }

        if (input.Sources.Count == 0)
        {
            reasons.Add("The reply is not based on any knowledge base article.");
        }
        else if (input.Sources.Any(source => !source.IsPublished || !source.AutoSendAllowed))
        {
            reasons.Add("The reply uses an article that is not approved for auto-send.");
        }

        if (input.Confidence < input.ConfidenceThreshold)
        {
            reasons.Add($"The AI's confidence ({input.Confidence:0.00}) is below the required {input.ConfidenceThreshold:0.00}.");
        }

        // The reply itself passed. What remains is whether sending is switched on at all.
        if (reasons.Count == 0)
        {
            switch (input.Mode)
            {
                case AutoSendMode.On when input.EmailEnabled:
                    return new AutoSendResult(AutoSendDecision.Send, []);
                case AutoSendMode.On:
                    reasons.Add("Email is not set up, so nothing can be sent.");
                    break;
                case AutoSendMode.Shadow:
                    return new AutoSendResult(AutoSendDecision.WouldSend, []);
                default:
                    reasons.Add("Auto-send is switched off.");
                    break;
            }
        }

        return new AutoSendResult(AutoSendDecision.Hold, reasons);
    }
}
