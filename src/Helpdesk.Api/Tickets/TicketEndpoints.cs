using System.Security.Claims;
using Helpdesk.Core.Tickets;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Tickets;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace Helpdesk.Api.Tickets;

/// <summary>Tickets. Any signed-in user (admin or agent) can see and work on every ticket.</summary>
public static class TicketEndpoints
{
    public const int MaxReplyLength = 20_000;

    /// <summary>Query string of the ticket list. Every part is optional.</summary>
    /// <param name="Status">Repeat to include several statuses.</param>
    /// <param name="Category">A category name, or "unclassified".</param>
    /// <param name="Assignee">"me", "unassigned", or a user id.</param>
    /// <param name="Sort">A <see cref="TicketSort"/> name. Defaults to createdAt.</param>
    public sealed record TicketListRequest(
        TicketStatus[]? Status,
        string? Category,
        string? Assignee,
        int? Team,
        string? Search,
        string? Sort,
        bool? Desc,
        int? Skip,
        int? Take);

    public sealed record ChangeStatusRequest(TicketStatus? Status);

    public sealed record ChangeCategoryRequest(TicketCategory? Category);

    public sealed record AddReplyRequest(string? Body, bool Resolve = false);

    public sealed record ReplyCreatedResponse(long MessageId);

    public static IEndpointRouteBuilder MapTicketEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/tickets").WithTags("Tickets");

        group.MapGet("/", ListAsync);
        group.MapGet("/{id:int}", GetAsync);
        group.MapPut("/{id:int}/status", ChangeStatusAsync);
        group.MapPut("/{id:int}/category", ChangeCategoryAsync);
        group.MapPost("/{id:int}/replies", AddReplyAsync);
        group.MapPost("/{id:int}/messages/{messageId:long}/retry-send", RetrySendAsync);

