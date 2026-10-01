namespace Sortr.Core;

public static class TargetLoader
{
    /// <summary>Every direct subfolder of <paramref name="targetsFolder"/> is one target.</summary>
    public static IReadOnlyList<Target> Load(string targetsFolder)
    {
        return new DirectoryInfo(targetsFolder)
            .EnumerateDirectories()
            .Where(d => !d.Name.StartsWith('.') && (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
            .Select(d => new Target(d.Name, d.FullName))
            .Where(a => a.Tokens.Count > 0)
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
