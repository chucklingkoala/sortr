namespace Sortr.Core;

/// <summary>How the preview window should show a file.</summary>
public enum PreviewKind
{
    Image,
    Video,
    Audio,
    Text,
    Other,
}

/// <summary>Decides how a file can be previewed, from its extension and, for unknown types, its first bytes.</summary>
public static class PreviewClassifier
{
    /// <summary>How many leading bytes <see cref="Classify(string)"/> reads to sniff unknown types.</summary>
    public const int SniffLength = 8 * 1024;

    private static readonly HashSet<string> ImageExtensions = Set(
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".bmp", ".dib", ".tif", ".tiff", ".ico",
        ".webp", ".heic", ".heif", ".jxr", ".wdp");

    private static readonly HashSet<string> VideoExtensions = Set(
        ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".mpg", ".mpeg", ".3gp", ".3g2", ".m2ts", ".asf");

    private static readonly HashSet<string> AudioExtensions = Set(
        ".mp3", ".wav", ".wma", ".m4a", ".aac", ".flac", ".ogg", ".oga", ".opus", ".aiff", ".aif");

    private static readonly HashSet<string> TextExtensions = Set(
        ".txt", ".text", ".md", ".markdown", ".log", ".csv", ".tsv", ".json", ".jsonl", ".xml", ".yaml", ".yml",
        ".toml", ".ini", ".cfg", ".conf", ".config", ".env", ".properties", ".html", ".htm", ".css", ".scss",
        ".js", ".mjs", ".tsx", ".jsx", ".cs", ".csproj", ".sln", ".slnx", ".xaml", ".py", ".rb", ".go",
        ".rs", ".java", ".kt", ".c", ".h", ".cpp", ".hpp", ".ps1", ".psm1", ".bat", ".cmd", ".sh", ".sql",
        ".srt", ".vtt", ".nfo", ".rtf", ".tex", ".gitignore", ".gitattributes", ".editorconfig");

    // Binary formats that may contain no NUL bytes in their first few KB but are never readable as text.
    private static readonly HashSet<string> KnownBinaryExtensions = Set(
        ".pdf", ".zip", ".7z", ".rar", ".gz", ".tar", ".exe", ".dll", ".msi", ".doc", ".docx", ".xls", ".xlsx",
        ".ppt", ".pptx", ".odt", ".ods", ".odp", ".psd", ".ai", ".eps", ".iso", ".bin", ".dat");

    private static HashSet<string> Set(params string[] items) => new(items, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Classifies by extension. Unknown extensions are sniffed: a file whose start contains no NUL bytes is treated
    /// as text. Files that can't be read are <see cref="PreviewKind.Other"/>.
    /// </summary>
    public static PreviewKind Classify(string path)
    {
        var byExtension = ClassifyByExtension(path);
        if (byExtension is not null)
            return byExtension.Value;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[SniffLength];
            int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return read > 0 && LooksLikeText(buffer.AsSpan(0, read)) ? PreviewKind.Text : PreviewKind.Other;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PreviewKind.Other;
        }
    }

    /// <summary>The kind implied by the extension alone, or null when the extension is unknown.</summary>
    public static PreviewKind? ClassifyByExtension(string path)
    {
        // ".ts" is deliberately absent: it is both TypeScript and MPEG transport stream, so sniffing decides.
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext))
            return null;
        if (ImageExtensions.Contains(ext)) return PreviewKind.Image;
        if (VideoExtensions.Contains(ext)) return PreviewKind.Video;
        if (AudioExtensions.Contains(ext)) return PreviewKind.Audio;
        if (TextExtensions.Contains(ext)) return PreviewKind.Text;
        if (KnownBinaryExtensions.Contains(ext)) return PreviewKind.Other;
        return null;
    }

    /// <summary>
    /// True when the bytes look like text: no NUL bytes, ignoring a UTF-16 byte order mark (UTF-16 text is full of
    /// NULs, so it is accepted whenever it starts with a BOM).
    /// </summary>
    public static bool LooksLikeText(ReadOnlySpan<byte> start)
    {
        if (start.Length >= 2 && ((start[0] == 0xFF && start[1] == 0xFE) || (start[0] == 0xFE && start[1] == 0xFF)))
            return true;
        return !start.Contains((byte)0);
    }
}
