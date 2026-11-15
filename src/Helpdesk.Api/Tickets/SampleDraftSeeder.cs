using Dapper;
using Helpdesk.Core.Tickets;
using Helpdesk.Infrastructure.Data;
using Helpdesk.Infrastructure.Tickets;

namespace Helpdesk.Api.Tickets;

/// <summary>
/// Puts one AI-style suggested reply on a sample ticket, so the suggested-reply card can be seen
/// and tested without an API key. Runs only when Seed:SampleDrafts is true and no draft exists yet.
/// It needs the sample tickets and sample articles to be there.
/// </summary>
public static class SampleDraftSeeder
{
    public const string TicketSubject = "Certificate after finishing the course";
    private const string ArticleTitle = "Certificates of completion";

    public const string Body = """
        Hi,

        Congratulations on completing all the modules. You receive your certificate once your final project has been marked. Certificates are issued automatically within five working days of that: you will get an email with a link, and the certificate also appears in the portal under "Achievements", where you can download it as a PDF.

        Kind regards,
        The Support Team
        """;

    public static async Task SeedSampleDraftsAsync(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Seed:SampleDrafts"))
        {
            return;
        }

        await using var connection = await app.Services.GetRequiredService<IDbConnectionFactory>().OpenAsync();
        var target = await connection.QuerySingleOrDefaultAsync<(int TicketId, int ArticleId, int Messages)?>(
            """
            SELECT TOP (1) t.Id, a.Id, (SELECT COUNT(*) FROM dbo.TicketMessages m WHERE m.TicketId = t.Id)
            FROM dbo.Tickets t
            CROSS JOIN dbo.Articles a
            WHERE t.Subject = @TicketSubject AND t.Status = N'Open' AND a.Title = @ArticleTitle
              AND NOT EXISTS (SELECT 1 FROM dbo.TicketDrafts)
              AND NOT EXISTS (SELECT 1 FROM dbo.TicketMessages m WHERE m.TicketId = t.Id AND m.Direction = N'Outbound')
            ORDER BY t.Id DESC
            """,
            new { TicketSubject, ArticleTitle });

        if (target is not { } found)
        {
            return;
        }

        await app.Services.GetRequiredService<TicketStore>().SaveDraftAsync(
            found.TicketId,
            Body.Replace("\r\n", "\n"),
            confidence: 0.91,
            [found.ArticleId],
            found.Messages,
            new AutoSendResult(AutoSendDecision.Hold, ["Auto-send is switched off."]),
            CancellationToken.None);

        app.Logger.LogInformation("Created a sample suggested reply on ticket {TicketId}", found.TicketId);
    }
}
