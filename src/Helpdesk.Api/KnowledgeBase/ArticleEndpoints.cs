using System.Security.Claims;
using Helpdesk.Core.Users;
using Helpdesk.Infrastructure.KnowledgeBase;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Helpdesk.Api.KnowledgeBase;

/// <summary>
/// The knowledge base. Any signed-in user can read, search, write, and publish articles.
/// Only an admin can approve an article for auto-send, and an agent's edit to an approved
/// article withdraws the approval, so nothing an admin has not seen is ever sent unreviewed.
/// </summary>
public static class ArticleEndpoints
{
    public const int MaxTitleLength = 300;
    public const int MaxBodyLength = 50_000;
    private const int SnippetLength = 240;

    public sealed record ArticleResponse(
        int Id,
        string Title,
        string Body,
        bool IsPublished,
        bool AutoSendAllowed,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        string? UpdatedByName)
    {
        public static ArticleResponse From(Article article) => new(
            article.Id, article.Title, article.Body, article.IsPublished, article.AutoSendAllowed,
            article.CreatedAt, article.UpdatedAt, article.UpdatedByName);
    }

    public sealed record ArticleMatchResponse(int Id, string Title, string Snippet, string Body, bool AutoSendAllowed, int Score)
    {
        public static ArticleMatchResponse From(ArticleMatch match) => new(
            match.Id, match.Title, MakeSnippet(match.Body), match.Body, match.AutoSendAllowed, match.Score);
    }

    /// <param name="AutoSendAllowed">Admins only. Agents leave it out.</param>
    public sealed record SaveArticleRequest(string? Title, string? Body, bool IsPublished = false, bool? AutoSendAllowed = null);

    public sealed record ArticleCreatedResponse(int Id);

    public static IEndpointRouteBuilder MapArticleEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/articles").WithTags("Knowledge base");

        group.MapGet("/", ListAsync);
        group.MapGet("/search", SearchAsync);
        group.MapGet("/{id:int}", GetAsync);
        group.MapPost("/", CreateAsync);
        group.MapPut("/{id:int}", UpdateAsync);
        group.MapPost("/{id:int}/publish", (int id, ClaimsPrincipal caller, ArticleStore store, CancellationToken cancellationToken) =>
            SetPublishedAsync(id, isPublished: true, caller, store, cancellationToken));
        group.MapPost("/{id:int}/unpublish", (int id, ClaimsPrincipal caller, ArticleStore store, CancellationToken cancellationToken) =>
            SetPublishedAsync(id, isPublished: false, caller, store, cancellationToken));

        return routes;
    }

    private static async Task<Ok<IEnumerable<ArticleResponse>>> ListAsync(ArticleStore store, CancellationToken cancellationToken) =>
        TypedResults.Ok((await store.ListAsync(cancellationToken)).Select(ArticleResponse.From));

    /// <summary>Published articles that best answer a question in ordinary language, best first.</summary>
    private static async Task<Ok<IEnumerable<ArticleMatchResponse>>> SearchAsync(
        string? q, int? take, ArticleStore store, CancellationToken cancellationToken)
    {
        var matches = await store.SearchAsync(q ?? "", take ?? 5, cancellationToken);
        return TypedResults.Ok(matches.Select(ArticleMatchResponse.From));
    }

    private static async Task<Results<Ok<ArticleResponse>, NotFound>> GetAsync(
        int id, ArticleStore store, CancellationToken cancellationToken)
    {
        var article = await store.GetAsync(id, cancellationToken);
        return article is null ? TypedResults.NotFound() : TypedResults.Ok(ArticleResponse.From(article));
    }

    private static async Task<Results<Created<ArticleCreatedResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        SaveArticleRequest request, ClaimsPrincipal caller, ArticleStore store, CancellationToken cancellationToken)
    {
        if (Validate(request, out var title, out var body) is { } invalid)
        {
            return invalid;
        }

        var isAdmin = caller.IsInRole(Roles.Admin);
        if (!isAdmin && request.AutoSendAllowed == true)
        {
            return OnlyAdminsApprove();
        }

        var content = new ArticleContent(title, body, request.IsPublished, isAdmin && request.AutoSendAllowed == true);
        var id = await store.CreateAsync(content, UserId(caller), cancellationToken);
        return TypedResults.Created($"/api/articles/{id}", new ArticleCreatedResponse(id));
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        int id, SaveArticleRequest request, ClaimsPrincipal caller, ArticleStore store, CancellationToken cancellationToken)
    {
        if (Validate(request, out var title, out var body) is { } invalid)
        {
            return invalid;
        }

        var existing = await store.GetAsync(id, cancellationToken);
        if (existing is null)
        {
            return TypedResults.NotFound();
        }

        bool autoSendAllowed;
        if (caller.IsInRole(Roles.Admin))
        {
            autoSendAllowed = request.AutoSendAllowed ?? existing.AutoSendAllowed;
        }
        else if (request.AutoSendAllowed == true && !existing.AutoSendAllowed)
        {
            return OnlyAdminsApprove();
        }
        else
        {
            // The admin approved the text as it was. Once an agent changes it, it needs approving again.
            var textChanged = title != existing.Title || body != existing.Body;
            autoSendAllowed = existing.AutoSendAllowed && !textChanged && request.AutoSendAllowed != false;
        }

        var content = new ArticleContent(title, body, request.IsPublished, autoSendAllowed);
        return await store.UpdateAsync(id, content, UserId(caller), cancellationToken)
            ? TypedResults.NoContent()
            : TypedResults.NotFound();
    }

    private static async Task<Results<NoContent, NotFound>> SetPublishedAsync(
        int id, bool isPublished, ClaimsPrincipal caller, ArticleStore store, CancellationToken cancellationToken)
    {
        var existing = await store.GetAsync(id, cancellationToken);
        if (existing is null)
        {
            return TypedResults.NotFound();
        }

        if (existing.IsPublished == isPublished)
        {
            return TypedResults.NoContent();
        }

        var content = new ArticleContent(existing.Title, existing.Body, isPublished, existing.AutoSendAllowed);
        return await store.UpdateAsync(id, content, UserId(caller), cancellationToken)
            ? TypedResults.NoContent()
            : TypedResults.NotFound();
    }

    private static ValidationProblem? Validate(SaveArticleRequest request, out string title, out string body)
    {
        title = request.Title?.Trim() ?? "";
        body = request.Body?.Trim() ?? "";
        var errors = new Dictionary<string, string[]>();

        if (title.Length == 0)
        {
            errors["title"] = ["Enter a title."];
        }
        else if (title.Length > MaxTitleLength)
        {
            errors["title"] = [$"The title must be {MaxTitleLength} characters or fewer."];
        }

        if (body.Length == 0)
        {
            errors["body"] = ["Write the article."];
        }
        else if (body.Length > MaxBodyLength)
        {
            errors["body"] = [$"The article must be {MaxBodyLength:N0} characters or fewer."];
        }

        return errors.Count > 0 ? TypedResults.ValidationProblem(errors) : null;
    }

    private static ProblemHttpResult OnlyAdminsApprove() => TypedResults.Problem(
        statusCode: StatusCodes.Status403Forbidden,
        title: "Only an admin can approve auto-send",
        detail: "Answers from an approved article can be sent without review, so an admin has to approve it.");

    private static Guid UserId(ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static string MakeSnippet(string body)
    {
        var text = string.Join(' ', body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= SnippetLength ? text : text[..SnippetLength].TrimEnd() + "…";
    }
}
