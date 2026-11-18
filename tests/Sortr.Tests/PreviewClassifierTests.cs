using System.Text;
using Sortr.Core;

namespace Sortr.Tests;

public class PreviewClassifierTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sortr-preview-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteFile(string name, byte[] content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Theory]
    [InlineData("photo.jpg", PreviewKind.Image)]
    [InlineData("PHOTO.JPG", PreviewKind.Image)]
    [InlineData("scan.TiFf", PreviewKind.Image)]
    [InlineData("clip.mp4", PreviewKind.Video)]
    [InlineData("clip.MKV", PreviewKind.Video)]
    [InlineData("song.mp3", PreviewKind.Audio)]
    [InlineData("song.flac", PreviewKind.Audio)]
    [InlineData("notes.txt", PreviewKind.Text)]
    [InlineData("data.json", PreviewKind.Text)]
    [InlineData(".gitignore", PreviewKind.Text)]
    [InlineData("report.pdf", PreviewKind.Other)]
    [InlineData("archive.zip", PreviewKind.Other)]
    public void KnownExtensions_AreClassifiedWithoutReadingTheFile(string name, PreviewKind expected)
    {
        // The file doesn't exist, so a correct answer proves the extension alone decided it.
        Assert.Equal(expected, PreviewClassifier.Classify(Path.Combine(_dir, name)));
    }

    [Theory]
    [InlineData("README")]
    [InlineData("thing.xyz")]
    public void UnknownExtension_WithText_IsText(string name)
    {
        var path = WriteFile(name, Encoding.UTF8.GetBytes("Hello, wörld!\r\nSecond line."));
        Assert.Equal(PreviewKind.Text, PreviewClassifier.Classify(path));
    }

    [Fact]
    public void UnknownExtension_WithNulBytes_IsOther()
    {
        var path = WriteFile("blob.xyz", [0x4D, 0x5A, 0x00, 0x01, 0x02, 0x00]);
        Assert.Equal(PreviewKind.Other, PreviewClassifier.Classify(path));
    }

    [Fact]
    public void UnknownExtension_EmptyFile_IsOther()
    {
        var path = WriteFile("empty.xyz", []);
        Assert.Equal(PreviewKind.Other, PreviewClassifier.Classify(path));
    }

    [Fact]
    public void MissingFile_WithUnknownExtension_IsOther()
    {
        Assert.Equal(PreviewKind.Other, PreviewClassifier.Classify(Path.Combine(_dir, "missing.xyz")));
    }

    [Fact]
    public void Utf16WithBom_LooksLikeText()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Hello")).ToArray();
        Assert.True(PreviewClassifier.LooksLikeText(bytes));
    }

    [Fact]
    public void Utf8WithBom_LooksLikeText()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("Hello")).ToArray();
        Assert.True(PreviewClassifier.LooksLikeText(bytes));
    }

    [Fact]
    public void BytesWithNul_DoNotLookLikeText()
    {
        Assert.False(PreviewClassifier.LooksLikeText([0x41, 0x00, 0x42]));
    }
}
