namespace Sortr.Core;

public static class FileScanner
{
    /// <summary>Top-level files of <paramref name="sourceFolder"/>, skipping hidden and system files.</summary>
    public static IReadOnlyList<string> Scan(string sourceFolder)
    {
        return new DirectoryInfo(sourceFolder)
            .EnumerateFiles()
            .Where(f => (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
            .Select(f => f.FullName)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
