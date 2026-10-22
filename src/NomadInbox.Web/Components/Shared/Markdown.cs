using Markdig;
using Microsoft.AspNetCore.Components;

namespace NomadInbox.Web.Components.Shared;

/// <summary>Renders model-written Markdown. Raw HTML is disabled: summaries come from web pages, so treat them as untrusted.</summary>
public static class Markdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UseAutoLinks()
        .UseEmphasisExtras()
        .UsePipeTables()
        .Build();

    public static MarkupString Render(string markdown) => new(Markdig.Markdown.ToHtml(markdown, Pipeline));
}
