using NomadInbox.Core.Subjects;

namespace NomadInbox.Tests;

public class SubjectParserTests
{
    private const string Prefix = "NOMAD:";

    [Fact]
    public void Parses_full_subject()
    {
        var p = SubjectParser.Parse("NOMAD: Survival/Water #filtration #DIY priority=high Ceramic filter builds", Prefix)!;

        Assert.Equal("Survival/Water", p.Category);
        Assert.Equal(["filtration", "diy"], p.Tags);
        Assert.Equal("high", p.Fields["priority"]);
        Assert.Equal("Ceramic filter builds", p.Title);
    }

    [Theory]
    [InlineData("nomad: hello")]
    [InlineData("  NOMAD:hello")]
    [InlineData("Fwd: NOMAD: hello")]
    [InlineData("RE: Fw: NOMAD: hello")]
    public void Matches_prefix_case_insensitively_after_reply_markers(string subject)
    {
        Assert.Equal("hello", SubjectParser.Parse(subject, Prefix)?.Title);
    }

    [Theory]
    [InlineData("Re: about NOMAD: stuff")]
    [InlineData("NOMADS: stuff")]
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_subjects_not_starting_with_prefix(string? subject)
    {
        Assert.Null(SubjectParser.Parse(subject, Prefix));
    }

    [Fact]
    public void Category_only_from_first_token()
    {
        var p = SubjectParser.Parse("NOMAD: Notes on 24/7 power", Prefix)!;

        Assert.Null(p.Category);
        Assert.Equal("Notes on 24/7 power", p.Title);
    }

    [Theory]
    [InlineData("NOMAD: Survival/ Knots", "Survival")]
    [InlineData("NOMAD: cat=Survival Knots", "Survival")]
    [InlineData("NOMAD: \"/Medical//First Aid/\" Knots", "Medical/First Aid")]
    public void Single_level_and_normalized_categories(string subject, string expected)
    {
        Assert.Equal(expected, SubjectParser.Parse(subject, Prefix)!.Category);
    }

    [Fact]
    public void Quoted_field_values_and_urls_in_title()
    {
        var p = SubjectParser.Parse("NOMAD: source=\"Prepper Guy\" see https://example.com/a/b", Prefix)!;

        Assert.Null(p.Category);
        Assert.Equal("Prepper Guy", p.Fields["source"]);
        Assert.Equal("see https://example.com/a/b", p.Title);
    }

    [Fact]
    public void Url_as_first_token_is_not_a_category()
    {
        var p = SubjectParser.Parse("NOMAD: https://example.com/page", Prefix)!;

        Assert.Null(p.Category);
        Assert.Equal("https://example.com/page", p.Title);
    }

    [Fact]
    public void Duplicate_tags_are_collapsed()
    {
        Assert.Equal(["a"], SubjectParser.Parse("NOMAD: #a #A", Prefix)!.Tags);
    }

    [Fact]
    public void Prefix_only_gives_empty_result()
    {
        var p = SubjectParser.Parse("NOMAD:", Prefix)!;

        Assert.Null(p.Category);
        Assert.Empty(p.Tags);
        Assert.Empty(p.Fields);
        Assert.Equal("", p.Title);
    }
}
