using System.Net;
using System.Net.Http.Json;
using Dapper;
using Helpdesk.Core.Tickets;
using Helpdesk.Infrastructure.Data;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Tickets;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Tests;

[Collection(ApiCollection.Name)]
public class EmailIntegrationTests
{
    private readonly AuthApiFactory _factory;
    private readonly FakeMailbox _mailbox;
    private readonly EmailIngestionService _ingestion;
    private readonly ReplySender _sender;
    private readonly TicketStore _tickets;

    public EmailIntegrationTests(AuthApiFactory factory)
    {
        _factory = factory;
        _mailbox = factory.Mailbox;
        _ingestion = factory.Services.GetRequiredService<EmailIngestionService>();
        _sender = factory.Services.GetRequiredService<ReplySender>();
        _tickets = factory.Tickets;

        // Each test starts with an empty mailbox that has never been read.
        _mailbox.Reset();
        factory.ReplyQueue.Enqueued.Clear();
        Execute("DELETE FROM dbo.MailboxState");
    }

    private void Execute(string sql, object? parameters = null)
    {
        using var connection = _factory.Services.GetRequiredService<IDbConnectionFactory>()
            .OpenAsync().GetAwaiter().GetResult();
        connection.Execute(sql, parameters);
    }

    private T Scalar<T>(string sql, object? parameters = null)
    {
        using var connection = _factory.Services.GetRequiredService<IDbConnectionFactory>()
            .OpenAsync().GetAwaiter().GetResult();
        return connection.ExecuteScalar<T>(sql, parameters)!;
    }

    private async Task<TicketDetail> TicketForAsync(InboundEmail email)
    {
        var id = Scalar<int>(
            "SELECT TicketId FROM dbo.TicketMessages WHERE InternetMessageId = @id", new { id = email.InternetMessageId });
        return (await _tickets.GetAsync(id, CancellationToken.None))!;
    }

    private async Task<InboundEmail> ReceiveAsync(InboundEmail email)
    {
        _mailbox.Inbox.Add(email);
        await _ingestion.PollAsync(CancellationToken.None);
        return email;
    }

    [Fact]
    public async Task New_email_becomes_an_open_ticket()
    {
        var email = FakeMailbox.Email(subject: "Cannot log in", from: "maya@example.com", text: "My password is rejected.");

        var summary = await PollWith(email);

        Assert.Equal(new IngestionSummary(Read: 1, Tickets: 1, Replies: 0, Skipped: 0, Failed: 0), summary);
        var ticket = await TicketForAsync(email);
        Assert.Equal("Cannot log in", ticket.Subject);
        Assert.Equal("maya@example.com", ticket.RequesterEmail);
        Assert.Equal("Test Student", ticket.RequesterName);
        Assert.Equal(TicketStatus.Open, ticket.Status);
        Assert.Null(ticket.Category);
        Assert.Equal(email.ReceivedAt.ToUnixTimeSeconds(), ticket.CreatedAt.ToUnixTimeSeconds());
        var message = Assert.Single(ticket.Messages);
        Assert.Equal(MessageDirection.Inbound, message.Direction);
        Assert.Equal("My password is rejected.", message.BodyText);
    }

