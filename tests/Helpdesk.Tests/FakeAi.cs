using System.Text.Json;
using Helpdesk.Infrastructure.Ai;

namespace Helpdesk.Tests;

/// <summary>An AI that answers whatever the test tells it to, and remembers what it was asked.</summary>
public sealed class FakeAiClient : IAiClient
{
    public List<AiRequest> Requests { get; } = [];

    /// <summary>Produces the answer to each request. Throw from it to simulate a failed call.</summary>
    public Func<AiRequest, AiResponse> Respond { get; set; } = DefaultResponse;

    public void Reset()
    {
        Requests.Clear();
        _classification = "{\"category\":\"GeneralQuestion\",\"confidence\":0.9,\"reason\":\"Test reason.\"}";
        _draft = DraftJson("A drafted reply.", [], 0.9);
        Respond = DefaultResponse;
    }

    public Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(Respond(request));
    }

    /// <summary>What kind of request this is, judged the way the real model would see it: by the schema asked for.</summary>
    public static bool IsClassification(AiRequest request) =>
        request.JsonSchema is not null && request.JsonSchema["properties"].TryGetProperty("category", out _);

    public static bool IsDraft(AiRequest request) =>
        request.JsonSchema is not null && request.JsonSchema["properties"].TryGetProperty("reply", out _);

    private string _classification = "{\"category\":\"GeneralQuestion\",\"confidence\":0.9,\"reason\":\"Test reason.\"}";
    private string _draft = DraftJson("A drafted reply.", [], 0.9);

    /// <summary>Answers every classification with this category and confidence.</summary>
    public void ClassifyAs(string category, double confidence)
    {
        _classification = JsonSerializer.Serialize(new { category, confidence, reason = "Test reason." });
        Respond = ByKind;
    }

    /// <summary>Answers every draft request with this reply.</summary>
    public void DraftAs(string reply, int[] sourceArticleIds, double confidence, bool canAnswer = true, string sensitiveTopic = "none")
    {
        _draft = DraftJson(reply, sourceArticleIds, confidence, canAnswer, sensitiveTopic);
        Respond = ByKind;
    }

    /// <summary>Answers every draft request with this raw text, valid or not.</summary>
    public void DraftRaw(string text)
    {
        _draft = text;
        Respond = ByKind;
    }

    public static string DraftJson(string reply, int[] sourceArticleIds, double confidence, bool canAnswer = true, string sensitiveTopic = "none") =>
        JsonSerializer.Serialize(new
        {
            can_answer = canAnswer,
            reply,
            source_article_ids = sourceArticleIds,
            sensitive_topic = sensitiveTopic,
            confidence,
        });

    private AiResponse ByKind(AiRequest request) =>
        IsClassification(request) ? Text(_classification) : IsDraft(request) ? Text(_draft) : Text("A summary.");

    public static AiResponse Text(string text) => new(text, InputTokens: 120, OutputTokens: 30);

    private static AiResponse DefaultResponse(AiRequest request) =>
        IsClassification(request)
            ? Text("""{"category":"GeneralQuestion","confidence":0.9,"reason":"Test reason."}""")
            : IsDraft(request) ? Text(DraftJson("A drafted reply.", [], 0.9)) : Text("A summary.");
}

/// <summary>Records which AI jobs were queued, in place of the background job queue.</summary>
public sealed class RecordingAiJobQueue : IAiJobQueue
{
    public List<int> Classifications { get; } = [];

    public List<int> Summaries { get; } = [];

    public List<int> Drafts { get; } = [];

    public void EnqueueClassification(int ticketId) => Classifications.Add(ticketId);

    public void EnqueueSummary(int ticketId) => Summaries.Add(ticketId);

    public void EnqueueDraft(int ticketId) => Drafts.Add(ticketId);

    public void Reset()
    {
        Classifications.Clear();
        Summaries.Clear();
        Drafts.Clear();
    }
}
