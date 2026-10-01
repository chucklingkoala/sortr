namespace Sortr.Core;

public sealed class Target
{
    public string Name { get; }
    public string FolderPath { get; }
    public IReadOnlyList<string> Tokens { get; }
    public string CompactKey { get; }
    public string ReversedCompactKey { get; }

    public Target(string name, string folderPath)
    {
        Name = name;
        FolderPath = folderPath;
        Tokens = NameNormalizer.Tokenize(name);
        CompactKey = string.Concat(Tokens);
        ReversedCompactKey = string.Concat(Tokens.Reverse());
    }

    public override string ToString() => Name;
}
