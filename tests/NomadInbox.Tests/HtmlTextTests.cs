using NomadInbox.Core.Content;

namespace NomadInbox.Tests;

public class HtmlTextTests
{
    [Fact]
    public void Converts_html_to_readable_text()
    {
        const string html = """
            <html><head><style>p { color: red }</style><title>x</title></head>
            <body><div>Hello&nbsp;<b>world</b> &amp; friends</div><p>Second</p>
            <ul><li>one</li><li>two</li></ul><!-- hidden --><script>alert(1)</script>Line<br>Break</body></html>
            """;

        var text = HtmlText.ToPlainText(html);

        Assert.Equal("Hello world & friends\nSecond\n\n• one\n• two\nLine\nBreak", text);
    }

    [Fact]
    public void Empty_input_gives_empty_text()
    {
        Assert.Equal("", HtmlText.ToPlainText(null));
        Assert.Equal("", HtmlText.ToPlainText("   "));
    }
}
