using Sortr.Core;

namespace Sortr.Tests;

public class NameMatcherTests
{
    private static NameMatcher CreateMatcher(params string[] names) =>
        new(names.Select(n => new Target(n, Path.Combine("C:\\Targets", n))).ToList());

    private static List<string> MatchNames(NameMatcher matcher, string fileName) =>
        matcher.Match(fileName).Select(c => c.Target.Name).ToList();

    [Theory]
    [InlineData("Breaking.Bad", new[] { "breaking", "bad" })]
    [InlineData("breaking_bad", new[] { "breaking", "bad" })]
    [InlineData("  BREAKING -- Bad ", new[] { "breaking", "bad" })]
    [InlineData("Élite", new[] { "elite" })]
    [InlineData("Cœur-Brisé", new[] { "cœur", "brise" })]
    public void Tokenize_NormalizesCaseSeparatorsAndAccents(string input, string[] expected)
    {
        Assert.Equal(expected, NameNormalizer.Tokenize(input));
    }

    [Theory]
    [InlineData("breaking.bad.s01e01.720p.mkv")]
    [InlineData("Breaking Bad - S02E03.mkv")]
    [InlineData("[WEB] breaking_bad (2008).mp4")]
    [InlineData("Bad_Breaking.mkv")]
    [InlineData("Bad, Breaking.mp4")]
    public void SeparatedNames_AreStrongMatches(string fileName)
    {
        var result = Assert.Single(CreateMatcher("Breaking Bad", "The Office").Match(fileName));
        Assert.Equal("Breaking Bad", result.Target.Name);
        Assert.Equal(MatchConfidence.Strong, result.Confidence);
    }

    [Theory]
    [InlineData("BreakingBad.mkv")]
    [InlineData("breakingbad_s01e02.mkv")]
    [InlineData("webripBREAKINGBAD.mkv")]
    [InlineData("BadBreaking.mkv")]
    public void FusedNames_AreLooseMatches(string fileName)
    {
        var result = Assert.Single(CreateMatcher("Breaking Bad", "The Office").Match(fileName));
        Assert.Equal("Breaking Bad", result.Target.Name);
        Assert.Equal(MatchConfidence.Loose, result.Confidence);
    }

    [Fact]
    public void SingleWordName_MatchesWholeTokenOnly()
    {
        var matcher = CreateMatcher("Friends");
        Assert.Equal(["Friends"], MatchNames(matcher, "friends_s01e01.mkv"));
        Assert.Empty(matcher.Match("boyfriends.mkv"));
        Assert.Empty(matcher.Match("superfriendsrecap.mkv"));
    }

    [Fact]
    public void ShortMultiWordName_DoesNotMatchLoosely()
    {
        var matcher = CreateMatcher("Ab Fab");
        Assert.Equal(["Ab Fab"], MatchNames(matcher, "ab.fab.s01e01.mkv"));
        Assert.Empty(matcher.Match("slabfabrication.mkv"));
    }

    [Fact]
    public void TwoTargets_BothReturned()
    {
        var names = MatchNames(CreateMatcher("Breaking Bad", "The Office", "Doctor Who"), "Breaking Bad and The Office crossover.mkv");
        Assert.Equal(2, names.Count);
        Assert.Contains("Breaking Bad", names);
        Assert.Contains("The Office", names);
    }

    [Fact]
    public void LongerName_ShadowsContainedShorterName()
    {
        var matcher = CreateMatcher("Doctor Who", "Doctor Who Confidential");
        Assert.Equal(["Doctor Who Confidential"], MatchNames(matcher, "doctor.who.confidential.s01e01.mkv"));
        Assert.Equal(["Doctor Who Confidential"], MatchNames(matcher, "doctorwhoconfidential.mkv"));
        Assert.Equal(["Doctor Who"], MatchNames(matcher, "doctor.who.s01e01.mkv"));
    }

    [Fact]
    public void StrongMatches_RankBeforeLooseMatches()
    {
        var candidates = CreateMatcher("Breaking Bad", "The Office").Match("theoffice with breaking.bad.mkv");
        Assert.Equal(["Breaking Bad", "The Office"], candidates.Select(c => c.Target.Name));
        Assert.Equal([MatchConfidence.Strong, MatchConfidence.Loose], candidates.Select(c => c.Confidence));
    }

    [Fact]
    public void ExtensionIsIgnored()
    {
        Assert.Empty(CreateMatcher("Mp Four").Match("clip.mpfour"));
    }

    [Fact]
    public void NoMatch_ReturnsEmpty()
    {
        Assert.Empty(CreateMatcher("Breaking Bad").Match("holiday_video.mp4"));
    }
}
