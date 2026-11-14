namespace Helpdesk.Core.Tickets;

/// <summary>Which status changes are allowed.</summary>
public static class TicketStatusRules
{
    private static readonly TicketStatus[] All = Enum.GetValues<TicketStatus>();

    /// <summary>
    /// Whether a ticket may move from one status to another at all.
    /// Closed is final; every other status can move to any other status.
    /// </summary>
    public static bool IsAllowed(TicketStatus from, TicketStatus to) =>
        from != TicketStatus.Closed && from != to;

    /// <summary>
    /// Whether an agent may make this change by hand. Agents cannot put a ticket into
    /// Needs Human Review: that status means the AI escalated it, and only the system sets it.
    /// </summary>
    public static bool IsAllowedForAgent(TicketStatus from, TicketStatus to) =>
        IsAllowed(from, to) && to != TicketStatus.NeedsHumanReview;

    /// <summary>The statuses an agent can choose for a ticket in the given status.</summary>
    public static IReadOnlyList<TicketStatus> AgentTargets(TicketStatus from) =>
        All.Where(to => IsAllowedForAgent(from, to)).ToList();

    /// <summary>A closed ticket accepts no replies and no other changes.</summary>
    public static bool AcceptsChanges(TicketStatus status) => status != TicketStatus.Closed;

    /// <summary>
    /// The status a ticket takes when an agent replies. Replying to an escalated ticket means
    /// a human has picked it up, so it becomes Open; otherwise the status stays as it is.
    /// </summary>
    public static TicketStatus AfterAgentReply(TicketStatus current, bool resolve)
    {
        if (resolve)
        {
            return TicketStatus.Resolved;
        }

        return current == TicketStatus.NeedsHumanReview ? TicketStatus.Open : current;
    }

    /// <summary>
    /// The status a ticket takes when the requester replies. A resolved ticket reopens;
    /// otherwise the status stays as it is. (A closed ticket never gets here: the reply becomes a new ticket.)
    /// </summary>
    public static TicketStatus AfterRequesterReply(TicketStatus current) =>
        current == TicketStatus.Resolved ? TicketStatus.Open : current;
}
