using AngleSharp.Html.Dom;
using Ganss.Xss;

namespace Helpdesk.Infrastructure.Tickets;

/// <summary>
/// Makes email HTML safe to show inside the app. Email bodies are untrusted: anyone can send one.
/// Only plain formatting survives. Scripts, styles, event handlers, forms, frames, and images
/// are removed; images because a remote image tells the sender when an agent opened the message.
/// </summary>
public sealed class EmailHtmlSanitizer
{
    private static readonly string[] AllowedTags =
    [
        "a", "b", "blockquote", "br", "code", "div", "em", "h1", "h2", "h3", "h4", "h5", "h6", "hr", "i",
        "li", "ol", "p", "pre", "s", "span", "strong", "sub", "sup", "table", "tbody", "td", "tfoot", "th",
        "thead", "tr", "u", "ul",
    ];

    /// <summary>Tags whose text content is code or markup, not something to show the reader.</summary>
    private static readonly HashSet<string> DroppedWithContent = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "head", "title", "template", "noscript", "iframe", "object", "embed", "svg",
        "math", "select", "textarea", "button",
    };

    private readonly HtmlSanitizer _sanitizer;

    public EmailHtmlSanitizer()
    {
        _sanitizer = new HtmlSanitizer
        {
            // An unknown wrapper tag (font, center, ...) is dropped but its text is kept.
            KeepChildNodes = true,
        };

        _sanitizer.AllowedTags.Clear();
        _sanitizer.AllowedTags.UnionWith(AllowedTags);

        _sanitizer.AllowedAttributes.Clear();
        _sanitizer.AllowedAttributes.UnionWith(["href", "colspan", "rowspan"]);

        _sanitizer.AllowedSchemes.Clear();
        _sanitizer.AllowedSchemes.UnionWith(["http", "https", "mailto"]);

        _sanitizer.AllowedCssProperties.Clear();
        _sanitizer.AllowedAtRules.Clear();

        _sanitizer.RemovingTag += (_, e) =>
        {
            if (DroppedWithContent.Contains(e.Tag.LocalName))
            {
                e.Tag.InnerHtml = "";
            }
        };

        // Links open in a new tab and give the target page no handle on this one.
        _sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is IHtmlAnchorElement anchor)
            {
                anchor.SetAttribute("target", "_blank");
                anchor.SetAttribute("rel", "noopener noreferrer nofollow");
            }
        };
    }

    public string Sanitize(string html) => _sanitizer.Sanitize(html);
}
