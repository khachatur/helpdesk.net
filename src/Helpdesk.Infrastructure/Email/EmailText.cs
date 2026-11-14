using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Helpdesk.Infrastructure.Email;

/// <summary>Turns email bodies into the plain text stored on a ticket.</summary>
public static partial class EmailText
{
    private static readonly HashSet<string> Invisible = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "head", "title", "template", "noscript",
    };

    private static readonly HashSet<string> Blocks = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "tr", "li", "ul", "ol", "table", "blockquote", "pre", "hr",
        "h1", "h2", "h3", "h4", "h5", "h6", "section", "article", "header", "footer",
    };

    /// <summary>Converts HTML to readable text, keeping paragraph and line breaks.</summary>
    public static string HtmlToText(string html)
    {
        var document = new HtmlParser().ParseDocument(html);
        var builder = new StringBuilder();
        if (document.Body is { } body)
        {
            Append(body, builder);
        }

        var text = builder.ToString().Replace(' ', ' ');
        text = SpacesAroundNewline().Replace(text, "\n");
        text = ThreeOrMoreNewlines().Replace(text, "\n\n");
        return text.Trim();
    }

    private static void Append(INode node, StringBuilder builder)
    {
        foreach (var child in node.ChildNodes)
        {
            switch (child)
            {
                case IText text:
                    builder.Append(Whitespace().Replace(text.Data, " "));
                    break;

                case IElement element when Invisible.Contains(element.LocalName):
                    break;

                case IElement element when element.LocalName == "br":
                    builder.Append('\n');
                    break;

                case IElement element when Blocks.Contains(element.LocalName):
                    builder.Append('\n');
                    Append(element, builder);
                    builder.Append('\n');
                    break;

                case IElement element:
                    Append(element, builder);
                    if (element.LocalName is "td" or "th")
                    {
                        builder.Append(' ');
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Removes the quoted earlier messages that mail clients append to a reply, leaving what the
    /// sender wrote. Recognises the common English-language markers; when nothing would be left,
    /// the text is returned unchanged, since the sender probably answered inline.
    /// </summary>
    public static string StripQuotedReply(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var cut = lines.Length;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();

            // "On Mon, 2 Nov 2026 at 10:15, Support <support@example.com> wrote:" - may wrap onto a second line.
            var isAttribution = OnWrote().IsMatch(line)
                || (line.StartsWith("On ", StringComparison.Ordinal)
                    && i + 1 < lines.Length
                    && OnWrote().IsMatch(line + " " + lines[i + 1].Trim()));

            // Outlook: a "From:" line followed shortly by "Sent:" or "Date:".
            var isOutlookHeader = line.StartsWith("From:", StringComparison.OrdinalIgnoreCase)
                && lines.Skip(i + 1).Take(4).Any(next => SentOrDate().IsMatch(next.Trim()));

            if (isAttribution || isOutlookHeader || OriginalMessage().IsMatch(line) || LongRule().IsMatch(line))
            {
                cut = i;
                break;
            }
        }

        // A trailing block made only of ">" quoted lines.
        var end = cut;
        while (end > 0 && (lines[end - 1].TrimStart().StartsWith('>') || string.IsNullOrWhiteSpace(lines[end - 1])))
        {
            end--;
        }

        if (lines.Take(cut).Skip(end).Any(line => line.TrimStart().StartsWith('>')))
        {
            cut = end;
        }

        var stripped = string.Join('\n', lines.Take(cut)).Trim();
        return stripped.Length == 0 ? text.Trim() : stripped;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[ \t]*\n[ \t]*")]
    private static partial Regex SpacesAroundNewline();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ThreeOrMoreNewlines();

    [GeneratedRegex(@"^On\s.+\swrote:$", RegexOptions.IgnoreCase)]
    private static partial Regex OnWrote();

    [GeneratedRegex(@"^(Sent|Date):\s", RegexOptions.IgnoreCase)]
    private static partial Regex SentOrDate();

    [GeneratedRegex(@"^-{2,}\s*(Original Message|Forwarded message)\s*-{2,}$", RegexOptions.IgnoreCase)]
    private static partial Regex OriginalMessage();

    [GeneratedRegex(@"^_{10,}$")]
    private static partial Regex LongRule();
}
