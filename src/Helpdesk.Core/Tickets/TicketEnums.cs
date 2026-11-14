namespace Helpdesk.Core.Tickets;

/// <summary>A ticket has exactly one status at a time.</summary>
public enum TicketStatus
{
    /// <summary>Being worked on.</summary>
    Open,

    /// <summary>Escalated from the AI and waiting for an agent.</summary>
    NeedsHumanReview,

    /// <summary>Answered, waiting to see whether the requester needs more help. A reply reopens it.</summary>
    Resolved,

    /// <summary>Final. Cannot be reopened.</summary>
    Closed,
}

/// <summary>A ticket belongs to a single category, or to none until it is classified.</summary>
public enum TicketCategory
{
    GeneralQuestion,
    TechnicalQuestion,
    RefundRequest,
}

public enum MessageDirection
{
    /// <summary>From the requester.</summary>
    Inbound,

    /// <summary>To the requester.</summary>
    Outbound,
}

public enum TicketEventType
{
    Created,
    StatusChanged,
    CategoryChanged,
    ReplyAdded,

    /// <summary>The requester replied by email.</summary>
    ReplyReceived,

    /// <summary>The ticket moved to another team's queue.</summary>
    TeamChanged,

    /// <summary>The ticket got a new owner, or lost its owner.</summary>
    Assigned,
}

/// <summary>Whether an outbound message has been emailed to the requester.</summary>
public enum MessageSendStatus
{
    /// <summary>Waiting to be sent, or being retried.</summary>
    Pending,
    Sent,

    /// <summary>Every attempt failed. An agent can retry.</summary>
    Failed,

    /// <summary>Stored only, because email was not configured when it was written.</summary>
    Skipped,
}
/// <summary>Where a ticket is in AI classification.</summary>
public enum ClassificationStatus
{
    /// <summary>Waiting for the AI. The ticket is not in a team queue yet.</summary>
    Pending,

    /// <summary>The AI answered, confidently or not.</summary>
    Done,

    /// <summary>The AI could not classify it, and it went to the default team.</summary>
    Failed,
}