        return routes;
    }

    private static async Task<Results<Ok<TicketListResponse>, ValidationProblem>> ListAsync(
        [AsParameters] TicketListRequest request,
        ClaimsPrincipal caller,
        TicketStore store,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        TicketCategory? category = null;
        var unclassified = string.Equals(request.Category, "unclassified", StringComparison.OrdinalIgnoreCase);
        if (!unclassified && !string.IsNullOrEmpty(request.Category))
        {
            if (TryParseName<TicketCategory>(request.Category, out var parsed))
            {
                category = parsed;
            }
            else
            {
                errors["category"] = ["Use a category name or 'unclassified'."];
            }
        }

        Guid? assigneeId = null;
        var unassigned = string.Equals(request.Assignee, "unassigned", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(request.Assignee, "me", StringComparison.OrdinalIgnoreCase))
        {
            assigneeId = caller.GetUserId();
        }
        else if (!unassigned && !string.IsNullOrEmpty(request.Assignee))
        {
            if (Guid.TryParse(request.Assignee, out var parsed))
            {
                assigneeId = parsed;
            }
            else
            {
                errors["assignee"] = ["Use 'me', 'unassigned', or a user id."];
            }
        }

        var sort = TicketSort.CreatedAt;
        if (!string.IsNullOrEmpty(request.Sort) && !TryParseName(request.Sort, out sort))
        {
            errors["sort"] = [$"Sort by one of: {string.Join(", ", Enum.GetNames<TicketSort>())}."];
        }

        if (request.Skip < 0)
        {
            errors["skip"] = ["Skip cannot be negative."];
        }

        if (request.Take is < 1 or > TicketListQuery.MaxPageSize)
        {
            errors["take"] = [$"Take must be between 1 and {TicketListQuery.MaxPageSize}."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var page = await store.ListAsync(
            new TicketListQuery
            {
                Statuses = request.Status ?? [],
                Category = category,
                Unclassified = unclassified,
                AssigneeId = assigneeId,
                Unassigned = unassigned,
                TeamId = request.Team,
                Search = request.Search,
                Sort = sort,
                // Newest first unless the caller says otherwise.
                Descending = request.Desc ?? string.IsNullOrEmpty(request.Sort),
                Skip = request.Skip ?? 0,
                Take = request.Take ?? 25,
            },
            cancellationToken);

        return TypedResults.Ok(new TicketListResponse(page.Items.Select(TicketSummaryResponse.From), page.TotalCount));
    }

    private static async Task<Results<Ok<TicketDetailResponse>, NotFound>> GetAsync(
        int id, TicketStore store, EmailHtmlSanitizer sanitizer, CancellationToken cancellationToken)
    {
        var ticket = await store.GetAsync(id, cancellationToken);
        return ticket is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(TicketDetailResponse.From(ticket, sanitizer));
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem, ProblemHttpResult>> ChangeStatusAsync(
        int id, ChangeStatusRequest request, ClaimsPrincipal caller, TicketStore store, CancellationToken cancellationToken)
    {
        if (request.Status is not { } status)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["status"] = ["Choose a status."] });
        }

        var result = await store.ChangeStatusAsync(id, status, caller.GetUserId(), cancellationToken);
        return result switch
        {
            TicketChangeResult.Ok => TypedResults.NoContent(),
            TicketChangeResult.NotFound => TypedResults.NotFound(),
            _ => TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Status change not allowed",
                detail: "A closed ticket cannot be reopened, and only the system can mark a ticket as needing human review."),
        };
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem, ProblemHttpResult>> ChangeCategoryAsync(
        int id, ChangeCategoryRequest request, ClaimsPrincipal caller, TicketStore store, CancellationToken cancellationToken)
    {
        if (request.Category is not { } category)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["category"] = ["Choose a category."] });
        }

        var result = await store.ChangeCategoryAsync(id, category, caller.GetUserId(), cancellationToken);
        return result switch
        {
            TicketChangeResult.Ok => TypedResults.NoContent(),
            TicketChangeResult.NotFound => TypedResults.NotFound(),
            _ => ClosedTicketProblem(),
        };
    }

    private static async Task<Results<Created<ReplyCreatedResponse>, NotFound, ValidationProblem, ProblemHttpResult>> AddReplyAsync(
        int id,
        AddReplyRequest request,
        ClaimsPrincipal caller,
        TicketStore store,
        IOptions<EmailOptions> email,
        IReplyQueue replyQueue,
        CancellationToken cancellationToken)
    {
        var body = request.Body?.Trim() ?? "";
        if (body.Length == 0)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["body"] = ["Write a reply."] });
        }

        if (body.Length > MaxReplyLength)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["body"] = [$"A reply can be at most {MaxReplyLength:N0} characters."],
            });
        }

        // The reply is stored first and emailed in the background, so a mail outage never loses what the agent wrote.
        var send = email.Value.IsEnabled;
        var (result, messageId) = await store.AddAgentReplyAsync(
            id, caller.GetUserId(), body, request.Resolve, send, cancellationToken);
        if (result == TicketChangeResult.Ok && send)
        {
            replyQueue.Enqueue(messageId!.Value);
        }

        return result switch
        {
            TicketChangeResult.Ok => TypedResults.Created($"/api/tickets/{id}", new ReplyCreatedResponse(messageId!.Value)),
            TicketChangeResult.NotFound => TypedResults.NotFound(),
            _ => ClosedTicketProblem(),
        };
    }

    /// <summary>Tries again to email a reply whose sending failed.</summary>
    private static async Task<Results<NoContent, NotFound>> RetrySendAsync(
        int id, long messageId, TicketStore store, IReplyQueue replyQueue, CancellationToken cancellationToken)
    {
        if (!await store.RetrySendAsync(id, messageId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        replyQueue.Enqueue(messageId);
        return TypedResults.NoContent();
    }

    private static ProblemHttpResult ClosedTicketProblem() => TypedResults.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Ticket is closed",
        detail: "A closed ticket cannot be changed.");

    private static Guid GetUserId(this ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>Parses an enum by name only. Numbers are rejected, since "7" is not a meaningful value to accept.</summary>
    private static bool TryParseName<TEnum>(string value, out TEnum result)
        where TEnum : struct, Enum
    {
        result = default;
        return !int.TryParse(value, out _) && Enum.TryParse(value, ignoreCase: true, out result) && Enum.IsDefined(result);
    }
}
