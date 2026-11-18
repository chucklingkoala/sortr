using System.Text;

namespace Sortr.Core;

/// <summary>The start of a text file, decoded for display.</summary>
/// <param name="Text">The decoded text.</param>
/// <param name="Truncated">True when the file is longer than what was read.</param>
public sealed record PreviewTextResult(string Text, bool Truncated);

/// <summary>Reads the start of a text file for the preview window without keeping it open.</summary>
public static class PreviewText
{
    public const int DefaultMaxBytes = 256 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Reads up to <paramref name="maxBytes"/> bytes and decodes them. A byte order mark picks UTF-8 or UTF-16;
    /// otherwise the bytes are decoded as UTF-8, falling back to Latin-1 for legacy single-byte files.
    /// </summary>
    public static PreviewTextResult Read(string path, int maxBytes = DefaultMaxBytes)
    {
        // Share everything so a move started while this read is running isn't blocked.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[(int)Math.Min(maxBytes, Math.Max(stream.Length, 0))];
        int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        bool truncated = stream.Length > read;
        return new PreviewTextResult(Decode(buffer.AsSpan(0, read), truncated), truncated);
    }

    /// <summary>Decodes file bytes. <paramref name="truncated"/> tolerates a character cut in half at the end.</summary>
    public static string Decode(ReadOnlySpan<byte> bytes, bool truncated)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            return Encoding.UTF8.GetString(bytes[3..]);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
            return Encoding.Unicode.GetString(TrimOdd(bytes[2..]));
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
            return Encoding.BigEndianUnicode.GetString(TrimOdd(bytes[2..]));

        try
        {
            // flush: false keeps a multi-byte character cut off by the read limit from counting as invalid.
            var decoder = StrictUtf8.GetDecoder();
            var chars = new char[StrictUtf8.GetMaxCharCount(bytes.Length)];
            int count = decoder.GetChars(bytes, chars, flush: !truncated);
            return new string(chars, 0, count);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static ReadOnlySpan<byte> TrimOdd(ReadOnlySpan<byte> bytes) => bytes[..(bytes.Length & ~1)];
}
