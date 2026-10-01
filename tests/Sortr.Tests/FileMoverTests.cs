using Sortr.Core;

namespace Sortr.Tests;

public sealed class FileMoverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sortr-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly string _target;
    private readonly UndoLog _undoLog;

    public FileMoverTests()
    {
        _source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        _target = Directory.CreateDirectory(Path.Combine(_root, "targets", "Breaking Bad")).FullName;
        _undoLog = new UndoLog(Path.Combine(_root, "undo"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string CreateSourceFile(string name, string content = "x")
    {
        var path = Path.Combine(_source, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task MovesFilesAndRecordsUndo()
    {
        var a = CreateSourceFile("breaking.bad.s01e01.mkv");
        var b = CreateSourceFile("breaking.bad.s01e02.mkv");
        var progress = new List<int>();

        var results = await new FileMover(_undoLog).MoveAsync(
            [new MoveRequest(a, _target), new MoveRequest(b, _target)],
            new SyncProgress(progress.Add));

        Assert.All(results, r => Assert.True(r.Succeeded));
        Assert.False(File.Exists(a));
        Assert.True(File.Exists(Path.Combine(_target, "breaking.bad.s01e01.mkv")));
        Assert.Equal([1, 2], progress);
        Assert.Equal(2, _undoLog.ReadLatest().Count);
    }

    [Fact]
    public async Task Collision_RenamesInsteadOfOverwriting()
    {
        File.WriteAllText(Path.Combine(_target, "clip.mp4"), "existing");
        File.WriteAllText(Path.Combine(_target, "clip (1).mp4"), "existing");
        var src = CreateSourceFile("clip.mp4", "new");

        var result = Assert.Single(await new FileMover(_undoLog).MoveAsync([new MoveRequest(src, _target)]));

        Assert.Equal(Path.Combine(_target, "clip (2).mp4"), result.FinalPath);
        Assert.Equal("existing", File.ReadAllText(Path.Combine(_target, "clip.mp4")));
        Assert.Equal("new", File.ReadAllText(result.FinalPath!));
    }

    [Fact]
    public async Task MissingSource_ReportsErrorAndContinues()
    {
        var good = CreateSourceFile("good.mp4");
        var missing = Path.Combine(_source, "missing.mp4");

        var results = await new FileMover(_undoLog).MoveAsync(
            [new MoveRequest(missing, _target), new MoveRequest(good, _target)]);

        Assert.False(results[0].Succeeded);
        Assert.True(results[1].Succeeded);
        Assert.Single(_undoLog.ReadLatest());
    }

    [Fact]
    public async Task UndoLatest_RestoresFilesAndDeletesBatch()
    {
        var a = CreateSourceFile("a.mp4");
        var b = CreateSourceFile("b.mp4");
        await new FileMover(_undoLog).MoveAsync([new MoveRequest(a, _target), new MoveRequest(b, _target)]);

        var result = _undoLog.UndoLatest();

        Assert.Equal(2, result.Restored);
        Assert.Empty(result.Errors);
        Assert.True(File.Exists(a));
        Assert.True(File.Exists(b));
        Assert.Null(_undoLog.LatestBatchPath);
    }

    [Fact]
    public async Task UndoLatest_KeepsEntriesThatCouldNotBeRestored()
    {
        var a = CreateSourceFile("a.mp4");
        var b = CreateSourceFile("b.mp4");
        await new FileMover(_undoLog).MoveAsync([new MoveRequest(a, _target), new MoveRequest(b, _target)]);
        File.WriteAllText(b, "someone put a new file here");

        var result = _undoLog.UndoLatest();

        Assert.Equal(1, result.Restored);
        Assert.Single(result.Errors);
        var remaining = Assert.Single(_undoLog.ReadLatest());
        Assert.Equal(b, remaining.From);
    }

    [Fact]
    public void UndoLatest_WithNoBatches_DoesNothing()
    {
        var result = _undoLog.UndoLatest();
        Assert.Equal(0, result.Restored);
        Assert.Empty(result.Errors);
    }

    /// <summary>Progress&lt;T&gt; posts asynchronously; this reports inline so assertions are deterministic.</summary>
    private sealed class SyncProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
