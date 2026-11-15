using System.Net;
using System.Net.Http.Json;
using Dapper;
using Helpdesk.Core.Tickets;
using Helpdesk.Infrastructure.Data;
using Helpdesk.Infrastructure.Tickets;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Tests;

[Collection(ApiCollection.Name)]
public class DashboardTests(AuthApiFactory factory)
{
    private void Execute(string sql, object? parameters = null)
    {
        using var connection = factory.Services.GetRequiredService<IDbConnectionFactory>().OpenAsync().GetAwaiter().GetResult();
        connection.Execute(sql, parameters);
    }

    /// <summary>
    /// A week in the distant past that no other test writes to. Period figures count tickets by
    /// creation date, so tickets placed here can be counted exactly. Each call gives a new week.
    /// </summary>
    private static DateTimeOffset QuietWeek() =>
        new DateTimeOffset(1990, 1, 1, 9, 0, 0, TimeSpan.Zero).AddDays(21 * Interlocked.Increment(ref _weeksUsed));

    private static int _weeksUsed;

    private static string Range(DateTimeOffset start) =>
        $"from={start:yyyy-MM-dd}&to={start.AddDays(6):yyyy-MM-dd}";

    private static async Task<DashboardDto> GetAsync(HttpClient client, string query) =>
        (await client.GetFromJsonAsync<DashboardDto>("/api/dashboard?" + query))!;

