using Helpdesk.Infrastructure.Tickets;

namespace Helpdesk.Tests;

public class EmailHtmlSanitizerTests
{
    private readonly EmailHtmlSanitizer _sanitizer = new();

    [Theory]
    [InlineData("<p>Hi</p><script>alert(1)</script>")]
    [InlineData("<p onclick=\"alert(1)\">Hi</p>")]
    [InlineData("<img src=x onerror=\"alert(1)\">Hi")]
    [InlineData("<a href=\"javascript:alert(1)\">Hi</a>")]
    [InlineData("<a href=\"data:text/html,<script>alert(1)</script>\">Hi</a>")]
    [InlineData("<iframe src=\"https://evil.example\"></iframe>Hi")]
    [InlineData("<svg onload=\"alert(1)\"><text>x</text></svg>Hi")]
    [InlineData("<style>body{background:url(javascript:alert(1))}</style>Hi")]
    [InlineData("<form action=\"https://evil.example\"><input name=\"password\"><button>Send</button></form>Hi")]
    [InlineData("<div style=\"position:fixed;top:0;left:0;width:100%;height:100%\">Hi</div>")]
    [InlineData("<math><mtext><script>alert(1)</script></mtext></math>Hi")]
    public void Removes_anything_that_can_run_code_or_restyle_the_page(string html)
    {
        var result = _sanitizer.Sanitize(html);

        Assert.Contains("Hi", result);
        Assert.DoesNotContain("alert", result);
        Assert.DoesNotContain("javascript:", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evil.example", result);
        Assert.DoesNotContain("style", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<input", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("on", ExtractAttributeNames(result));
    }

    [Fact]
    public void Removes_remote_images_so_opening_a_message_is_not_tracked()
    {
        var result = _sanitizer.Sanitize("<p>Hello</p><img src=\"https://tracker.example/pixel.gif\">");

        Assert.Equal("<p>Hello</p>", result);
    }

    [Fact]
    public void Keeps_plain_formatting()
    {
        const string html = "<p>Hello <b>team</b>, <i>thanks</i></p><ul><li>One</li><li>Two</li></ul>" +
                            "<blockquote>Quoted</blockquote><table><tbody><tr><td colspan=\"2\">Cell</td></tr></tbody></table>";

        Assert.Equal(html, _sanitizer.Sanitize(html));
    }

    [Fact]
    public void Keeps_the_text_of_unknown_wrapper_tags()
    {
        var result = _sanitizer.Sanitize("<center><font color=\"red\">Important</font> notice</center>");

        Assert.Equal("Important notice", result);
    }

    [Fact]
    public void Links_open_in_a_new_tab_without_access_to_the_app()
    {
        var result = _sanitizer.Sanitize("<a href=\"https://example.com/page\" onclick=\"x()\">link</a>");

        Assert.Contains("href=\"https://example.com/page\"", result);
        Assert.Contains("target=\"_blank\"", result);
        Assert.Contains("rel=\"noopener noreferrer nofollow\"", result);
        Assert.DoesNotContain("onclick", result);
    }

    [Fact]
    public void Keeps_mailto_links()
    {
        var result = _sanitizer.Sanitize("<a href=\"mailto:someone@example.com\">write</a>");

        Assert.Contains("href=\"mailto:someone@example.com\"", result);
    }

    /// <summary>Attribute names in the output that start with "on", reduced to that prefix.</summary>
    private static IEnumerable<string> ExtractAttributeNames(string html) =>
        System.Text.RegularExpressions.Regex.Matches(html, @"\s(on)\w+\s*=", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(match => match.Groups[1].Value.ToLowerInvariant());
}
