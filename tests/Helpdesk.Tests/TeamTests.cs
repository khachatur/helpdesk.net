using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Helpdesk.Core.Tickets;
using Helpdesk.Infrastructure.Tickets;

namespace Helpdesk.Tests;

/// <summary>
/// Teams, routing, and assignment. Each test builds its own team and routes one category to it;
/// routing is put back afterwards so other tests' tickets are not handed to these agents.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TeamTests(AuthApiFactory factory) : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public void Dispose() => factory.ResetRouting();

    private static async Task<List<TeamDto>> TeamsAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<TeamDto>>("/api/teams", Json))!;

    private static async Task<int> CreateTeamAsync(HttpClient admin, string? name = null)
    {
        var response = await admin.PostAsJsonAsync("/api/teams", new { name = name ?? "Team " + Guid.NewGuid().ToString("N")[..10] });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreatedDto>())!.Id;
    }

    /// <summary>A new team that handles the category, with the given number of active agents.</summary>
    private async Task<(int TeamId, List<Guid> Agents)> TeamForAsync(HttpClient admin, TicketCategory category, int agents)
    {
        var teamId = await CreateTeamAsync(admin);
        var name = (await TeamsAsync(admin)).Single(team => team.Id == teamId).Name;
        var route = await admin.PutAsJsonAsync($"/api/teams/{teamId}", new { name, categories = new[] { category.ToString() }, isDefault = false });
        route.EnsureSuccessStatusCode();

        var ids = new List<Guid>();
        for (var i = 0; i < agents; i++)
        {
            ids.Add((await factory.CreateAgentAsync(admin, teamId)).Id);
        }

        return (teamId, ids);
    }

    private async Task<TicketDetail> TicketAsync(int id) => (await factory.Tickets.GetAsync(id, CancellationToken.None))!;

    [Fact]
    public async Task New_ticket_goes_to_the_queue_for_its_category()
    {
        var general = await TicketAsync(await factory.CreateTicketAsync(category: TicketCategory.GeneralQuestion));
        var technical = await TicketAsync(await factory.CreateTicketAsync(category: TicketCategory.TechnicalQuestion));
        var refund = await TicketAsync(await factory.CreateTicketAsync(category: TicketCategory.RefundRequest));

        Assert.Equal("General Support", general.TeamName);
        Assert.Equal("Technical Support", technical.TeamName);
        Assert.Equal("Finance Support", refund.TeamName);
    }

    [Fact]
    public async Task Unclassified_ticket_goes_to_the_default_team()
    {
        var ticket = await TicketAsync(await factory.CreateTicketAsync());

        Assert.Equal("General Support", ticket.TeamName);
    }

    [Fact]
    public async Task Tickets_are_shared_out_in_turn()
    {
        var admin = await factory.SignInAsAdminAsync();
        var (teamId, agents) = await TeamForAsync(admin, TicketCategory.RefundRequest, agents: 3);

        var owners = new List<Guid?>();
        for (var i = 0; i < 6; i++)
        {
            var ticket = await TicketAsync(await factory.CreateTicketAsync(category: TicketCategory.RefundRequest));
            Assert.Equal(teamId, ticket.TeamId);
            owners.Add(ticket.AssigneeId);
        }

        // Everyone gets one ticket before anyone gets a second, and the second round repeats the first.
        Assert.Equal(agents.Order(), owners.Take(3).Select(owner => owner!.Value).Order());
        Assert.Equal(owners.Take(3), owners.Skip(3));
    }

    [Fact]
    public async Task Tickets_created_at_the_same_moment_are_still_shared_out_evenly()
    {
        var admin = await factory.SignInAsAdminAsync();
        var (_, agents) = await TeamForAsync(admin, TicketCategory.RefundRequest, agents: 4);

        var ids = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => factory.CreateTicketAsync(category: TicketCategory.RefundRequest))));

        var owners = new List<Guid?>();
        foreach (var id in ids)
        {
            owners.Add((await TicketAsync(id)).AssigneeId);
        }

        Assert.All(agents, agent => Assert.Equal(5, owners.Count(owner => owner == agent)));
    }

    [Fact]
    public async Task Assignment_is_recorded_in_the_history()
    {
        var admin = await factory.SignInAsAdminAsync();
        await TeamForAsync(admin, TicketCategory.RefundRequest, agents: 1);

        var ticket = await TicketAsync(await factory.CreateTicketAsync(category: TicketCategory.RefundRequest));

        var assigned = Assert.Single(ticket.Events, e => e.Type == TicketEventType.Assigned);
        Assert.Equal(((string?)null, "Test Agent"), (assigned.FromValue, assigned.ToValue));
        Assert.Null(assigned.ActorUserId);
    }

    [Fact]
    public async Task Deactivated_agents_get_no_tickets()
    {
        var admin = await factory.SignInAsAdminAsync();
        var (_, agents) = await TeamForAsync(admin, TicketCategory.RefundRequest, agents: 2);
        await admin.PostAsync($"/api/users/{agents[0]}/deactivate", null);

        for (var i = 0; i < 3; i++)
        {
            var ticket = await TicketAsync(await factory.CreateTicketAsync(category: TicketCategory.RefundRequest));
            Assert.Equal(agents[1], ticket.AssigneeId);
        }
    }

    [Fact]
    public async Task Ticket_waits_unassigned_until_its_team_has_an_active_member()
    {
        var admin = await factory.SignInAsAdminAsync();
        var (teamId, _) = await TeamForAsync(admin, TicketCategory.RefundRequest, agents: 0);
        var id = await factory.CreateTicketAsync(category: TicketCategory.RefundRequest);
        Assert.Null((await TicketAsync(id)).AssigneeId);

        var agent = await factory.CreateAgentAsync(admin, teamId);

        Assert.Equal(agent.Id, (await TicketAsync(id)).AssigneeId);
    }

    [Fact]
    public async Task Waiting_tickets_are_assigned_when_an_agent_is_moved_into_the_team()
    {
        var admin = await factory.SignInAsAdminAsync();
        var (teamId, _) = await TeamForAsync(admin, TicketCategory.RefundRequest, agents: 0);
        var id = await factory.CreateTicketAsync(category: TicketCategory.RefundRequest);
        var agent = await factory.CreateAgentAsync(admin);

        var response = await admin.PutAsJsonAsync($"/api/users/{agent.Id}/team", new { teamId });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(agent.Id, (await TicketAsync(id)).AssigneeId);
    }

    [Fact]
    public async Task Changing_the_category_moves_the_ticket_to_the_new_team_and_owner()
    {
        var admin = await factory.SignInAsAdminAsync();
        var (refundTeam, refundAgents) = await TeamForAsync(admin, TicketCategory.RefundRequest, agents: 1);
        var (technicalTeam, technicalAgents) = await TeamForAsync(admin, TicketCategory.TechnicalQuestion, agents: 1);
        var id = await factory.CreateTicketAsync(category: TicketCategory.TechnicalQuestion);
        Assert.Equal((technicalTeam, technicalAgents[0]), ((await TicketAsync(id)).TeamId!.Value, (await TicketAsync(id)).AssigneeId!.Value));

        var response = await admin.PutAsJsonAsync($"/api/tickets/{id}/category", new { category = "RefundRequest" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var ticket = await TicketAsync(id);
        Assert.Equal(refundTeam, ticket.TeamId);
        Assert.Equal(refundAgents[0], ticket.AssigneeId);
        Assert.Equal(
            [TicketEventType.Created, TicketEventType.Assigned, TicketEventType.CategoryChanged, TicketEventType.TeamChanged, TicketEventType.Assigned],
            ticket.Events.Select(e => e.Type));
    }

    [Fact]
    public async Task Deactivating_an_agent_hands_their_open_tickets_to_a_teammate()
    {
        var admin = await factory.SignInAsAdminAsync();
        var (_, agents) = await TeamForAsync(admin, TicketCategory.RefundRequest, agents: 2);
        var open = await factory.CreateTicketAsync(category: TicketCategory.RefundRequest);
        await factory.CreateTicketAsync(category: TicketCategory.RefundRequest);
        var resolved = await factory.CreateTicketAsync(category: TicketCategory.RefundRequest);
        var leaving = (await TicketAsync(open)).AssigneeId!.Value;
        Assert.Equal(leaving, (await TicketAsync(resolved)).AssigneeId);
        await admin.PutAsJsonAsync($"/api/tickets/{resolved}/status", new { status = "Resolved" });

        await admin.PostAsync($"/api/users/{leaving}/deactivate", null);

        var teammate = agents.Single(agent => agent != leaving);
        Assert.Equal(teammate, (await TicketAsync(open)).AssigneeId);
        // A resolved ticket keeps its owner on record; it is reassigned only if it reopens.
        Assert.Equal(leaving, (await TicketAsync(resolved)).AssigneeId);
    }

    [Fact]
    public async Task Tickets_of_a_deactivated_sole_member_become_unassigned()
    {
        var admin = await factory.SignInAsAdminAsync();
        var (_, agents) = await TeamForAsync(admin, TicketCategory.RefundRequest, agents: 1);
        var id = await factory.CreateTicketAsync(category: TicketCategory.RefundRequest);

        await admin.PostAsync($"/api/users/{agents[0]}/deactivate", null);

        Assert.Null((await TicketAsync(id)).AssigneeId);
    }

    [Fact]
    public async Task Moving_an_agent_to_another_team_hands_on_the_tickets_they_leave_behind()
    {
        var admin = await factory.SignInAsAdminAsync();
        var (_, agents) = await TeamForAsync(admin, TicketCategory.RefundRequest, agents: 2);
        var otherTeam = await CreateTeamAsync(admin);
        var id = await factory.CreateTicketAsync(category: TicketCategory.RefundRequest);
        var mover = (await TicketAsync(id)).AssigneeId!.Value;

        await admin.PutAsJsonAsync($"/api/users/{mover}/team", new { teamId = otherTeam });

        Assert.Equal(agents.Single(agent => agent != mover), (await TicketAsync(id)).AssigneeId);
    }

    [Fact]
    public async Task Reopened_ticket_returns_to_its_previous_owner()
    {
        var admin = await factory.SignInAsAdminAsync();
        await TeamForAsync(admin, TicketCategory.RefundRequest, agents: 3);
        var id = await factory.CreateTicketAsync(category: TicketCategory.RefundRequest);
        var owner = (await TicketAsync(id)).AssigneeId;
        await admin.PutAsJsonAsync($"/api/tickets/{id}/status", new { status = "Resolved" });

        var result = await factory.Tickets.AppendInboundAsync(id, Reply("It broke again."), CancellationToken.None);

        Assert.Equal(AppendResult.Reopened, result);
        Assert.Equal(owner, (await TicketAsync(id)).AssigneeId);
    }

    [Fact]
    public async Task Reopened_ticket_goes_to_a_teammate_when_its_owner_was_deactivated()
    {
        var admin = await factory.SignInAsAdminAsync();
        var (_, agents) = await TeamForAsync(admin, TicketCategory.RefundRequest, agents: 2);
        var id = await factory.CreateTicketAsync(category: TicketCategory.RefundRequest);
        var owner = (await TicketAsync(id)).AssigneeId!.Value;
        await admin.PutAsJsonAsync($"/api/tickets/{id}/status", new { status = "Resolved" });
        await admin.PostAsync($"/api/users/{owner}/deactivate", null);

        await factory.Tickets.AppendInboundAsync(id, Reply("It broke again."), CancellationToken.None);

        Assert.Equal(agents.Single(agent => agent != owner), (await TicketAsync(id)).AssigneeId);
    }

    [Fact]
    public async Task Follow_up_to_a_closed_ticket_goes_to_the_same_team_and_owner()
    {
        var admin = await factory.SignInAsAdminAsync();
        var (teamId, _) = await TeamForAsync(admin, TicketCategory.RefundRequest, agents: 3);
        var originalId = await factory.CreateTicketAsync(category: TicketCategory.RefundRequest);
        var owner = (await TicketAsync(originalId)).AssigneeId;
        await admin.PutAsJsonAsync($"/api/tickets/{originalId}/status", new { status = "Closed" });

        var followUpId = await factory.Tickets.CreateAsync(
            new NewTicket("Re: Refund", "student@example.com", "Test Student", "One more question.", OriginalTicketId: originalId),
            CancellationToken.None);

        var followUp = await TicketAsync(followUpId);
        Assert.Equal(TicketCategory.RefundRequest, followUp.Category);
        Assert.Equal(teamId, followUp.TeamId);
        Assert.Equal(owner, followUp.AssigneeId);
    }

    [Fact]
    public async Task List_filters_by_team_and_by_owner()
    {
        var admin = await factory.SignInAsAdminAsync();
        var (teamId, _) = await TeamForAsync(admin, TicketCategory.RefundRequest, agents: 0);
        var agent = await factory.CreateAgentAsync(admin);
        var waiting = await factory.CreateTicketAsync(category: TicketCategory.RefundRequest);
        await admin.PutAsJsonAsync($"/api/users/{agent.Id}/team", new { teamId });
        var mine = waiting;
        var agentClient = await factory.SignInAsync(agent.Email, agent.Password);
        await admin.PostAsync($"/api/users/{agent.Id}/deactivate", null);
        var unowned = await factory.CreateTicketAsync(category: TicketCategory.RefundRequest);
        await admin.PostAsync($"/api/users/{agent.Id}/activate", null);
        agentClient = await factory.SignInAsync(agent.Email, agent.Password);

        var inTeam = await agentClient.GetFromJsonAsync<ListDto>($"/api/tickets?team={teamId}", Json);
        var myTickets = await agentClient.GetFromJsonAsync<ListDto>("/api/tickets?assignee=me", Json);
        var unassigned = await agentClient.GetFromJsonAsync<ListDto>($"/api/tickets?team={teamId}&assignee=unassigned", Json);

        Assert.Equal(new[] { mine, unowned }.Order(), inTeam!.Items.Select(t => t.Id).Order());
        // Reactivating the agent gave them the ticket that had been waiting.
        Assert.Equal(new[] { mine, unowned }.Order(), myTickets!.Items.Select(t => t.Id).Order());
        Assert.Empty(unassigned!.Items);
    }

    [Fact]
    public async Task Agents_can_list_teams_but_not_change_them()
    {
        var admin = await factory.SignInAsAdminAsync();
        var agent = await factory.CreateAgentAsync(admin);
        var client = await factory.SignInAsync(agent.Email, agent.Password);
        var teamId = (await TeamsAsync(client)).First().Id;

        var create = await client.PostAsJsonAsync("/api/teams", new { name = "Sneaky" });
        var update = await client.PutAsJsonAsync($"/api/teams/{teamId}", new { name = "Renamed", categories = Array.Empty<string>(), isDefault = false });
        var delete = await client.DeleteAsync($"/api/teams/{teamId}");
        var setTeam = await client.PutAsJsonAsync($"/api/users/{agent.Id}/team", new { teamId });

        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, setTeam.StatusCode);
    }

    [Fact]
    public async Task Seeded_teams_route_the_three_categories()
    {
        var admin = await factory.SignInAsAdminAsync();

        var teams = await TeamsAsync(admin);

        var general = teams.Single(team => team.Name == "General Support");
        Assert.True(general.IsDefault);
        Assert.Equal([TicketCategory.GeneralQuestion], general.Categories);
        Assert.Equal([TicketCategory.TechnicalQuestion], teams.Single(team => team.Name == "Technical Support").Categories);
        Assert.Equal([TicketCategory.RefundRequest], teams.Single(team => team.Name == "Finance Support").Categories);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("General Support")]
    public async Task Team_name_must_be_present_and_unique(string name)
    {
        var admin = await factory.SignInAsAdminAsync();

        var response = await admin.PostAsJsonAsync("/api/teams", new { name });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("name", (await response.Content.ReadFromJsonAsync<ValidationProblemDto>())!.Errors.Keys);
    }

    [Fact]
    public async Task Team_can_be_renamed_and_counts_its_members()
    {
        var admin = await factory.SignInAsAdminAsync();
        var teamId = await CreateTeamAsync(admin);
        var agent = await factory.CreateAgentAsync(admin, teamId);
        await factory.CreateAgentAsync(admin, teamId);
        await admin.PostAsync($"/api/users/{agent.Id}/deactivate", null);
        var newName = "Renamed " + Guid.NewGuid().ToString("N")[..8];

        var response = await admin.PutAsJsonAsync($"/api/teams/{teamId}", new { name = newName, categories = Array.Empty<string>(), isDefault = false });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var team = (await TeamsAsync(admin)).Single(t => t.Id == teamId);
        Assert.Equal((newName, 2, 1), (team.Name, team.MemberCount, team.ActiveMemberCount));
        var users = await admin.GetFromJsonAsync<List<UserDto>>("/api/users");
        Assert.Equal(newName, users!.Single(user => user.Id == agent.Id).TeamName);
    }

    [Fact]
    public async Task Giving_a_category_to_a_team_takes_it_from_the_team_that_had_it()
    {
        var admin = await factory.SignInAsAdminAsync();
        var (teamId, _) = await TeamForAsync(admin, TicketCategory.RefundRequest, agents: 0);

        var teams = await TeamsAsync(admin);

        Assert.Equal([TicketCategory.RefundRequest], teams.Single(team => team.Id == teamId).Categories);
        Assert.Empty(teams.Single(team => team.Name == "Finance Support").Categories);
    }

    [Fact]
    public async Task There_is_always_exactly_one_default_team()
    {
        var admin = await factory.SignInAsAdminAsync();
        var teamId = await CreateTeamAsync(admin);
        var team = (await TeamsAsync(admin)).Single(t => t.Id == teamId);
        var general = (await TeamsAsync(admin)).Single(t => t.Name == "General Support");

        var unsetOnly = await admin.PutAsJsonAsync(
            $"/api/teams/{general.Id}", new { name = general.Name, categories = new[] { "GeneralQuestion" }, isDefault = false });
        var makeDefault = await admin.PutAsJsonAsync(
            $"/api/teams/{teamId}", new { name = team.Name, categories = Array.Empty<string>(), isDefault = true });

        Assert.Equal(HttpStatusCode.Conflict, unsetOnly.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, makeDefault.StatusCode);
        Assert.Equal(teamId, Assert.Single(await TeamsAsync(admin), t => t.IsDefault).Id);
        Assert.Equal(teamId, (await TicketAsync(await factory.CreateTicketAsync())).TeamId);
    }

    [Fact]
    public async Task Unused_team_can_be_deleted_but_a_team_in_use_cannot()
    {
        var admin = await factory.SignInAsAdminAsync();
        var unused = await CreateTeamAsync(admin);
        var withMember = await CreateTeamAsync(admin);
        await factory.CreateAgentAsync(admin, withMember);
        var general = (await TeamsAsync(admin)).Single(t => t.Name == "General Support");

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/teams/{unused}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/teams/{withMember}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/teams/{general.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/teams/{unused}")).StatusCode);
    }

    [Fact]
    public async Task Agent_cannot_be_put_in_a_team_that_does_not_exist()
    {
        var admin = await factory.SignInAsAdminAsync();
        var agent = await factory.CreateAgentAsync(admin);

        var move = await admin.PutAsJsonAsync($"/api/users/{agent.Id}/team", new { teamId = 999_999 });
        var create = await admin.PostAsJsonAsync("/api/users", new
        {
            email = $"agent-{Guid.NewGuid():N}@helpdesk.test",
            displayName = "Nobody",
            password = "Valid-Pass-123",
            teamId = 999_999,
        });

        Assert.Equal(HttpStatusCode.BadRequest, move.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
    }

    private static InboundReply Reply(string text) =>
        new("student@example.com", "Test Student", text, null, DateTimeOffset.UtcNow, null, null, null);

    private sealed record TeamDto(int Id, string Name, bool IsDefault, List<TicketCategory> Categories, int MemberCount, int ActiveMemberCount);

    private sealed record CreatedDto(int Id);

    private sealed record ListDto(List<ListItemDto> Items, int TotalCount);

    private sealed record ListItemDto(int Id);
}
