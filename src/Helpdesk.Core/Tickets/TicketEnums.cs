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
}
