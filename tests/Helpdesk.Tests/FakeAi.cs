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
        Respond = DefaultResponse;
    }

    public Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(Respond(request));
    }

    /// <summary>Answers every classification with this category and confidence.</summary>
    public void ClassifyAs(string category, double confidence) =>
        Respond = request => request.JsonSchema is null
            ? Text("A summary.")
            : Text(JsonSerializer.Serialize(new { category, confidence, reason = "Test reason." }));

    public static AiResponse Text(string text) => new(text, InputTokens: 120, OutputTokens: 30);

    private static AiResponse DefaultResponse(AiRequest request) =>
        request.JsonSchema is null
            ? Text("A summary.")
            : Text("""{"category":"GeneralQuestion","confidence":0.9,"reason":"Test reason."}""");
}

/// <summary>Records which AI jobs were queued, in place of the background job queue.</summary>
public sealed class RecordingAiJobQueue : IAiJobQueue
{
    public List<int> Classifications { get; } = [];

    public List<int> Summaries { get; } = [];

    public void EnqueueClassification(int ticketId) => Classifications.Add(ticketId);

    public void EnqueueSummary(int ticketId) => Summaries.Add(ticketId);

    public void Reset()
    {
        Classifications.Clear();
        Summaries.Clear();
    }
}
