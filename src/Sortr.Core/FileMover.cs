namespace Sortr.Core;

public sealed record MoveRequest(string SourcePath, string DestinationFolder);

public sealed record MoveResult(MoveRequest Request, string? FinalPath, string? Error)
{
    public bool Succeeded => Error is null;
}

public sealed class FileMover
{
    private readonly UndoLog _undoLog;

    public FileMover(UndoLog undoLog)
    {
        _undoLog = undoLog;
    }

    public Task<IReadOnlyList<MoveResult>> MoveAsync(
        IReadOnlyList<MoveRequest> requests,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<MoveResult>>(() =>
        {
            var batch = _undoLog.BeginBatch();
            var results = new List<MoveResult>(requests.Count);
            for (int i = 0; i < requests.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(MoveOne(requests[i], batch));
                progress?.Report(i + 1);
            }
            return results;
        }, cancellationToken);
    }

    private static MoveResult MoveOne(MoveRequest request, UndoBatch batch)
    {
        try
        {
            Directory.CreateDirectory(request.DestinationFolder);
            var destination = GetAvailablePath(request.DestinationFolder, Path.GetFileName(request.SourcePath));
            File.Move(request.SourcePath, destination, overwrite: false);
            batch.Append(new MoveRecord(request.SourcePath, destination));
            return new MoveResult(request, destination, null);
        }
        catch (Exception ex)
        {
            return new MoveResult(request, null, ex.Message);
        }
    }

    /// <summary>Returns folder\name.ext, or folder\name (n).ext if that already exists.</summary>
    public static string GetAvailablePath(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        if (!File.Exists(path))
            return path;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (int n = 1; ; n++)
        {
            path = Path.Combine(folder, $"{stem} ({n}){extension}");
            if (!File.Exists(path))
                return path;
        }
    }
}
