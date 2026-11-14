using Sortr.Core;

namespace Sortr.Tests;

public class TargetSearchTests
{
    private static readonly IReadOnlyList<Target> Targets =
        new[] { "Acme Corp", "Breaking Bad", "Breathe", "Élite", "Jane Doe", "The Office" }
            .Select(n => new Target(n, Path.Combine("C:\\Targets", n)))
            .ToList();

    private static List<string> Search(string? query) =>
        TargetSearch.Filter(Targets, query).Select(t => t.Name).ToList();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyQuery_ReturnsAllTargets(string? query)
    {
        Assert.Equal(Targets.Select(t => t.Name), Search(query));
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("BAD")]
    [InlineData("king b")]
    public void Substring_IsCaseInsensitive(string query)
    {
        Assert.Equal(["Breaking Bad"], Search(query));
    }

    [Theory]
    [InlineData("breakingbad", "Breaking Bad")]
    [InlineData("breaking.bad", "Breaking Bad")]
    [InlineData("jane_doe", "Jane Doe")]
    [InlineData("elite", "Élite")]
    public void SeparatorsAndAccents_AreIgnored(string query, string expected)
    {
        Assert.Equal([expected], Search(query));
    }

    [Fact]
    public void PrefixMatches_ComeFirst()
    {
        Assert.Equal(["The Office", "Breathe"], Search("the"));
    }

    [Fact]
    public void NoMatch_ReturnsEmpty()
    {
        Assert.Empty(Search("zebra"));
    }
}
