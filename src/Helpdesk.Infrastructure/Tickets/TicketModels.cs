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
    public int? OriginalTicketId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class TicketDetail : TicketSummary
{
    public DateTimeOffset? ResolvedAt { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
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
    int? OriginalTicketId = null);

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
