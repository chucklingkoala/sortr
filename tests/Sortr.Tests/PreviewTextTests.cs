using System.Text;
using Sortr.Core;

namespace Sortr.Tests;

public class PreviewTextTests
{
    [Fact]
    public void Utf8WithoutBom_IsDecoded()
    {
        Assert.Equal("Crème brûlée", PreviewText.Decode(Encoding.UTF8.GetBytes("Crème brûlée"), truncated: false));
    }

    [Fact]
    public void Utf8Bom_IsStripped()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("héllo")).ToArray();
        Assert.Equal("héllo", PreviewText.Decode(bytes, truncated: false));
    }

    [Fact]
    public void Utf16LittleEndianBom_IsDecoded()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("héllo")).ToArray();
        Assert.Equal("héllo", PreviewText.Decode(bytes, truncated: false));
    }

    [Fact]
    public void Utf16BigEndianBom_IsDecoded()
    {
        var bytes = Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes("héllo")).ToArray();
        Assert.Equal("héllo", PreviewText.Decode(bytes, truncated: false));
    }

    [Fact]
    public void InvalidUtf8_FallsBackToLatin1()
    {
        // "café" saved as Windows-1252 / Latin-1: 0xE9 alone is not valid UTF-8.
        byte[] bytes = [0x63, 0x61, 0x66, 0xE9];
        Assert.Equal("café", PreviewText.Decode(bytes, truncated: false));
    }

    [Fact]
    public void CharacterCutByTheLimit_StillDecodesAsUtf8()
    {
        // "ab" followed by the first byte of the two-byte "é" (0xC3 0xA9).
        byte[] bytes = [0x61, 0x62, 0xC3];
        Assert.Equal("ab", PreviewText.Decode(bytes, truncated: true));
    }

    [Fact]
    public void Read_ReportsTruncation()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, new string('x', 100));
            var cut = PreviewText.Read(path, maxBytes: 10);
            Assert.Equal(new string('x', 10), cut.Text);
            Assert.True(cut.Truncated);

            var whole = PreviewText.Read(path, maxBytes: 1000);
            Assert.Equal(100, whole.Text.Length);
            Assert.False(whole.Truncated);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
