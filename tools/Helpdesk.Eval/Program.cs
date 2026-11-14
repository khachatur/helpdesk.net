using System.Text.Json;
using Helpdesk.Core.Tickets;
using Helpdesk.Infrastructure.Ai;
using Microsoft.Extensions.Options;

// Measures how well the classifier sorts a set of labelled emails, using the real model.
// Every run calls the Anthropic API once per sample and so costs money.
//
//   $env:ANTHROPIC_API_KEY = "<key>"
//   dotnet run --project tools/Helpdesk.Eval -- [samples.json] [--model <id>] [--min-accuracy 0.9]
//
// Exits with 1 when accuracy is below --min-accuracy, so it can gate a prompt or model change.

var samplesPath = args.FirstOrDefault(arg => !arg.StartsWith("--")) ?? Path.Combine("eval", "classification-samples.json");
var options = new AiOptions
{
    Mode = AiMode.Anthropic,
    ApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "",
};
if (Option("--model") is { } model)
{
    options.ClassificationModel = model;
}

double? minAccuracy = Option("--min-accuracy") is { } min ? double.Parse(min, System.Globalization.CultureInfo.InvariantCulture) : null;

if (string.IsNullOrWhiteSpace(options.ApiKey))
{
    Console.Error.WriteLine("Set the ANTHROPIC_API_KEY environment variable first.");
    return 2;
}

if (!File.Exists(samplesPath))
{
    Console.Error.WriteLine($"Samples file not found: {samplesPath}");
    return 2;
}

var samples = JsonSerializer.Deserialize<List<Sample>>(
    File.ReadAllText(samplesPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
var classifier = new EmailClassifier(new AnthropicAiClient(Options.Create(options)), Options.Create(options));

Console.WriteLine($"Classifying {samples.Count} samples with {options.ClassificationModel} (confidence threshold {options.ConfidenceThreshold})");
Console.WriteLine();

var results = new List<Result>();
foreach (var sample in samples)
{
    string actual;
    double confidence = 0;
    string detail = "";
    int inputTokens = 0, outputTokens = 0;

    try
    {
        var attempt = await classifier.ClassifyAsync(sample.Subject, sample.Body, CancellationToken.None);
        inputTokens = attempt.Response.InputTokens;
        outputTokens = attempt.Response.OutputTokens;

        if (attempt.Classification is { } classification)
        {
            actual = classification.Category?.ToString() ?? "Unclear";
            confidence = classification.Confidence;
            detail = classification.Reason;
        }
        else
        {
            actual = attempt.Outcome.ToString();
            detail = attempt.Problem ?? "";
        }
    }
    catch (AiException ex)
    {
        actual = "Error";
        detail = ex.Message;
    }

    var result = new Result(sample, actual, confidence, detail, inputTokens, outputTokens);
    results.Add(result);
    Console.WriteLine($"{(result.Correct ? "ok  " : "MISS")} {confidence,4:0.00}  {sample.Expected,-18} {(result.Correct ? "" : "got " + actual + "  ")}{sample.Subject}");
}

var correct = results.Count(result => result.Correct);
var accuracy = (double)correct / results.Count;

Console.WriteLine();
Console.WriteLine($"Accuracy: {correct}/{results.Count} ({accuracy:P1})");

foreach (var group in results.GroupBy(result => result.Sample.Expected).OrderBy(group => group.Key))
{
    Console.WriteLine($"  {group.Key,-18} {group.Count(result => result.Correct)}/{group.Count()}");
}

// What the confidence threshold does: a wrong answer below it never reaches a ticket,
// and a right answer below it costs an agent a manual categorisation.
var categorised = results.Where(result => Enum.TryParse<TicketCategory>(result.Actual, out _)).ToList();
var applied = categorised.Where(result => result.Confidence >= options.ConfidenceThreshold).ToList();
Console.WriteLine();
Console.WriteLine($"Applied automatically (confidence >= {options.ConfidenceThreshold}): {applied.Count} of {results.Count}");
Console.WriteLine($"  of which wrong: {applied.Count(result => !result.Correct)}");
Console.WriteLine($"Left for an agent although the AI was right: {categorised.Count(result => result.Correct && result.Confidence < options.ConfidenceThreshold)}");

var injections = results.Where(result => result.Sample.Injection).ToList();
if (injections.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"Prompt-injection samples classified by real need: {injections.Count(result => result.Correct)}/{injections.Count}");
}

var misses = results.Where(result => !result.Correct).ToList();
if (misses.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("Misses:");
    foreach (var miss in misses)
    {
        Console.WriteLine($"  \"{miss.Sample.Subject}\": expected {miss.Sample.Expected}, got {miss.Actual} ({miss.Confidence:0.00}). {miss.Detail}");
    }
}

Console.WriteLine();
Console.WriteLine($"Tokens: {results.Sum(result => result.InputTokens):N0} in, {results.Sum(result => result.OutputTokens):N0} out");

return minAccuracy is { } required && accuracy < required ? 1 : 0;

string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

/// <param name="Expected">A category name, or "Unclear" when the email fits none.</param>
/// <param name="Injection">True when the email tries to instruct the classifier.</param>
internal sealed record Sample(string Subject, string Body, string Expected, bool Injection = false);

internal sealed record Result(Sample Sample, string Actual, double Confidence, string Detail, int InputTokens, int OutputTokens)
{
    public bool Correct => Actual == Sample.Expected;
}
