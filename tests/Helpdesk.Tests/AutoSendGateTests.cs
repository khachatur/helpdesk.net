using Helpdesk.Core.Tickets;

namespace Helpdesk.Tests;

/// <summary>Every branch of the check that decides whether an AI reply goes out without review.</summary>
public class AutoSendGateTests
{
    /// <summary>A reply that passes every check, with auto-send on. Each test changes one thing.</summary>
    private static readonly AutoSendInput Passing = new(
        Mode: AutoSendMode.On,
        EmailEnabled: true,
        Status: TicketStatus.Open,
        Category: TicketCategory.GeneralQuestion,
        AutoSendDisabledOnTicket: false,
        HasEarlierReplies: false,
        CanAnswer: true,
        SensitiveTopic: SensitiveTopic.None,
        Confidence: 0.9,
        ConfidenceThreshold: 0.85,
        Sources: [new DraftSourceApproval(IsPublished: true, AutoSendAllowed: true)]);

    private static void AssertHeld(AutoSendInput input, string expectedReason)
    {
        var result = AutoSendGate.Evaluate(input);

        Assert.Equal(AutoSendDecision.Hold, result.Decision);
        Assert.Contains(result.Reasons, reason => reason.Contains(expectedReason, StringComparison.Ordinal));
    }

    [Fact]
    public void Reply_that_passes_every_check_is_sent_when_auto_send_is_on()
    {
        var result = AutoSendGate.Evaluate(Passing);

        Assert.Equal(AutoSendDecision.Send, result.Decision);
        Assert.Empty(result.Reasons);
    }

    [Theory]
    [InlineData(TicketCategory.GeneralQuestion)]
    [InlineData(TicketCategory.TechnicalQuestion)]
    public void General_and_technical_questions_can_be_sent(TicketCategory category)
    {
        Assert.Equal(AutoSendDecision.Send, AutoSendGate.Evaluate(Passing with { Category = category }).Decision);
    }

    [Fact]
    public void Shadow_mode_records_the_reply_as_would_send_and_sends_nothing()
    {
        var result = AutoSendGate.Evaluate(Passing with { Mode = AutoSendMode.Shadow });

        Assert.Equal(AutoSendDecision.WouldSend, result.Decision);
    }

    [Fact]
    public void Shadow_mode_does_not_need_email_to_be_set_up()
    {
        var result = AutoSendGate.Evaluate(Passing with { Mode = AutoSendMode.Shadow, EmailEnabled = false });

        Assert.Equal(AutoSendDecision.WouldSend, result.Decision);
    }

    [Fact]
    public void Nothing_is_sent_while_auto_send_is_switched_off() =>
        AssertHeld(Passing with { Mode = AutoSendMode.Off }, "switched off");

    [Fact]
    public void Nothing_is_sent_when_email_is_not_set_up() =>
        AssertHeld(Passing with { EmailEnabled = false }, "Email is not set up");

    [Fact]
    public void Reply_from_an_unapproved_article_is_held() =>
        AssertHeld(
            Passing with { Sources = [new DraftSourceApproval(IsPublished: true, AutoSendAllowed: false)] },
            "not approved for auto-send");

    [Fact]
    public void One_unapproved_article_among_approved_ones_holds_the_reply() =>
        AssertHeld(
            Passing with
            {
                Sources =
                [
                    new DraftSourceApproval(IsPublished: true, AutoSendAllowed: true),
                    new DraftSourceApproval(IsPublished: true, AutoSendAllowed: false),
                ],
            },
            "not approved for auto-send");

    [Fact]
    public void Reply_from_an_unpublished_article_is_held() =>
        AssertHeld(
            Passing with { Sources = [new DraftSourceApproval(IsPublished: false, AutoSendAllowed: true)] },
            "not approved for auto-send");

    [Fact]
    public void Reply_based_on_no_article_is_held() =>
        AssertHeld(Passing with { Sources = [] }, "not based on any knowledge base article");

    [Fact]
    public void Confidence_below_the_threshold_holds_the_reply() =>
        AssertHeld(Passing with { Confidence = 0.84 }, "below the required 0.85");

    [Fact]
    public void Confidence_exactly_at_the_threshold_passes()
    {
        Assert.Equal(AutoSendDecision.Send, AutoSendGate.Evaluate(Passing with { Confidence = 0.85 }).Decision);
    }

    [Fact]
    public void Refund_requests_are_never_sent_automatically() =>
        AssertHeld(Passing with { Category = TicketCategory.RefundRequest }, "Refund requests are always handled by a person");

    [Fact]
    public void Unclassified_ticket_is_held() =>
        AssertHeld(Passing with { Category = null }, "no category");

    [Theory]
    [InlineData(SensitiveTopic.RefundOrBilling)]
    [InlineData(SensitiveTopic.Complaint)]
    [InlineData(SensitiveTopic.PolicyException)]
    [InlineData(SensitiveTopic.AccountOwnership)]
    [InlineData(SensitiveTopic.Other)]
    public void Sensitive_subjects_are_held(SensitiveTopic topic) =>
        AssertHeld(Passing with { SensitiveTopic = topic }, "always needs a person");

    [Fact]
    public void Reply_the_ai_could_not_ground_is_held() =>
        AssertHeld(Passing with { CanAnswer = false }, "could not answer");

    [Fact]
    public void Ticket_where_the_requester_already_replied_to_an_automatic_answer_is_held() =>
        AssertHeld(Passing with { AutoSendDisabledOnTicket = true }, "already replied to an automatic answer");

    [Fact]
    public void Only_a_first_reply_is_sent_automatically() =>
        AssertHeld(Passing with { HasEarlierReplies = true }, "only a first reply");

    [Theory]
    [InlineData(TicketStatus.NeedsHumanReview)]
    [InlineData(TicketStatus.Resolved)]
    [InlineData(TicketStatus.Closed)]
    public void Ticket_that_is_not_open_is_held(TicketStatus status) =>
        AssertHeld(Passing with { Status = status }, "not open");

    [Fact]
    public void Every_failing_check_is_reported()
    {
        var result = AutoSendGate.Evaluate(Passing with
        {
            Category = TicketCategory.RefundRequest,
            Confidence = 0.2,
            Sources = [],
            SensitiveTopic = SensitiveTopic.RefundOrBilling,
        });

        Assert.Equal(AutoSendDecision.Hold, result.Decision);
        Assert.Equal(4, result.Reasons.Count);
    }

    [Theory]
    [InlineData(AutoSendMode.Shadow)]
    [InlineData(AutoSendMode.On)]
    public void No_mode_releases_a_reply_that_fails_a_check(AutoSendMode mode)
    {
        var result = AutoSendGate.Evaluate(Passing with
        {
            Mode = mode,
            Sources = [new DraftSourceApproval(IsPublished: true, AutoSendAllowed: false)],
        });

        Assert.Equal(AutoSendDecision.Hold, result.Decision);
    }
}
