using Helpdesk.Core.Tickets;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Tickets;

namespace Helpdesk.Api.Tickets;

/// <summary>
/// Fills an empty database with sample tickets so the screens have something to show before
/// email integration exists. Runs only when Seed:SampleTickets is true, which is set for development only.
/// </summary>
public static class SampleTicketSeeder
{
    private static readonly (string Name, string Email)[] Requesters =
    [
        ("Maya Patel", "maya.patel@example.com"),
        ("Liam O'Connor", "liam.oconnor@example.com"),
        ("Sofia Rossi", "sofia.rossi@example.com"),
        ("Noah Kim", "noah.kim@example.com"),
        ("Amara Okafor", "amara.okafor@example.com"),
        ("Lucas Meyer", "lucas.meyer@example.com"),
        ("Hana Suzuki", "hana.suzuki@example.com"),
        ("Diego Alvarez", "diego.alvarez@example.com"),
    ];

    private static readonly (TicketCategory? Category, string Subject, string Body)[] Samples =
    [
        (TicketCategory.GeneralQuestion, "When does the next data analytics course start?",
            "Hi,\n\nI'd like to join the data analytics course. When is the next start date, and is there a deadline to enrol?\n\nThanks"),
        (TicketCategory.GeneralQuestion, "Certificate after finishing the course",
            "Hello, I completed all modules last week. How and when do I get my certificate?"),
        (TicketCategory.GeneralQuestion, "Can I switch to the evening group?",
            "My work schedule changed. Is it possible to move from the morning group to the evening one?"),
        (TicketCategory.GeneralQuestion, "Enrollment deadline for spring term",
            "Could you tell me the last day to enrol for the spring term? I can't find it on the website."),
        (TicketCategory.TechnicalQuestion, "Can't log in to the course portal",
            "I reset my password twice but the portal still says my credentials are invalid. I'm using Chrome on Windows."),
        (TicketCategory.TechnicalQuestion, "Video lessons won't play",
            "The videos in module 3 show a spinner and never start. Other modules work fine. Tried two browsers."),
        (TicketCategory.TechnicalQuestion, "Assignment upload fails with error 413",
            "When I upload my project zip (about 80 MB) I get an error 413. Is there a size limit?"),
        (TicketCategory.TechnicalQuestion, "No access to course materials after payment",
            "I paid yesterday and got the receipt, but the course still shows as locked in my account."),
        (TicketCategory.RefundRequest, "Refund request - enrolled in the wrong course",
            "I signed up for the advanced course by mistake and meant to take the beginner one. Can I get a refund or a transfer?"),
        (TicketCategory.RefundRequest, "Charged twice for the same course",
            "My card statement shows two identical charges from last Friday. Please refund the duplicate."),
        (TicketCategory.RefundRequest, "Withdrawing from the course",
            "Due to a family situation I have to withdraw. I've attended two sessions. What refund am I entitled to?"),
        (null, "Question",
            "Hi, I have a question about my account and also about the schedule, who should I talk to?"),
        (null, "Re: Welcome to the course!",
            "Thanks for the welcome email. One thing - the link in it doesn't open for me."),
    ];

    private const string HtmlSample = """
        <div style="font-family: Arial; position: fixed; top: 0">
          <p>Hello <b>support team</b>,</p>
          <p>My invoice shows the <i>wrong company name</i>. Details:</p>
          <ul><li>Invoice: INV-2041</li><li>Should read: Northwind Learning Ltd</li></ul>
          <p>See <a href="https://example.com/invoice/2041">the invoice</a>.</p>
          <img src="https://example.com/tracking-pixel.gif" onerror="alert('xss')">
          <script>alert('xss')</script>
        </div>
        """;

    public static async Task SeedSampleTicketsAsync(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Seed:SampleTickets"))
        {
            return;
        }

        var store = app.Services.GetRequiredService<TicketStore>();
        if (await store.AnyAsync(CancellationToken.None))
        {
            return;
        }

        var users = await app.Services.GetRequiredService<UserQueries>().ListAsync(CancellationToken.None);
        if (users.Count == 0)
        {
            return;
        }

        var agentId = users[0].Id;
        var now = DateTimeOffset.UtcNow;
        const int count = 60;

        for (var i = 0; i < count; i++)
        {
            var (category, subject, body) = Samples[i % Samples.Length];
            var (name, email) = Requesters[(i * 3) % Requesters.Length];
            // Oldest first, spread over the last two weeks, so ticket numbers rise with time.
            var receivedAt = now.AddHours(-(count - i) * 5.5);

            var id = await store.CreateAsync(
                new NewTicket(subject, email, name, body, Category: category, ReceivedAt: receivedAt),
                CancellationToken.None);

            // Older tickets are mostly finished; recent ones are mostly still open.
            switch (i % 6)
            {
                case 0 when i < 40:
                    await store.AddAgentReplyAsync(id, agentId, "Thanks for getting in touch. This is now sorted - let us know if anything else comes up.", resolve: true, send: false, CancellationToken.None);
                    await store.ChangeStatusAsync(id, TicketStatus.Closed, agentId, CancellationToken.None);
                    break;
                case 1 when i < 50:
                    await store.AddAgentReplyAsync(id, agentId, "Hi, thanks for your message. I've looked into this and it should be resolved now.", resolve: true, send: false, CancellationToken.None);
                    break;
                case 2:
                    await store.AddAgentReplyAsync(id, agentId, "Thanks - could you send a screenshot of what you see?", resolve: false, send: false, CancellationToken.None);
                    break;
            }
        }

        await store.CreateAsync(
            new NewTicket(
                "Wrong company name on invoice (HTML email)",
                "billing@northwind.example.com",
                "Northwind Learning",
                "Hello support team,\n\nMy invoice shows the wrong company name.\n\nInvoice: INV-2041\nShould read: Northwind Learning Ltd",
                BodyHtml: HtmlSample,
                Category: TicketCategory.GeneralQuestion),
            CancellationToken.None);

        app.Logger.LogInformation("Created {Count} sample tickets", count + 1);
    }
}