    [Fact]
    public async Task Dashboard_is_for_admins_only()
    {
        var admin = await factory.SignInAsAdminAsync();
        var agent = await factory.CreateAgentAsync(admin);
        var agentClient = await factory.SignInAsync(agent.Email, agent.Password);

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateBrowser().GetAsync("/api/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agentClient.GetAsync("/api/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/dashboard")).StatusCode);
    }

    [Fact]
    public async Task Without_dates_the_period_is_the_last_thirty_days()
    {
        var admin = await factory.SignInAsAdminAsync();

        var dashboard = await GetAsync(admin, "");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.InRange(dashboard.To, today.AddDays(-1), today);
        Assert.Equal(29, dashboard.To.DayNumber - dashboard.From.DayNumber);
    }

    [Theory]
    [InlineData("from=2026-03-10&to=2026-03-01")]
    [InlineData("from=2024-01-01&to=2026-01-01")]
    [InlineData("from=yesterday")]
    public async Task Invalid_period_is_rejected(string query)
    {
        var admin = await factory.SignInAsAdminAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/dashboard?" + query)).StatusCode);
    }

    [Fact]
    public async Task Period_counts_tickets_by_the_day_they_were_created()
    {
        var admin = await factory.SignInAsAdminAsync();
        var start = QuietWeek();
        await factory.CreateTicketAsync(receivedAt: start);
        await factory.CreateTicketAsync(receivedAt: start.AddHours(3));
        await factory.CreateTicketAsync(receivedAt: start.AddDays(2));
        // Just outside: the day before the first day, and the day after the last.
        await factory.CreateTicketAsync(receivedAt: start.AddDays(-1));
        await factory.CreateTicketAsync(receivedAt: start.AddDays(7));

        var dashboard = await GetAsync(admin, Range(start));

        Assert.Equal(3, dashboard.Data.Tickets.Created);
        Assert.Equal(
            [(DateOnly.FromDateTime(start.UtcDateTime), 2), (DateOnly.FromDateTime(start.AddDays(2).UtcDateTime), 1)],
            dashboard.Data.DailyVolume.Select(day => (DateOnly.FromDateTime(day.Day), day.Created)));
    }

    [Fact]
    public async Task Response_and_resolution_times_are_medians()
    {
        var admin = await factory.SignInAsAdminAsync();
        var me = await admin.GetFromJsonAsync<UserDto>("/api/auth/me");
        var start = QuietWeek();

        // Answered after 10, 20 and 60 minutes; one never answered. Resolved after 1 and 3 hours.
        var minutesToReply = new[] { 10, 20, 60 };
        for (var i = 0; i < 4; i++)
        {
            var id = await factory.CreateTicketAsync(receivedAt: start.AddHours(i));
            if (i == 3)
            {
                continue;
            }

            await factory.Tickets.AddAgentReplyAsync(id, me!.Id, "An answer.", resolve: i < 2, send: false, CancellationToken.None);
            Execute(
                """
                UPDATE dbo.TicketMessages SET CreatedAt = DATEADD(MINUTE, @minutes, (SELECT CreatedAt FROM dbo.Tickets WHERE Id = @id))
                WHERE TicketId = @id AND Direction = N'Outbound';
                UPDATE dbo.Tickets SET ResolvedAt = DATEADD(HOUR, @hours, CreatedAt) WHERE Id = @id AND ResolvedAt IS NOT NULL;
                """,
                new { id, minutes = minutesToReply[i], hours = i == 0 ? 1 : 3 });
        }

        var tickets = (await GetAsync(admin, Range(start))).Data.Tickets;

        Assert.Equal((4, 3, 2), (tickets.Created, tickets.Answered, tickets.Resolved));
        Assert.Equal(20 * 60, tickets.MedianFirstResponseSeconds);
        Assert.Equal(2 * 3600, tickets.MedianResolutionSeconds);
    }

    [Fact]
    public async Task Period_with_no_tickets_has_no_times()
    {
        var admin = await factory.SignInAsAdminAsync();

        var data = (await GetAsync(admin, Range(QuietWeek()))).Data;

        Assert.Equal(0, data.Tickets.Created);
        Assert.Null(data.Tickets.MedianFirstResponseSeconds);
        Assert.Null(data.Tickets.MedianResolutionSeconds);
        Assert.Empty(data.DailyVolume);
        Assert.Equal(0, data.Ai.ClassifiedByAi);
    }

    [Fact]
    public async Task Ai_figures_count_overrides_draft_outcomes_auto_answers_and_escalations()
    {
        var admin = await factory.SignInAsAdminAsync();
        var start = QuietWeek();
        var agreed = await factory.CreateTicketAsync(receivedAt: start);
        var overridden = await factory.CreateTicketAsync(receivedAt: start);
        var unsure = await factory.CreateTicketAsync(receivedAt: start);
        var failed = await factory.CreateTicketAsync(receivedAt: start);
        var autoAnswered = await factory.CreateTicketAsync(receivedAt: start);

        Execute(
            """
            UPDATE dbo.Tickets SET AiCategory = N'GeneralQuestion', Category = N'GeneralQuestion', ClassificationStatus = N'Done' WHERE Id = @agreed;
            UPDATE dbo.Tickets SET AiCategory = N'GeneralQuestion', Category = N'RefundRequest', ClassificationStatus = N'Done' WHERE Id = @overridden;
            UPDATE dbo.Tickets SET AiCategory = N'TechnicalQuestion', Category = NULL, ClassificationStatus = N'Done' WHERE Id = @unsure;
            UPDATE dbo.Tickets SET ClassificationStatus = N'Failed' WHERE Id = @failed;

            INSERT INTO dbo.TicketDrafts (TicketId, Body, Confidence, Status, GateDecision, BasedOnMessageCount, CreatedAt)
            VALUES (@agreed, N'd', 0.9, N'Sent', N'Hold', 1, @at), (@overridden, N'd', 0.9, N'Edited', N'Hold', 1, @at),
                   (@unsure, N'd', 0.9, N'Edited', N'WouldSend', 1, @at), (@failed, N'd', 0.9, N'Discarded', N'Hold', 1, @at),
                   (@failed, N'd', 0.9, N'Superseded', N'Hold', 1, @at);

            INSERT INTO dbo.TicketMessages (TicketId, Direction, BodyText, CreatedAt, SendStatus, IsAiGenerated)
            VALUES (@autoAnswered, N'Outbound', N'An automatic answer.', @at, N'Sent', 1);

            INSERT INTO dbo.AiRuns (TicketId, Purpose, Model, InputTokens, OutputTokens, LatencyMs, Outcome, CreatedAt)
            VALUES (@agreed, N'Classification', N'm', 100, 10, 5, N'Succeeded', @at), (@failed, N'Classification', N'm', NULL, NULL, 5, N'Failed', @at),
                   (@agreed, N'Draft', N'm', 900, 200, 5, N'Succeeded', @at);
            """,
            new { agreed, overridden, unsure, failed, autoAnswered, at = start.AddMinutes(5) });
        await factory.Tickets.EscalateAsync(failed, "A reason.", CancellationToken.None);

        var data = (await GetAsync(admin, Range(start))).Data;

        Assert.Equal(
            new AiDto(
                ClassifiedByAi: 3, ClassificationsOverridden: 1, ClassificationsFailed: 1,
                DraftsSentUnchanged: 1, DraftsSentEdited: 2, DraftsDiscarded: 1,
                AnsweredAutomatically: 1, WouldHaveBeenAnsweredAutomatically: 1, Escalated: 1),
            data.Ai);
        Assert.Equal(
            [new UsageDto("Classification", 2, 1, 100, 10), new UsageDto("Draft", 1, 0, 900, 200)],
            data.AiUsage);
    }

    [Fact]
    public async Task Current_figures_show_open_work_and_what_needs_attention()
    {
        var admin = await factory.SignInAsAdminAsync();
        var me = await admin.GetFromJsonAsync<UserDto>("/api/auth/me");
        var before = (await GetAsync(admin, "")).Data;

        var open = await factory.CreateTicketAsync(subject: "Needs an owner");
        var resolved = await factory.CreateTicketAsync();
        await admin.PutAsJsonAsync($"/api/tickets/{resolved}/status", new { status = "Resolved" });
        var closed = await factory.CreateTicketAsync();
        await admin.PutAsJsonAsync($"/api/tickets/{closed}/status", new { status = "Closed" });
        var (_, messageId) = await factory.Tickets.AddAgentReplyAsync(open, me!.Id, "Lost in the post.", resolve: false, send: true, CancellationToken.None);
        Execute("UPDATE dbo.TicketMessages SET SendStatus = N'Failed' WHERE Id = @messageId", new { messageId });

        var after = (await GetAsync(admin, "")).Data;

        int Count(DataDto data, string status) => data.OpenByStatus.SingleOrDefault(row => row.Status == status)?.Count ?? 0;
        Assert.Equal(Count(before, "Open") + 1, Count(after, "Open"));
        Assert.Equal(Count(before, "Resolved") + 1, Count(after, "Resolved"));
        Assert.DoesNotContain(after.OpenByStatus, row => row.Status == "Closed");

        // The tickets went to General Support, which has no members in this test, so they are unassigned.
        int Active(DataDto data) => data.ActiveByTeam.SingleOrDefault(row => row.Team == "General Support")?.Active ?? 0;
        Assert.Equal(Active(before) + 1, Active(after));
        Assert.Equal(before.Attention.UnassignedTickets + 1, after.Attention.UnassignedTickets);
        Assert.Equal(before.Attention.FailedSends + 1, after.Attention.FailedSends);
        Assert.Contains(after.Attention.FailedSendTickets, ticket => ticket.Id == open && ticket.Subject == "Needs an owner");
    }

    [Fact]
    public async Task Shadow_replies_awaiting_review_are_listed()
    {
        var admin = await factory.SignInAsAdminAsync();
        var id = await factory.CreateTicketAsync(subject: "Shadow reply here");
        Execute(
            """
            INSERT INTO dbo.TicketDrafts (TicketId, Body, Confidence, Status, GateDecision, BasedOnMessageCount, CreatedAt)
            VALUES (@id, N'd', 0.9, N'Pending', N'WouldSend', 1, SYSDATETIMEOFFSET())
            """,
            new { id });

        var attention = (await GetAsync(admin, "")).Data.Attention;

        Assert.True(attention.ShadowRepliesAwaitingReview >= 1);
        Assert.Contains(attention.ShadowReplyTickets, ticket => ticket.Id == id);
    }

    [Fact]
    public async Task Ticket_resolved_long_enough_ago_is_closed_and_a_recent_one_is_not()
    {
        var admin = await factory.SignInAsAdminAsync();
        var old = await factory.CreateTicketAsync();
        var recent = await factory.CreateTicketAsync();
        var open = await factory.CreateTicketAsync();
        await admin.PutAsJsonAsync($"/api/tickets/{old}/status", new { status = "Resolved" });
        await admin.PutAsJsonAsync($"/api/tickets/{recent}/status", new { status = "Resolved" });
        Execute("UPDATE dbo.Tickets SET ResolvedAt = DATEADD(DAY, -8, SYSDATETIMEOFFSET()) WHERE Id = @old", new { old });
        Execute("UPDATE dbo.Tickets SET ResolvedAt = DATEADD(DAY, -6, SYSDATETIMEOFFSET()) WHERE Id = @recent", new { recent });
        Execute("UPDATE dbo.Tickets SET CreatedAt = DATEADD(DAY, -30, SYSDATETIMEOFFSET()) WHERE Id = @open", new { open });

        var closed = await factory.Tickets.CloseResolvedBeforeAsync(DateTimeOffset.UtcNow.AddDays(-7), CancellationToken.None);

        Assert.True(closed >= 1);
        var ticket = (await factory.Tickets.GetAsync(old, CancellationToken.None))!;
        Assert.Equal(TicketStatus.Closed, ticket.Status);
        Assert.NotNull(ticket.ClosedAt);
        var change = ticket.Events.Last();
        Assert.Equal((TicketEventType.StatusChanged, "Resolved", "Closed"), (change.Type, change.FromValue, change.ToValue));
        Assert.Null(change.ActorUserId);
        Assert.Equal(TicketStatus.Resolved, (await factory.Tickets.GetAsync(recent, CancellationToken.None))!.Status);
        Assert.Equal(TicketStatus.Open, (await factory.Tickets.GetAsync(open, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task Ticket_reopened_and_resolved_again_counts_its_days_from_the_last_resolution()
    {
        var admin = await factory.SignInAsAdminAsync();
        var id = await factory.CreateTicketAsync();
        await admin.PutAsJsonAsync($"/api/tickets/{id}/status", new { status = "Resolved" });
        Execute("UPDATE dbo.Tickets SET ResolvedAt = DATEADD(DAY, -30, SYSDATETIMEOFFSET()) WHERE Id = @id", new { id });
        await admin.PutAsJsonAsync($"/api/tickets/{id}/status", new { status = "Open" });
        await admin.PutAsJsonAsync($"/api/tickets/{id}/status", new { status = "Resolved" });

        await factory.Tickets.CloseResolvedBeforeAsync(DateTimeOffset.UtcNow.AddDays(-7), CancellationToken.None);

        Assert.Equal(TicketStatus.Resolved, (await factory.Tickets.GetAsync(id, CancellationToken.None))!.Status);
    }

    private sealed record DashboardDto(DateOnly From, DateOnly To, DataDto Data);

    private sealed record DataDto(
        List<StatusDto> OpenByStatus,
        List<TeamDto> ActiveByTeam,
        AttentionDto Attention,
        TicketsDto Tickets,
        List<DayDto> DailyVolume,
        AiDto Ai,
        List<UsageDto> AiUsage);

    private sealed record StatusDto(string Status, int Count);

    private sealed record TeamDto(string Team, int Active, int Unassigned);

    private sealed record LinkDto(int Id, string Subject);

    private sealed record AttentionDto(
        int UnassignedTickets, int FailedSends, List<LinkDto> FailedSendTickets, int StuckInClassification,
        int ShadowRepliesAwaitingReview, List<LinkDto> ShadowReplyTickets);

    private sealed record TicketsDto(int Created, int Answered, double? MedianFirstResponseSeconds, int Resolved, double? MedianResolutionSeconds);

    private sealed record DayDto(DateTime Day, int Created);

    private sealed record AiDto(
        int ClassifiedByAi, int ClassificationsOverridden, int ClassificationsFailed, int DraftsSentUnchanged,
        int DraftsSentEdited, int DraftsDiscarded, int AnsweredAutomatically, int WouldHaveBeenAnsweredAutomatically, int Escalated);

    private sealed record UsageDto(string Purpose, int Calls, int Failures, long InputTokens, long OutputTokens);
}
