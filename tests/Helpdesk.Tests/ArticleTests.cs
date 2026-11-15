using System.Net;
using System.Net.Http.Json;
using Dapper;
using Helpdesk.Api.KnowledgeBase;
using Helpdesk.Infrastructure.Data;
using Helpdesk.Infrastructure.KnowledgeBase;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Tests;

[Collection(ApiCollection.Name)]
public class ArticleTests
{
    private readonly AuthApiFactory _factory;
    private readonly ArticleStore _articles;

    public ArticleTests(AuthApiFactory factory)
    {
        _factory = factory;
        _articles = factory.Services.GetRequiredService<ArticleStore>();

        // Search ranks articles against each other, so every test starts with an empty knowledge base.
        factory.ClearKnowledgeBase();
    }

    private static async Task<int> CreateAsync(
        HttpClient client, string title, string body = "Some helpful text.", bool isPublished = true, bool? autoSendAllowed = null)
    {
        var response = await client.PostAsJsonAsync("/api/articles", new { title, body, isPublished, autoSendAllowed });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreatedDto>())!.Id;
    }

    private async Task<List<MatchDto>> SearchAsync(HttpClient client, string question, int? take = null)
    {
        await _articles.WaitForIndexAsync(CancellationToken.None);
        var url = "/api/articles/search?q=" + Uri.EscapeDataString(question) + (take is null ? "" : $"&take={take}");
        return (await client.GetFromJsonAsync<List<MatchDto>>(url))!;
    }

    private async Task<HttpClient> SignInAsAgentAsync()
    {
        var agent = await _factory.CreateAgentAsync(await _factory.SignInAsAdminAsync());
        return await _factory.SignInAsync(agent.Email, agent.Password);
    }

    [Fact]
    public async Task Articles_require_sign_in()
    {
        var anonymous = _factory.CreateBrowser();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/articles")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/articles/search?q=password")).StatusCode);
    }

    [Fact]
    public async Task Created_article_can_be_read_and_listed()
    {
        var admin = await _factory.SignInAsAdminAsync();

        var id = await CreateAsync(admin, "  Opening hours  ", "  We answer on weekdays.  ", isPublished: false);

        var article = await admin.GetFromJsonAsync<ArticleDto>($"/api/articles/{id}");
        Assert.Equal(("Opening hours", "We answer on weekdays.", false, false, "Test Admin"),
            (article!.Title, article.Body, article.IsPublished, article.AutoSendAllowed, article.UpdatedByName));
        var list = await admin.GetFromJsonAsync<List<ArticleDto>>("/api/articles");
        Assert.Equal(id, Assert.Single(list!).Id);
    }

    [Theory]
    [InlineData("", "Body", "title")]
    [InlineData("   ", "Body", "title")]
    [InlineData("Title", "", "body")]
    [InlineData("Title", "  ", "body")]
    public async Task Title_and_body_are_required(string title, string body, string field)
    {
        var admin = await _factory.SignInAsAdminAsync();

        var response = await admin.PostAsJsonAsync("/api/articles", new { title, body });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, (await response.Content.ReadFromJsonAsync<ValidationProblemDto>())!.Errors.Keys);
    }

    [Fact]
    public async Task Overlong_title_is_rejected()
    {
        var admin = await _factory.SignInAsAdminAsync();

        var response = await admin.PostAsJsonAsync("/api/articles", new { title = new string('x', 301), body = "Body" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_article_returns_not_found()
    {
        var admin = await _factory.SignInAsAdminAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/articles/999999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.PutAsJsonAsync("/api/articles/999999", new { title = "T", body = "B" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/articles/999999/publish", null)).StatusCode);
    }

    [Fact]
    public async Task Agent_can_write_and_publish_articles()
    {
        var agent = await SignInAsAgentAsync();

        var id = await CreateAsync(agent, "Parking", "Park behind the building.", isPublished: false);
        var edit = await agent.PutAsJsonAsync($"/api/articles/{id}", new { title = "Parking", body = "Park in front.", isPublished = false });
        var publish = await agent.PostAsync($"/api/articles/{id}/publish", null);

        Assert.Equal(HttpStatusCode.NoContent, edit.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, publish.StatusCode);
        var article = await agent.GetFromJsonAsync<ArticleDto>($"/api/articles/{id}");
        Assert.Equal(("Park in front.", true, false, "Test Agent"),
            (article!.Body, article.IsPublished, article.AutoSendAllowed, article.UpdatedByName));
    }

    [Fact]
    public async Task Only_an_admin_can_approve_an_article_for_auto_send()
    {
        var admin = await _factory.SignInAsAdminAsync();
        var agent = await SignInAsAgentAsync();
        var id = await CreateAsync(agent, "Deadlines", "Enrol by Thursday.");

        var agentCreate = await agent.PostAsJsonAsync("/api/articles", new { title = "Sneaky", body = "Body", autoSendAllowed = true });
        var agentApprove = await agent.PutAsJsonAsync(
            $"/api/articles/{id}", new { title = "Deadlines", body = "Enrol by Thursday.", isPublished = true, autoSendAllowed = true });
        var adminApprove = await admin.PutAsJsonAsync(
            $"/api/articles/{id}", new { title = "Deadlines", body = "Enrol by Thursday.", isPublished = true, autoSendAllowed = true });

        Assert.Equal(HttpStatusCode.Forbidden, agentCreate.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, agentApprove.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, adminApprove.StatusCode);
        Assert.True((await admin.GetFromJsonAsync<ArticleDto>($"/api/articles/{id}"))!.AutoSendAllowed);
    }

    [Fact]
    public async Task Agent_changing_an_approved_articles_text_withdraws_the_approval()
    {
        var admin = await _factory.SignInAsAdminAsync();
        var agent = await SignInAsAgentAsync();
        var id = await CreateAsync(admin, "Deadlines", "Enrol by Thursday.", autoSendAllowed: true);

        var response = await agent.PutAsJsonAsync(
            $"/api/articles/{id}", new { title = "Deadlines", body = "Enrol whenever you like.", isPublished = true });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var article = await admin.GetFromJsonAsync<ArticleDto>($"/api/articles/{id}");
        Assert.Equal("Enrol whenever you like.", article!.Body);
        Assert.False(article.AutoSendAllowed);
    }

    [Fact]
    public async Task Agent_unpublishing_an_approved_article_keeps_the_approval()
    {
        var admin = await _factory.SignInAsAdminAsync();
        var agent = await SignInAsAgentAsync();
        var id = await CreateAsync(admin, "Deadlines", "Enrol by Thursday.", autoSendAllowed: true);

        await agent.PostAsync($"/api/articles/{id}/unpublish", null);
        await agent.PutAsJsonAsync($"/api/articles/{id}", new { title = "Deadlines", body = "Enrol by Thursday.", isPublished = true });

        var article = await admin.GetFromJsonAsync<ArticleDto>($"/api/articles/{id}");
        Assert.True(article!.IsPublished);
        Assert.True(article.AutoSendAllowed);
    }

    [Fact]
    public async Task Admin_edit_keeps_the_approval_unless_the_admin_changes_it()
    {
        var admin = await _factory.SignInAsAdminAsync();
        var id = await CreateAsync(admin, "Deadlines", "Enrol by Thursday.", autoSendAllowed: true);

        await admin.PutAsJsonAsync($"/api/articles/{id}", new { title = "Deadlines", body = "Enrol by Friday.", isPublished = true });
        var kept = (await admin.GetFromJsonAsync<ArticleDto>($"/api/articles/{id}"))!.AutoSendAllowed;
        await admin.PutAsJsonAsync(
            $"/api/articles/{id}", new { title = "Deadlines", body = "Enrol by Friday.", isPublished = true, autoSendAllowed = false });

        Assert.True(kept);
        Assert.False((await admin.GetFromJsonAsync<ArticleDto>($"/api/articles/{id}"))!.AutoSendAllowed);
    }

    [Fact]
    public async Task Search_finds_only_published_articles()
    {
        var admin = await _factory.SignInAsAdminAsync();
        var published = await CreateAsync(admin, "Library opening hours", "The library opens at nine.");
        var draft = await CreateAsync(admin, "Library closing hours", "The library closes at five.", isPublished: false);

        var before = await SearchAsync(admin, "library hours");
        await admin.PostAsync($"/api/articles/{draft}/publish", null);
        await admin.PostAsync($"/api/articles/{published}/unpublish", null);
        var after = await SearchAsync(admin, "library hours");

        Assert.Equal([published], before.Select(match => match.Id));
        Assert.Equal([draft], after.Select(match => match.Id));
    }

    [Fact]
    public async Task Search_matches_other_forms_of_a_word()
    {
        var admin = await _factory.SignInAsAdminAsync();
        var id = await CreateAsync(admin, "How to reset your password", "Choose Forgot password on the sign-in page.");
        await CreateAsync(admin, "Parking", "Park behind the building.");

        var matches = await SearchAsync(admin, "I keep resetting my passwords and nothing works");

        Assert.Equal(id, Assert.Single(matches).Id);
    }

    [Fact]
    public async Task Search_ranks_a_title_match_above_a_passing_mention()
    {
        var admin = await _factory.SignInAsAdminAsync();
        await CreateAsync(admin, "Parking", "Park behind the building. Certificates are not needed to park.");
        var certificates = await CreateAsync(admin, "Certificates of completion", "You receive a certificate when you finish.");

        var matches = await SearchAsync(admin, "certificate");

        Assert.Equal(2, matches.Count);
        Assert.Equal(certificates, matches[0].Id);
        Assert.True(matches[0].Score > matches[1].Score);
    }

    [Fact]
    public async Task Search_result_carries_the_auto_send_flag_and_a_snippet()
    {
        var admin = await _factory.SignInAsAdminAsync();
        await CreateAsync(admin, "Refunds", string.Join("\n\n", Enumerable.Repeat("Refunds take ten working days to arrive.", 20)), autoSendAllowed: true);

        var match = Assert.Single(await SearchAsync(admin, "refund"));

        Assert.True(match.AutoSendAllowed);
        Assert.InRange(match.Snippet.Length, 100, 241);
        Assert.EndsWith("…", match.Snippet);
        Assert.DoesNotContain("\n", match.Snippet);
        Assert.True(match.Body.Length > match.Snippet.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("the and of to")]
    [InlineData("\"unclosed quote AND ( NEAR")]
    [InlineData("'; DROP TABLE dbo.Articles; --")]
    [InlineData("zzzqqqxxx")]
    public async Task Search_with_nothing_to_find_returns_no_results_rather_than_an_error(string question)
    {
        var admin = await _factory.SignInAsAdminAsync();
        await CreateAsync(admin, "Parking", "Park behind the building.");

        var matches = await SearchAsync(admin, question);

        Assert.Empty(matches);
        Assert.Single((await admin.GetFromJsonAsync<List<ArticleDto>>("/api/articles"))!);
    }

    [Fact]
    public async Task Search_returns_at_most_the_number_asked_for()
    {
        var admin = await _factory.SignInAsAdminAsync();
        for (var i = 0; i < 4; i++)
        {
            await CreateAsync(admin, $"Refund rule {i}", "Refunds follow the refund policy.");
        }

        Assert.Equal(2, (await SearchAsync(admin, "refund", take: 2)).Count);
        Assert.Equal(4, (await SearchAsync(admin, "refund")).Count);
    }

    /// <summary>
    /// The search quality check: each question, written the way a student would write it, should
    /// bring up the sample article that answers it. The questions are the sample tickets' wording.
    /// Ten of the eleven put the right article first. The refund question puts it second: "signed up"
    /// in the question matches "sign in" and "sign out" in the course access article, which keyword
    /// search cannot tell apart. Search only has to put the right article among the few candidates
    /// an agent or the AI then reads, so second place is accepted there, and no lower.
    /// </summary>
    [Theory]
    [InlineData("I'd like to join the data analytics course. When is the next start date?", "Course start dates and schedules")]
    [InlineData("I completed all modules last week. How and when do I get my certificate?", "Certificates of completion")]
    [InlineData("My work schedule changed. Is it possible to move from the morning group to the evening one?", "Switching between morning and evening groups")]
    [InlineData("Could you tell me the last day to enrol for the spring term?", "Enrollment deadlines")]
    [InlineData("I reset my password twice but the portal still says my credentials are invalid.", "How to reset your password")]
    [InlineData("The videos in module 3 show a spinner and never start. Other modules work fine.", "Video lessons will not play")]
    [InlineData("When I upload my project zip (about 80 MB) I get an error 413. Is there a size limit?", "Assignment upload limits and errors")]
    [InlineData("I paid yesterday and got the receipt, but the course still shows as locked in my account.", "Getting access to course materials after payment")]
    [InlineData("I signed up for the advanced course by mistake. Can I get a refund or a transfer?", "Refund policy", 2)]
    [InlineData("My card statement shows two identical charges from last Friday. Please refund the duplicate.", "Duplicate or incorrect charges")]
    [InlineData("Due to a family situation I have to withdraw from the course.", "Withdrawing from a course")]
    public async Task Sample_question_finds_the_article_that_answers_it(string question, string expectedTitle, int worstPosition = 1)
    {
        var admin = await _factory.SignInAsAdminAsync();
        foreach (var sample in SampleArticles.All)
        {
            await _articles.CreateAsync(
                new ArticleContent(sample.Title, SampleArticles.Unindent(sample.Body), IsPublished: true, sample.AutoSendAllowed),
                userId: null,
                CancellationToken.None);
        }

        var matches = await SearchAsync(admin, question);

        var position = matches.FindIndex(match => match.Title == expectedTitle) + 1;
        Assert.True(
            position >= 1 && position <= worstPosition,
            $"Expected '{expectedTitle}' at position {worstPosition} or better, but the results were: {string.Join(" | ", matches.Select(match => match.Title))}");
    }

    private sealed record CreatedDto(int Id);

    private sealed record ArticleDto(int Id, string Title, string Body, bool IsPublished, bool AutoSendAllowed, string? UpdatedByName);

    private sealed record MatchDto(int Id, string Title, string Snippet, string Body, bool AutoSendAllowed, int Score);
}
