using Helpdesk.Infrastructure.Email;

namespace Helpdesk.Tests;

public class EmailTextTests
{
    [Fact]
    public void Html_becomes_text_with_paragraph_and_line_breaks()
    {
        var text = EmailText.HtmlToText(
            "<html><head><style>p{color:red}</style><title>T</title></head><body>" +
            "<p>Hello&nbsp;<b>team</b>,</p><p>Line one<br>line two</p><ul><li>First</li><li>Second</li></ul>" +
            "<script>alert(1)</script></body></html>");

        Assert.Equal("Hello team,\n\nLine one\nline two\n\nFirst\n\nSecond", text);
    }

    [Fact]
    public void Html_whitespace_is_collapsed()
    {
        Assert.Equal("One two three", EmailText.HtmlToText("<div>  One\n   two\t three  </div>"));
    }

    [Theory]
    [InlineData("Thanks, that worked!\n\nOn Mon, 2 Nov 2026 at 10:15, Support <support@example.com> wrote:\n> Try resetting your password.\n> Regards")]
    [InlineData("Thanks, that worked!\n\nOn Mon, 2 Nov 2026 at 10:15, Support\n<support@example.com> wrote:\n> Try resetting your password.")]
    [InlineData("Thanks, that worked!\n\n-----Original Message-----\nFrom: Support\nSent: Monday\nTry resetting your password.")]
    [InlineData("Thanks, that worked!\n\nFrom: Support <support@example.com>\nSent: Monday, 2 November 2026 10:15\nTo: Student\nSubject: RE: Login\n\nTry resetting your password.")]
    [InlineData("Thanks, that worked!\n\n________________________________\nFrom: Support\nTry resetting your password.")]
    [InlineData("Thanks, that worked!\n\n> Try resetting your password.\n> Regards\n")]
    public void Quoted_history_is_removed_from_a_reply(string reply)
    {
        Assert.Equal("Thanks, that worked!", EmailText.StripQuotedReply(reply));
    }

    [Fact]
    public void Reply_without_quoted_history_is_unchanged()
    {
        const string reply = "It still fails.\n\nFrom my side nothing changed since Monday.";

        Assert.Equal(reply, EmailText.StripQuotedReply(reply));
    }

    [Fact]
    public void Reply_that_is_only_a_quote_is_kept_whole()
    {
        const string reply = "On Mon, 2 Nov 2026, Support wrote:\n> Is it A or B?\nB, thanks";

        Assert.Equal(reply, EmailText.StripQuotedReply(reply));
    }
}

public class InboundEmailRulesTests
{
    [Theory]
    [InlineData("Auto-Submitted", "auto-replied")]
    [InlineData("Auto-Submitted", "auto-generated")]
    [InlineData("Precedence", "bulk")]
    [InlineData("Precedence", "auto_reply")]
    [InlineData("X-Autoreply", "yes")]
    [InlineData("List-Id", "<news.example.com>")]
    [InlineData("Return-Path", "<>")]
    public void Machine_sent_email_is_automated(string header, string value)
    {
        var email = FakeMailbox.Email(headers: new() { [header] = value });

        Assert.True(InboundEmailRules.IsAutomated(email));
    }

    [Theory]
    [InlineData("MAILER-DAEMON@example.com")]
    [InlineData("postmaster@example.com")]
    public void Bounce_senders_are_automated(string from)
    {
        Assert.True(InboundEmailRules.IsAutomated(FakeMailbox.Email(from: from)));
    }

    [Fact]
    public void Ordinary_email_is_not_automated()
    {
        var email = FakeMailbox.Email(headers: new() { ["Auto-Submitted"] = "no", ["Precedence"] = "normal" });

        Assert.False(InboundEmailRules.IsAutomated(email));
    }

    [Fact]
    public void Own_address_is_recognised_whatever_the_case()
    {
        Assert.True(InboundEmailRules.IsFromMailbox(FakeMailbox.Email(from: "Support@Helpdesk.Test"), FakeMailbox.Address));
        Assert.False(InboundEmailRules.IsFromMailbox(FakeMailbox.Email(), FakeMailbox.Address));
    }

    [Fact]
    public void Referenced_ids_come_from_both_reply_headers_without_duplicates()
    {
        var email = FakeMailbox.Email(headers: new()
        {
            ["In-Reply-To"] = "<b@example.com>",
            ["References"] = "<a@example.com> <b@example.com>",
        });

        Assert.Equal(["<b@example.com>", "<a@example.com>"], email.ReferencedMessageIds());
    }
}
