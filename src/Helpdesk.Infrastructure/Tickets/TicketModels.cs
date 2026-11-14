using Helpdesk.Core.Tickets;

namespace Helpdesk.Infrastructure.Tickets;

public class TicketSummary
{
    public int Id { get; init; }
    public string Subject { get; init; } = "";
    public string RequesterEmail { get; init; } = "";
    public string? RequesterName { get; init; }
    public TicketStatus Status { get; init; }
    public TicketCategory? Category { get; init; }
    public Guid? AssigneeId { get; init; }
    public string? AssigneeName { get; init; }
    public int? TeamId { get; init; }
    public string? TeamName { get; init; }
    public int? OriginalTicketId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class TicketDetail : TicketSummary
{
    public DateTimeOffset? ResolvedAt { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }

    /// <summary>Null when classification was never requested for this ticket.</summary>
    public ClassificationStatus? ClassificationStatus { get; init; }

    /// <summary>What the AI said the category is, whether or not it was applied.</summary>
    public TicketCategory? AiCategory { get; init; }

    public decimal? AiConfidence { get; init; }
    public string? Summary { get; init; }

    /// <summary>How many messages the summary covers.</summary>
    public int? SummaryMessageCount { get; init; }

    public DateTimeOffset? SummaryUpdatedAt { get; init; }
    public IReadOnlyList<TicketMessage> Messages { get; set; } = [];
    public IReadOnlyList<TicketEvent> Events { get; set; } = [];
}

public sealed class TicketMessage
{
    public long Id { get; init; }
    public MessageDirection Direction { get; init; }
    public string? SenderEmail { get; init; }
    public string? SenderName { get; init; }
    public Guid? AuthorUserId { get; init; }
    public string? AuthorName { get; init; }
    public string BodyText { get; init; } = "";

    /// <summary>The email HTML exactly as received. Untrusted: sanitise before showing it.</summary>
    public string? BodyHtml { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Outbound only: whether the reply has been emailed.</summary>
    public MessageSendStatus? SendStatus { get; init; }

    public string? SendError { get; init; }
}

public sealed class TicketEvent
{
    public long Id { get; init; }
    public TicketEventType Type { get; init; }
    public Guid? ActorUserId { get; init; }
    public string? ActorName { get; init; }
    public string? FromValue { get; init; }
    public string? ToValue { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>A ticket to create from an incoming message.</summary>
public sealed record NewTicket(
    string Subject,
    string RequesterEmail,
    string? RequesterName,
    string BodyText,
    string? BodyHtml = null,
    TicketCategory? Category = null,
    DateTimeOffset? ReceivedAt = null,
    int? OriginalTicketId = null,
    string? InternetMessageId = null,
    string? ProviderMessageId = null,
    string? ConversationId = null,
    // Leave the ticket out of any team queue until the AI has classified it.
    bool DeferRouting = false);

public enum TicketSort
{
    Id,
    Subject,
    RequesterEmail,
    Status,
    Category,
    CreatedAt,
    UpdatedAt,
}

public sealed record TicketListQuery
{
    public const int MaxPageSize = 100;

    /// <summary>Empty means every status.</summary>
    public IReadOnlyList<TicketStatus> Statuses { get; init; } = [];

    public TicketCategory? Category { get; init; }

    /// <summary>Only tickets with no category. Takes precedence over <see cref="Category"/>.</summary>
    public bool Unclassified { get; init; }

    public Guid? AssigneeId { get; init; }

    /// <summary>Only tickets with no owner. Takes precedence over <see cref="AssigneeId"/>.</summary>
    public bool Unassigned { get; init; }

    public int? TeamId { get; init; }

    /// <summary>Matches part of the subject, requester email, or requester name.</summary>
    public string? Search { get; init; }

    public TicketSort Sort { get; init; } = TicketSort.CreatedAt;
    public bool Descending { get; init; } = true;
    public int Skip { get; init; }
    public int Take { get; init; } = 25;
}

public sealed record TicketPage(IReadOnlyList<TicketSummary> Items, int TotalCount);

public enum TicketChangeResult
{
    Ok,
    NotFound,

    /// <summary>The ticket's current status does not allow the change.</summary>
    NotAllowed,
}

/// <summary>An email from the requester that belongs to an existing ticket.</summary>
public sealed record InboundReply(
    string SenderEmail,
    string? SenderName,
    string BodyText,
    string? BodyHtml,
    DateTimeOffset ReceivedAt,
    string? InternetMessageId,
    string? ProviderMessageId,
    string? ConversationId);

public enum AppendResult
{
    Appended,

    /// <summary>The ticket was resolved, and the reply reopened it.</summary>
    Reopened,

    /// <summary>The ticket is closed, so nothing was added.</summary>
    TicketClosed,
    NotFound,
}

/// <summary>An agent reply, with what is needed to email it.</summary>
public sealed class OutboundReply
{
    public long Id { get; init; }
    public int TicketId { get; init; }
    public string Subject { get; init; } = "";
    public string RequesterEmail { get; init; } = "";
    public string? RequesterName { get; init; }
    public string BodyText { get; init; } = "";
    public MessageSendStatus? SendStatus { get; init; }
    public int SendAttempts { get; init; }

    /// <summary>The requester's latest email on the ticket, which the reply answers. Null when there is none.</summary>
    public string? ReplyToProviderMessageId { get; init; }
}