using Helpdesk.Core.Tickets;
using static Helpdesk.Core.Tickets.TicketStatus;

namespace Helpdesk.Tests;

public class TicketStatusRulesTests
{
    [Theory]
    [InlineData(Open, Resolved)]
    [InlineData(Open, Closed)]
    [InlineData(NeedsHumanReview, Open)]
    [InlineData(NeedsHumanReview, Resolved)]
    [InlineData(NeedsHumanReview, Closed)]
    [InlineData(Resolved, Open)]
    [InlineData(Resolved, Closed)]
    public void Agent_can_make_these_changes(TicketStatus from, TicketStatus to)
    {
        Assert.True(TicketStatusRules.IsAllowedForAgent(from, to));
    }

    [Theory]
    [InlineData(Closed, Open)]
    [InlineData(Closed, Resolved)]
    [InlineData(Closed, NeedsHumanReview)]
    [InlineData(Closed, Closed)]
    public void Closed_is_final(TicketStatus from, TicketStatus to)
    {
        Assert.False(TicketStatusRules.IsAllowed(from, to));
        Assert.False(TicketStatusRules.IsAllowedForAgent(from, to));
    }

    [Theory]
    [InlineData(Open)]
    [InlineData(Resolved)]
    public void Only_the_system_can_escalate_to_needs_human_review(TicketStatus from)
    {
        Assert.True(TicketStatusRules.IsAllowed(from, NeedsHumanReview));
        Assert.False(TicketStatusRules.IsAllowedForAgent(from, NeedsHumanReview));
    }

    [Fact]
    public void A_status_cannot_change_to_itself()
    {
        Assert.All(Enum.GetValues<TicketStatus>(), status => Assert.False(TicketStatusRules.IsAllowed(status, status)));
    }

    [Fact]
    public void Agent_targets_list_what_an_agent_can_choose()
    {
        Assert.Equal([Resolved, Closed], TicketStatusRules.AgentTargets(Open));
        Assert.Equal([Open, Resolved, Closed], TicketStatusRules.AgentTargets(NeedsHumanReview));
        Assert.Equal([Open, Closed], TicketStatusRules.AgentTargets(Resolved));
        Assert.Empty(TicketStatusRules.AgentTargets(Closed));
    }

    [Theory]
    [InlineData(Open, false, Open)]
    [InlineData(Open, true, Resolved)]
    [InlineData(NeedsHumanReview, false, Open)]
    [InlineData(NeedsHumanReview, true, Resolved)]
    [InlineData(Resolved, false, Resolved)]
    [InlineData(Resolved, true, Resolved)]
    public void Agent_reply_sets_the_expected_status(TicketStatus current, bool resolve, TicketStatus expected)
    {
        Assert.Equal(expected, TicketStatusRules.AfterAgentReply(current, resolve));
    }
}