    private async Task<IngestionSummary> PollWith(params InboundEmail[] emails)
    {
        _mailbox.Inbox.AddRange(emails);
        return await _ingestion.PollAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Html_email_keeps_its_html_and_gets_a_text_version()
    {
        var email = await ReceiveAsync(FakeMailbox.Email(text: null, html: "<p>Hello <b>team</b></p><p>Second paragraph</p>"));

        var message = Assert.Single((await TicketForAsync(email)).Messages);

        Assert.Equal("Hello team\n\nSecond paragraph", message.BodyText);
        Assert.Equal("<p>Hello <b>team</b></p><p>Second paragraph</p>", message.BodyHtml);
    }

    [Fact]
    public async Task Email_without_a_subject_gets_a_placeholder()
    {
        var email = await ReceiveAsync(FakeMailbox.Email(subject: "  "));

        Assert.Equal("(no subject)", (await TicketForAsync(email)).Subject);
    }

    [Fact]
    public async Task The_same_email_read_twice_creates_one_ticket()
    {
        var email = FakeMailbox.Email();
        await PollWith(email);

        var second = await PollWith(email);

        Assert.Equal(0, second.Tickets);
        Assert.Equal(1, Scalar<int>(
            "SELECT COUNT(*) FROM dbo.TicketMessages WHERE InternetMessageId = @id", new { id = email.InternetMessageId }));
    }

    [Fact]
    public async Task Reply_in_the_same_thread_is_added_to_the_ticket()
    {
        var first = await ReceiveAsync(FakeMailbox.Email(conversationId: "thread-" + Guid.NewGuid()));

        var summary = await PollWith(FakeMailbox.Email(
            subject: "Re: I need help", text: "One more detail.", conversationId: first.ConversationId));

        Assert.Equal(1, summary.Replies);
        var ticket = await TicketForAsync(first);
        Assert.Equal(2, ticket.Messages.Count);
        Assert.Equal("One more detail.", ticket.Messages[1].BodyText);
        Assert.Equal(TicketEventType.ReplyReceived, ticket.Events.Last().Type);
        Assert.Null(ticket.Events.Last().ActorUserId);
    }

    [Fact]
    public async Task Reply_naming_an_earlier_message_is_added_even_in_a_new_thread()
    {
        var first = await ReceiveAsync(FakeMailbox.Email());

        await PollWith(FakeMailbox.Email(text: "Following up.", inReplyTo: first.InternetMessageId));

        Assert.Equal(2, (await TicketForAsync(first)).Messages.Count);
    }

    [Fact]
    public async Task Quoted_history_is_stripped_from_a_reply()
    {
        var first = await ReceiveAsync(FakeMailbox.Email());

        await PollWith(FakeMailbox.Email(
            text: "Yes, that fixed it.\n\nOn Mon, 2 Nov 2026 at 10:15, Support <support@helpdesk.test> wrote:\n> Please try again.",
            conversationId: first.ConversationId));

        Assert.Equal("Yes, that fixed it.", (await TicketForAsync(first)).Messages[1].BodyText);
    }

    [Fact]
    public async Task Reply_to_a_resolved_ticket_reopens_it()
    {
        var first = await ReceiveAsync(FakeMailbox.Email());
        var id = (await TicketForAsync(first)).Id;
        var admin = await _factory.SignInAsAdminAsync();
        await admin.PutAsJsonAsync($"/api/tickets/{id}/status", new { status = "Resolved" });

        var summary = await PollWith(FakeMailbox.Email(text: "It broke again.", conversationId: first.ConversationId));

        Assert.Equal(1, summary.Replies);
        var ticket = (await _tickets.GetAsync(id, CancellationToken.None))!;
        Assert.Equal(TicketStatus.Open, ticket.Status);
        Assert.Null(ticket.ResolvedAt);
        var reopened = ticket.Events.Last();
        Assert.Equal((TicketEventType.StatusChanged, "Resolved", "Open"), (reopened.Type, reopened.FromValue, reopened.ToValue));
        Assert.Null(reopened.ActorUserId);
    }

    [Fact]
    public async Task Reply_to_a_closed_ticket_creates_a_linked_ticket()
    {
        var first = await ReceiveAsync(FakeMailbox.Email(subject: "Original problem"));
        var originalId = (await TicketForAsync(first)).Id;
        var admin = await _factory.SignInAsAdminAsync();
        await admin.PutAsJsonAsync($"/api/tickets/{originalId}/status", new { status = "Closed" });

        var reply = FakeMailbox.Email(subject: "Re: Original problem", text: "Sorry, it is back.", conversationId: first.ConversationId);
        var summary = await PollWith(reply);

        Assert.Equal(1, summary.Tickets);
        var original = (await _tickets.GetAsync(originalId, CancellationToken.None))!;
        Assert.Equal(TicketStatus.Closed, original.Status);
        Assert.Single(original.Messages);
        var followUp = await TicketForAsync(reply);
        Assert.NotEqual(originalId, followUp.Id);
        Assert.Equal(originalId, followUp.OriginalTicketId);
        Assert.Equal(TicketStatus.Open, followUp.Status);
    }

    [Fact]
    public async Task Later_replies_in_that_thread_go_to_the_follow_up_ticket()
    {
        var first = await ReceiveAsync(FakeMailbox.Email());
        var originalId = (await TicketForAsync(first)).Id;
        var admin = await _factory.SignInAsAdminAsync();
        await admin.PutAsJsonAsync($"/api/tickets/{originalId}/status", new { status = "Closed" });
        var followUpEmail = await ReceiveAsync(FakeMailbox.Email(conversationId: first.ConversationId));

        await PollWith(FakeMailbox.Email(text: "And another thing.", conversationId: first.ConversationId));

        Assert.Equal(2, (await TicketForAsync(followUpEmail)).Messages.Count);
    }

    [Fact]
    public async Task Automated_email_is_skipped_and_does_not_reopen_a_ticket()
    {
        var first = await ReceiveAsync(FakeMailbox.Email());
        var id = (await TicketForAsync(first)).Id;
        var admin = await _factory.SignInAsAdminAsync();
        await admin.PutAsJsonAsync($"/api/tickets/{id}/status", new { status = "Resolved" });
        var outOfOffice = FakeMailbox.Email(
            subject: "Automatic reply: I need help",
            conversationId: first.ConversationId,
            headers: new() { ["Auto-Submitted"] = "auto-replied" });

        var summary = await PollWith(outOfOffice);

        Assert.Equal(new IngestionSummary(Read: 1, Tickets: 0, Replies: 0, Skipped: 1, Failed: 0), summary);
        var ticket = (await _tickets.GetAsync(id, CancellationToken.None))!;
        Assert.Equal(TicketStatus.Resolved, ticket.Status);
        Assert.Single(ticket.Messages);
        Assert.Equal("SkippedAutomated", Scalar<string>(
            "SELECT Outcome FROM dbo.InboundEmails WHERE InternetMessageId = @id", new { id = outOfOffice.InternetMessageId }));
    }

    [Fact]
    public async Task Email_from_the_mailbox_itself_is_skipped()
    {
        var own = FakeMailbox.Email(from: FakeMailbox.Address);

        var summary = await PollWith(own);

        Assert.Equal(1, summary.Skipped);
        Assert.Equal("SkippedOwnMessage", Scalar<string>(
            "SELECT Outcome FROM dbo.InboundEmails WHERE InternetMessageId = @id", new { id = own.InternetMessageId }));
    }

    [Fact]
    public async Task One_bad_email_does_not_block_the_others()
    {
        // An address too long for the column makes this email fail in the database.
        var bad = FakeMailbox.Email(from: new string('x', 400) + "@example.com");
        var good = FakeMailbox.Email(subject: "Fine");

        var summary = await PollWith(bad, good);

        Assert.Equal(new IngestionSummary(Read: 2, Tickets: 1, Replies: 0, Skipped: 0, Failed: 1), summary);
        Assert.Equal("Fine", (await TicketForAsync(good)).Subject);
        Assert.Equal("Failed", Scalar<string>(
            "SELECT Outcome FROM dbo.InboundEmails WHERE InternetMessageId = @id", new { id = bad.InternetMessageId }));
    }

    [Fact]
    public async Task First_read_starts_from_now_and_later_reads_continue_from_the_saved_position()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        await _ingestion.PollAsync(CancellationToken.None);
        await _ingestion.PollAsync(CancellationToken.None);

        var first = _mailbox.Reads[0];
        var second = _mailbox.Reads[1];
        Assert.Null(first.Cursor);
        Assert.InRange(first.Since, before, DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.NotNull(second.Cursor);
        Assert.StartsWith("cursor-", second.Cursor);
        // The start time is fixed by the first read and never moves.
        Assert.Equal(first.Since.ToUnixTimeMilliseconds(), second.Since.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Agent_reply_is_queued_then_emailed_in_the_requesters_thread()
    {
        var email = await ReceiveAsync(FakeMailbox.Email(subject: "Login problem", from: "maya@example.com"));
        var id = (await TicketForAsync(email)).Id;
        var admin = await _factory.SignInAsAdminAsync();

        var response = await admin.PostAsJsonAsync($"/api/tickets/{id}/replies", new { body = "Please reset your password." });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var messageId = Assert.Single(_factory.ReplyQueue.Enqueued);
        var pending = (await _tickets.GetAsync(id, CancellationToken.None))!.Messages.Last();
        Assert.Equal(MessageSendStatus.Pending, pending.SendStatus);
        Assert.Empty(_mailbox.Sent);

        await _sender.SendAsync(messageId, CancellationToken.None);

        var sent = Assert.Single(_mailbox.Sent);
        Assert.Equal("maya@example.com", sent.ToEmail);
        Assert.Equal("Re: Login problem", sent.Subject);
        Assert.Equal("Please reset your password.", sent.BodyText);
        Assert.Equal(email.ProviderMessageId, sent.ReplyToProviderMessageId);
        Assert.Equal(MessageSendStatus.Sent, (await _tickets.GetAsync(id, CancellationToken.None))!.Messages.Last().SendStatus);
    }

    [Fact]
    public async Task Sending_twice_emails_once()
    {
        var email = await ReceiveAsync(FakeMailbox.Email());
        var id = (await TicketForAsync(email)).Id;
        var admin = await _factory.SignInAsAdminAsync();
        await admin.PostAsJsonAsync($"/api/tickets/{id}/replies", new { body = "Answer." });
        var messageId = Assert.Single(_factory.ReplyQueue.Enqueued);

        await _sender.SendAsync(messageId, CancellationToken.None);
        await _sender.SendAsync(messageId, CancellationToken.None);

        Assert.Single(_mailbox.Sent);
    }

    [Fact]
    public async Task Requesters_answer_to_a_sent_reply_returns_to_the_same_ticket()
    {
        var email = await ReceiveAsync(FakeMailbox.Email());
        var id = (await TicketForAsync(email)).Id;
        var admin = await _factory.SignInAsAdminAsync();
        await admin.PostAsJsonAsync($"/api/tickets/{id}/replies", new { body = "Does this help?", resolve = true });
        await _sender.SendAsync(Assert.Single(_factory.ReplyQueue.Enqueued), CancellationToken.None);
        var sentMessageId = Scalar<string>(
            "SELECT TOP (1) InternetMessageId FROM dbo.TicketMessages WHERE TicketId = @id AND Direction = N'Outbound'", new { id });

        // The answer names our reply in In-Reply-To but arrives with a thread id we have never seen.
        await PollWith(FakeMailbox.Email(text: "No, still broken.", inReplyTo: sentMessageId));

        var ticket = (await _tickets.GetAsync(id, CancellationToken.None))!;
        Assert.Equal(3, ticket.Messages.Count);
        Assert.Equal(TicketStatus.Open, ticket.Status);
    }

    [Fact]
    public async Task Failed_sends_are_retried_then_marked_failed_and_can_be_retried_by_hand()
    {
        var email = await ReceiveAsync(FakeMailbox.Email());
        var id = (await TicketForAsync(email)).Id;
        var admin = await _factory.SignInAsAdminAsync();
        await admin.PostAsJsonAsync($"/api/tickets/{id}/replies", new { body = "Answer." });
        var messageId = Assert.Single(_factory.ReplyQueue.Enqueued);
        _mailbox.SendFailure = new InvalidOperationException("Mail server unavailable");

        // Each attempt but the last throws, which is what makes the job runner try again.
        for (var attempt = 1; attempt < ReplySender.MaxAttempts; attempt++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => _sender.SendAsync(messageId, CancellationToken.None));
            Assert.Equal(MessageSendStatus.Pending, (await _tickets.GetAsync(id, CancellationToken.None))!.Messages.Last().SendStatus);
        }

        await _sender.SendAsync(messageId, CancellationToken.None);

        var failed = (await _tickets.GetAsync(id, CancellationToken.None))!.Messages.Last();
        Assert.Equal(MessageSendStatus.Failed, failed.SendStatus);
        Assert.Equal("Mail server unavailable", failed.SendError);

        _mailbox.SendFailure = null;
        _factory.ReplyQueue.Enqueued.Clear();
        var retry = await admin.PostAsync($"/api/tickets/{id}/messages/{messageId}/retry-send", null);

        Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
        Assert.Equal([messageId], _factory.ReplyQueue.Enqueued);
        await _sender.SendAsync(messageId, CancellationToken.None);
        Assert.Equal(MessageSendStatus.Sent, (await _tickets.GetAsync(id, CancellationToken.None))!.Messages.Last().SendStatus);
        Assert.Single(_mailbox.Sent);
    }

    [Fact]
    public async Task Only_a_failed_reply_can_be_retried()
    {
        var email = await ReceiveAsync(FakeMailbox.Email());
        var id = (await TicketForAsync(email)).Id;
        var admin = await _factory.SignInAsAdminAsync();
        await admin.PostAsJsonAsync($"/api/tickets/{id}/replies", new { body = "Answer." });
        var messageId = Assert.Single(_factory.ReplyQueue.Enqueued);

        var pending = await admin.PostAsync($"/api/tickets/{id}/messages/{messageId}/retry-send", null);
        var wrongTicket = await admin.PostAsync($"/api/tickets/1/messages/{messageId}/retry-send", null);

        Assert.Equal(HttpStatusCode.NotFound, pending.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongTicket.StatusCode);
    }

    [Fact]
    public async Task Reply_stored_without_sending_is_never_emailed()
    {
        var admin = await _factory.SignInAsAdminAsync();
        var me = await admin.GetFromJsonAsync<UserDto>("/api/auth/me");
        var id = await _factory.CreateTicketAsync();
        var (_, messageId) = await _tickets.AddAgentReplyAsync(id, me!.Id, "Stored only.", resolve: false, send: false, CancellationToken.None);

        await _sender.SendAsync(messageId!.Value, CancellationToken.None);

        Assert.Empty(_mailbox.Sent);
        Assert.Equal(MessageSendStatus.Skipped, (await _tickets.GetAsync(id, CancellationToken.None))!.Messages.Last().SendStatus);
    }

    [Fact]
    public async Task Ticket_with_no_incoming_email_is_answered_in_a_new_thread()
    {
        var admin = await _factory.SignInAsAdminAsync();
        var id = await _factory.CreateTicketAsync(subject: "Created by hand");
        await admin.PostAsJsonAsync($"/api/tickets/{id}/replies", new { body = "Hello." });

        await _sender.SendAsync(Assert.Single(_factory.ReplyQueue.Enqueued), CancellationToken.None);

        var sent = Assert.Single(_mailbox.Sent);
        Assert.Null(sent.ReplyToProviderMessageId);
        Assert.Equal("Re: Created by hand", sent.Subject);
    }
}
