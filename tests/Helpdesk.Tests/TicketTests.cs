using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Helpdesk.Core.Tickets;

namespace Helpdesk.Tests;

[Collection(ApiCollection.Name)]
public class TicketTests(AuthApiFactory factory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    // Other tests add tickets to the same database, so list tests search for a token unique to the test.
    private static string Token() => "tok" + Guid.NewGuid().ToString("N")[..12];

    private static async Task<TicketListDto> ListAsync(HttpClient client, string query) =>
        (await client.GetFromJsonAsync<TicketListDto>("/api/tickets?" + query, Json))!;

    private static async Task<TicketDetailDto> GetAsync(HttpClient client, int id) =>
        (await client.GetFromJsonAsync<TicketDetailDto>($"/api/tickets/{id}", Json))!;

    [Fact]
    public async Task Tickets_require_sign_in()
    {
        var anonymous = factory.CreateBrowser();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/tickets")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/tickets/1000")).StatusCode);
    }

    [Fact]
    public async Task Agents_see_all_tickets()
    {
        var admin = await factory.SignInAsAdminAsync();
        var agent = await factory.CreateAgentAsync(admin);
        var client = await factory.SignInAsync(agent.Email, agent.Password);
        var token = Token();
        var id = await factory.CreateTicketAsync($"Visible {token}");

        var page = await ListAsync(client, $"search={token}");

        Assert.Equal(id, Assert.Single(page.Items).Id);
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task List_pages_through_results_newest_first_by_default()
    {
        var admin = await factory.SignInAsAdminAsync();
        var token = Token();
        var start = DateTimeOffset.UtcNow.AddDays(-1);
        var ids = new List<int>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await factory.CreateTicketAsync($"Paging {token} {i}", receivedAt: start.AddMinutes(i)));
        }

        var first = await ListAsync(admin, $"search={token}&take=2");
        var second = await ListAsync(admin, $"search={token}&take=2&skip=2");
        var last = await ListAsync(admin, $"search={token}&take=2&skip=4");

        Assert.Equal(5, first.TotalCount);
        Assert.Equal([ids[4], ids[3]], first.Items.Select(t => t.Id));
        Assert.Equal([ids[2], ids[1]], second.Items.Select(t => t.Id));
        Assert.Equal([ids[0]], last.Items.Select(t => t.Id));
    }

    [Fact]
    public async Task List_sorts_by_the_requested_column()
    {
        var admin = await factory.SignInAsAdminAsync();
        var token = Token();
        await factory.CreateTicketAsync($"{token} Banana");
        await factory.CreateTicketAsync($"{token} Apple");
        await factory.CreateTicketAsync($"{token} Cherry");

        var ascending = await ListAsync(admin, $"search={token}&sort=subject");
        var descending = await ListAsync(admin, $"search={token}&sort=subject&desc=true");

        Assert.Equal(["Apple", "Banana", "Cherry"], ascending.Items.Select(t => t.Subject.Split(' ')[1]));
        Assert.Equal(["Cherry", "Banana", "Apple"], descending.Items.Select(t => t.Subject.Split(' ')[1]));
    }

    [Fact]
    public async Task List_filters_by_status()
    {
        var admin = await factory.SignInAsAdminAsync();
        var token = Token();
        var open = await factory.CreateTicketAsync($"{token} open");
        var resolved = await factory.CreateTicketAsync($"{token} resolved");
        var closed = await factory.CreateTicketAsync($"{token} closed");
        await admin.PutAsJsonAsync($"/api/tickets/{resolved}/status", new { status = "Resolved" });
        await admin.PutAsJsonAsync($"/api/tickets/{closed}/status", new { status = "Closed" });

        var onlyOpen = await ListAsync(admin, $"search={token}&status=Open");
        var finished = await ListAsync(admin, $"search={token}&status=Resolved&status=Closed");

        Assert.Equal([open], onlyOpen.Items.Select(t => t.Id));
        Assert.Equal(new[] { resolved, closed }.Order(), finished.Items.Select(t => t.Id).Order());
    }

    [Fact]
    public async Task List_filters_by_category_and_unclassified()
    {
        var admin = await factory.SignInAsAdminAsync();
        var token = Token();
        var refund = await factory.CreateTicketAsync($"{token} refund", TicketCategory.RefundRequest);
        await factory.CreateTicketAsync($"{token} technical", TicketCategory.TechnicalQuestion);
        var unclassified = await factory.CreateTicketAsync($"{token} unknown");

        var refunds = await ListAsync(admin, $"search={token}&category=RefundRequest");
        var none = await ListAsync(admin, $"search={token}&category=unclassified");

        Assert.Equal([refund], refunds.Items.Select(t => t.Id));
        Assert.Equal([unclassified], none.Items.Select(t => t.Id));
    }

    [Fact]
    public async Task Search_matches_the_requester_and_treats_wildcards_as_text()
    {
        var admin = await factory.SignInAsAdminAsync();
        var token = Token();
        var id = await factory.CreateTicketAsync("Plain subject", requesterEmail: $"{token}@example.com");
        await factory.CreateTicketAsync($"100% {token} done");

        var byEmail = await ListAsync(admin, $"search={token}@example");
        var wildcard = await ListAsync(admin, $"search={Uri.EscapeDataString($"0_ {token}")}");

        Assert.Equal([id], byEmail.Items.Select(t => t.Id));
        Assert.Empty(wildcard.Items);
    }

    [Theory]
    [InlineData("sort=password")]
    [InlineData("sort=1")]
    [InlineData("category=nonsense")]
    [InlineData("assignee=someone")]
    [InlineData("take=0")]
    [InlineData("take=101")]
    [InlineData("skip=-1")]
    [InlineData("status=Nonsense")]
    public async Task List_rejects_invalid_query_values(string query)
    {
        var admin = await factory.SignInAsAdminAsync();

        var response = await admin.GetAsync("/api/tickets?" + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Detail_returns_the_conversation_and_history()
    {
        var admin = await factory.SignInAsAdminAsync();
        var id = await factory.CreateTicketAsync("Need help", TicketCategory.TechnicalQuestion);

        var ticket = await GetAsync(admin, id);

        Assert.Equal("Need help", ticket.Subject);
        Assert.Equal(TicketStatus.Open, ticket.Status);
        Assert.Equal(TicketCategory.TechnicalQuestion, ticket.Category);
        Assert.True(ticket.AcceptsChanges);
        Assert.Equal([TicketStatus.Resolved, TicketStatus.Closed], ticket.AllowedStatuses);
        var message = Assert.Single(ticket.Messages);
        Assert.Equal(MessageDirection.Inbound, message.Direction);
        Assert.Equal("Hello, I need help.", message.BodyText);
        Assert.Null(message.BodyHtml);
        Assert.Equal(TicketEventType.Created, Assert.Single(ticket.Events).Type);
    }

    [Fact]
    public async Task Detail_returns_sanitised_html()
    {
        var admin = await factory.SignInAsAdminAsync();
        var id = await factory.CreateTicketAsync(
            bodyHtml: "<p onclick=\"steal()\">Hi <b>there</b></p><script>steal()</script><img src=\"https://t.example/p.gif\">");

        var ticket = await GetAsync(admin, id);

        Assert.Equal("<p>Hi <b>there</b></p>", Assert.Single(ticket.Messages).BodyHtml);
    }

    [Fact]
    public async Task Unknown_ticket_returns_not_found()
    {
        var admin = await factory.SignInAsAdminAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/tickets/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.PutAsJsonAsync("/api/tickets/1/status", new { status = "Resolved" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.PostAsJsonAsync("/api/tickets/1/replies", new { body = "Hello" })).StatusCode);
    }

    [Fact]
    public async Task Status_change_is_recorded_in_the_history()
    {
        var admin = await factory.SignInAsAdminAsync();
        var id = await factory.CreateTicketAsync();

        var response = await admin.PutAsJsonAsync($"/api/tickets/{id}/status", new { status = "Resolved" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var ticket = await GetAsync(admin, id);
        Assert.Equal(TicketStatus.Resolved, ticket.Status);
        Assert.NotNull(ticket.ResolvedAt);
        var change = ticket.Events.Last();
        Assert.Equal(TicketEventType.StatusChanged, change.Type);
        Assert.Equal(("Open", "Resolved", "Test Admin"), (change.From, change.To, change.ActorName));
    }

    [Fact]
    public async Task Resolved_ticket_can_be_reopened()
    {
        var admin = await factory.SignInAsAdminAsync();
        var id = await factory.CreateTicketAsync();
        await admin.PutAsJsonAsync($"/api/tickets/{id}/status", new { status = "Resolved" });

        var response = await admin.PutAsJsonAsync($"/api/tickets/{id}/status", new { status = "Open" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var ticket = await GetAsync(admin, id);
        Assert.Equal(TicketStatus.Open, ticket.Status);
        Assert.Null(ticket.ResolvedAt);
    }

    [Fact]
    public async Task Closed_ticket_is_final()
    {
        var admin = await factory.SignInAsAdminAsync();
        var id = await factory.CreateTicketAsync();
        await admin.PutAsJsonAsync($"/api/tickets/{id}/status", new { status = "Closed" });

        var reopen = await admin.PutAsJsonAsync($"/api/tickets/{id}/status", new { status = "Open" });
        var recategorise = await admin.PutAsJsonAsync($"/api/tickets/{id}/category", new { category = "RefundRequest" });
        var reply = await admin.PostAsJsonAsync($"/api/tickets/{id}/replies", new { body = "One more thing" });

        Assert.Equal(HttpStatusCode.Conflict, reopen.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, recategorise.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, reply.StatusCode);
        var ticket = await GetAsync(admin, id);
        Assert.Equal(TicketStatus.Closed, ticket.Status);
        Assert.False(ticket.AcceptsChanges);
        Assert.Empty(ticket.AllowedStatuses);
        Assert.Single(ticket.Messages);
    }

    [Fact]
    public async Task Agent_cannot_set_needs_human_review()
    {
        var admin = await factory.SignInAsAdminAsync();
        var id = await factory.CreateTicketAsync();

        var response = await admin.PutAsJsonAsync($"/api/tickets/{id}/status", new { status = "NeedsHumanReview" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData("{\"status\":\"Nonsense\"}")]
    [InlineData("{\"status\":2}")]
    [InlineData("{}")]
    public async Task Status_change_rejects_invalid_values(string json)
    {
        var admin = await factory.SignInAsAdminAsync();
        var id = await factory.CreateTicketAsync();

        var response = await admin.PutAsync(
            $"/api/tickets/{id}/status", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(TicketStatus.Open, (await GetAsync(admin, id)).Status);
    }

    [Fact]
    public async Task Category_change_is_recorded_in_the_history()
    {
        var admin = await factory.SignInAsAdminAsync();
        var id = await factory.CreateTicketAsync();

        var response = await admin.PutAsJsonAsync($"/api/tickets/{id}/category", new { category = "RefundRequest" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var ticket = await GetAsync(admin, id);
        Assert.Equal(TicketCategory.RefundRequest, ticket.Category);
        var change = Assert.Single(ticket.Events, e => e.Type == TicketEventType.CategoryChanged);
        Assert.Equal(((string?)null, "RefundRequest"), (change.From, change.To));
    }

    [Fact]
    public async Task Setting_the_same_category_again_adds_no_history()
    {
        var admin = await factory.SignInAsAdminAsync();
        var id = await factory.CreateTicketAsync(category: TicketCategory.RefundRequest);

        await admin.PutAsJsonAsync($"/api/tickets/{id}/category", new { category = "RefundRequest" });

        Assert.Single((await GetAsync(admin, id)).Events);
    }

    [Fact]
    public async Task Reply_is_added_to_the_conversation_under_the_agents_name()
    {
        var admin = await factory.SignInAsAdminAsync();
        var id = await factory.CreateTicketAsync();

        var response = await admin.PostAsJsonAsync($"/api/tickets/{id}/replies", new { body = "  Here is the answer.  " });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var ticket = await GetAsync(admin, id);
        Assert.Equal(TicketStatus.Open, ticket.Status);
        var reply = ticket.Messages.Last();
        Assert.Equal(MessageDirection.Outbound, reply.Direction);
        Assert.Equal("Here is the answer.", reply.BodyText);
        Assert.Equal("Test Admin", reply.AuthorName);
        Assert.Equal(TicketEventType.ReplyAdded, ticket.Events.Last().Type);
    }

    [Fact]
    public async Task Reply_and_resolve_does_both()
    {
        var admin = await factory.SignInAsAdminAsync();
        var id = await factory.CreateTicketAsync();

        var response = await admin.PostAsJsonAsync($"/api/tickets/{id}/replies", new { body = "All fixed.", resolve = true });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var ticket = await GetAsync(admin, id);
        Assert.Equal(TicketStatus.Resolved, ticket.Status);
        Assert.Equal(2, ticket.Messages.Count);
        Assert.Equal(
            [TicketEventType.Created, TicketEventType.ReplyAdded, TicketEventType.StatusChanged],
            ticket.Events.Select(e => e.Type));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_reply_is_rejected(string body)
    {
        var admin = await factory.SignInAsAdminAsync();
        var id = await factory.CreateTicketAsync();

        var response = await admin.PostAsJsonAsync($"/api/tickets/{id}/replies", new { body });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Single((await GetAsync(admin, id)).Messages);
    }

    [Fact]
    public async Task Overlong_reply_is_rejected()
    {
        var admin = await factory.SignInAsAdminAsync();
        var id = await factory.CreateTicketAsync();

        var response = await admin.PostAsJsonAsync($"/api/tickets/{id}/replies", new { body = new string('x', 20_001) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private sealed record TicketListDto(List<TicketSummaryDto> Items, int TotalCount);

    private sealed record TicketSummaryDto(int Id, string Subject, TicketStatus Status, TicketCategory? Category);

    private sealed record TicketDetailDto(
        int Id,
        string Subject,
        TicketStatus Status,
        TicketCategory? Category,
        DateTimeOffset? ResolvedAt,
        bool AcceptsChanges,
        List<TicketStatus> AllowedStatuses,
        List<MessageDto> Messages,
        List<EventDto> Events);

    private sealed record MessageDto(MessageDirection Direction, string BodyText, string? BodyHtml, string? AuthorName);

    private sealed record EventDto(TicketEventType Type, string? From, string? To, string? ActorName);
}
