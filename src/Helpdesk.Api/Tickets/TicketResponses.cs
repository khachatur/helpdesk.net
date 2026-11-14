using Helpdesk.Core.Tickets;
using Helpdesk.Infrastructure.Tickets;

namespace Helpdesk.Api.Tickets;

public sealed record TicketListResponse(IEnumerable<TicketSummaryResponse> Items, int TotalCount);

public sealed record TicketSummaryResponse(
    int Id,
    string Subject,
    string RequesterEmail,
    string? RequesterName,
    TicketStatus Status,
    TicketCategory? Category,
    Guid? AssigneeId,
    string? AssigneeName,
    int? TeamId,
    string? TeamName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static TicketSummaryResponse From(TicketSummary ticket) => new(
        ticket.Id,
        ticket.Subject,
        ticket.RequesterEmail,
        ticket.RequesterName,
        ticket.Status,
        ticket.Category,
        ticket.AssigneeId,
        ticket.AssigneeName,
        ticket.TeamId,
        ticket.TeamName,
        ticket.CreatedAt,
        ticket.UpdatedAt);
}

public sealed record TicketDetailResponse(
    int Id,
    string Subject,
    string RequesterEmail,
    string? RequesterName,
    TicketStatus Status,
    TicketCategory? Category,
    Guid? AssigneeId,
    string? AssigneeName,
    int? TeamId,
    string? TeamName,
    int? OriginalTicketId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ResolvedAt,
    DateTimeOffset? ClosedAt,
    // Null when AI classification was never requested for this ticket.
    ClassificationStatus? ClassificationStatus,
    // What the AI said the category is, whether or not it was applied.
    TicketCategory? AiCategory,
    decimal? AiConfidence,
    string? Summary,
    // False when messages have arrived since the summary was written.
    bool SummaryIsCurrent,
    // What the signed-in agent may do next, so the client does not repeat the rules.
    IReadOnlyList<TicketStatus> AllowedStatuses,
    bool AcceptsChanges,
    IEnumerable<TicketMessageResponse> Messages,
    IEnumerable<TicketEventResponse> Events)
{
    public static TicketDetailResponse From(TicketDetail ticket, EmailHtmlSanitizer sanitizer) => new(
        ticket.Id,
        ticket.Subject,
        ticket.RequesterEmail,
        ticket.RequesterName,
        ticket.Status,
        ticket.Category,
        ticket.AssigneeId,
        ticket.AssigneeName,
        ticket.TeamId,
        ticket.TeamName,
        ticket.OriginalTicketId,
        ticket.CreatedAt,
        ticket.UpdatedAt,
        ticket.ResolvedAt,
        ticket.ClosedAt,
        ticket.ClassificationStatus,
        ticket.AiCategory,
        ticket.AiConfidence,
        ticket.Summary,
        ticket.SummaryMessageCount == ticket.Messages.Count,
        TicketStatusRules.AgentTargets(ticket.Status),
        TicketStatusRules.AcceptsChanges(ticket.Status),
        ticket.Messages.Select(message => TicketMessageResponse.From(message, sanitizer)),
        ticket.Events.Select(TicketEventResponse.Of));
}

/// <param name="BodyHtml">Sanitised HTML, safe to insert into the page. Null for plain-text messages.</param>
/// <param name="SendStatus">For agent replies: whether the reply has been emailed. Null for incoming messages.</param>
public sealed record TicketMessageResponse(
    long Id,
    MessageDirection Direction,
    string? SenderEmail,
    string? SenderName,
    string? AuthorName,
    string BodyText,
    string? BodyHtml,
    DateTimeOffset CreatedAt,
    MessageSendStatus? SendStatus,
    string? SendError)
{
    public static TicketMessageResponse From(TicketMessage message, EmailHtmlSanitizer sanitizer) => new(
        message.Id,
        message.Direction,
        message.SenderEmail,
        message.SenderName,
        message.AuthorName,
        message.BodyText,
        message.BodyHtml is null ? null : sanitizer.Sanitize(message.BodyHtml),
        message.CreatedAt,
        message.SendStatus,
        message.SendError);
}

/// <param name="ActorName">Null when the system made the change.</param>
public sealed record TicketEventResponse(
    long Id,
    TicketEventType Type,
    string? ActorName,
    string? From,
    string? To,
    DateTimeOffset CreatedAt)
{
    public static TicketEventResponse Of(TicketEvent ticketEvent) => new(
        ticketEvent.Id,
        ticketEvent.Type,
        ticketEvent.ActorName,
        ticketEvent.FromValue,
        ticketEvent.ToValue,
        ticketEvent.CreatedAt);
}
