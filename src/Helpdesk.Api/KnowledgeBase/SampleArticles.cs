using Helpdesk.Infrastructure.KnowledgeBase;

namespace Helpdesk.Api.KnowledgeBase;

/// <summary>
/// Sample knowledge base for an imaginary online school. It exists so the screens and the search
/// have something to work with in development; replace it with real articles before going live.
/// The auto-send flags follow the scope: schedules, deadlines, password resets, course access and
/// certificates may be auto-sent; anything about money or exceptions may not.
/// </summary>
public static class SampleArticles
{
    public sealed record Sample(string Title, string Body, bool AutoSendAllowed);

    public static readonly IReadOnlyList<Sample> All =
    [
        new("Course start dates and schedules",
            """
            New groups start on the first Monday of every month for all courses, including data analytics, web development and UX design.

            Each course has a morning group (09:00-12:00) and an evening group (18:30-21:30), two sessions a week. The full schedule for your course, with every session date, is on the course page in the portal under "Schedule".

            Sessions are recorded. If you miss one, the recording appears in the portal within 24 hours.
            """,
            AutoSendAllowed: true),

        new("Enrollment deadlines",
            """
            Enrollment for a group closes at 23:59 on the Thursday before its start date. For the spring term, the last day to enrol is the Thursday before the first Monday of March.

            After the deadline you can join the next group, which starts one month later. Enrol from the course page in the portal: choose a start date, then "Enrol".
            """,
            AutoSendAllowed: true),

        new("How to reset your password",
            """
            If you cannot log in to the course portal or your password is rejected:

            1. Open the portal sign-in page and choose "Forgot password".
            2. Enter the email address you enrolled with.
            3. Open the reset email (check your spam folder) and follow the link within 30 minutes.
            4. Choose a new password of at least 10 characters.

            If the portal still says your credentials are invalid after a reset, clear your browser's saved passwords for the portal and sign in by typing the new password. Accounts lock for 15 minutes after five failed attempts.
            """,
            AutoSendAllowed: true),

        new("Getting access to course materials after payment",
            """
            Course materials unlock automatically once your payment is confirmed. Card payments are confirmed within a few minutes. Bank transfers take one to two working days.

            If you have a receipt but the course still shows as locked: sign out of the portal and sign in again, then open "My courses". If it is still locked two hours after a card payment, reply with your receipt number and we will unlock it by hand.
            """,
            AutoSendAllowed: true),

        new("Certificates of completion",
            """
            You receive a certificate when you have completed every module and passed the final project.

            Certificates are issued automatically within five working days of your final project being marked. You will get an email with a link, and the certificate also appears in the portal under "Achievements", where you can download it as a PDF or share it to LinkedIn.
            """,
            AutoSendAllowed: true),

        new("Video lessons will not play",
            """
            If a video lesson shows a spinner and never starts, or stops part-way:

            1. Reload the page.
            2. Try a private browsing window, which rules out browser extensions (ad blockers are the usual cause).
            3. Check that your network does not block video streaming; company and school networks often do.
            4. Lower the quality with the gear icon on the player.

            If only one module's videos fail while others play, the problem is on our side. Tell us which module and lesson.
            """,
            AutoSendAllowed: false),

        new("Assignment upload limits and errors",
            """
            Assignment uploads are limited to 50 MB per file. A larger file fails with error 413.

            To submit a larger project, remove build output and dependency folders (such as node_modules, bin and obj) before zipping, or upload the project to a Git repository and submit the link instead.

            Accepted file types are zip, pdf, docx and ipynb.
            """,
            AutoSendAllowed: false),

        new("Switching between morning and evening groups",
            """
            You can move between the morning and evening group of the same course once per course, if the other group has a free place.

            Ask at least three working days before the session you want to switch from. We confirm by email, and your portal schedule updates the same day.
            """,
            AutoSendAllowed: false),

        new("Refund policy",
            """
            Refunds depend on when you ask:

            - Before the course starts: a full refund.
            - Within 14 days of the start date, having attended no more than two sessions: a full refund.
            - After that: a refund for the months not yet started.

            If you enrolled in the wrong course, we can transfer you to another course instead of refunding; any price difference is charged or returned.

            Refunds go back to the original payment method and take five to ten working days to arrive. Every refund is reviewed by the finance team.
            """,
            AutoSendAllowed: false),

        new("Duplicate or incorrect charges",
            """
            If your statement shows two identical charges, or an amount you do not recognise, send us the date, the amount, and the last four digits of the card.

            A duplicate charge is refunded in full once the finance team has confirmed it, usually within three working days. A pending charge that later disappears from your statement was an authorisation, not a payment, and needs no refund.
            """,
            AutoSendAllowed: false),

        new("Withdrawing from a course",
            """
            To withdraw, tell us in writing which course you are leaving and from what date.

            You keep access to the materials for the months you have paid for. Any refund follows the refund policy. If you are withdrawing because of illness or a family emergency, tell us: the finance team can pause your enrollment for up to six months instead, so you can return without paying again.
            """,
            AutoSendAllowed: false),
    ];

    /// <summary>Loads the samples into an empty knowledge base when Seed:SampleArticles is true.</summary>
    public static async Task SeedSampleArticlesAsync(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Seed:SampleArticles"))
        {
            return;
        }

        var store = app.Services.GetRequiredService<ArticleStore>();
        if (await store.AnyAsync(CancellationToken.None))
        {
            return;
        }

        foreach (var sample in All)
        {
            await store.CreateAsync(
                new ArticleContent(sample.Title, Unindent(sample.Body), IsPublished: true, sample.AutoSendAllowed),
                userId: null,
                CancellationToken.None);
        }

        app.Logger.LogInformation("Created {Count} sample knowledge base articles", All.Count);
    }

    public static string Unindent(string text) => text.Replace("\r\n", "\n").Trim();
}
