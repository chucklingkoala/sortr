using System.Text.Json;

namespace Sortr.Core;

public sealed record MoveRecord(string From, string To);

public sealed record UndoResult(int Restored, IReadOnlyList<string> Errors);

/// <summary>
/// Stores each move batch as a JSON-lines file so the most recent batch can be reverted.
/// Entries are appended as each move succeeds, so the log stays accurate after a crash.
/// </summary>
public sealed class UndoLog
{
    private readonly string _directory;

    public UndoLog(string directory)
    {
        _directory = directory;
    }

    public UndoBatch BeginBatch()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, $"batch-{DateTime.Now:yyyyMMdd-HHmmss-fff}.jsonl");
        return new UndoBatch(path);
    }

    public string? LatestBatchPath =>
        Directory.Exists(_directory)
            ? Directory.EnumerateFiles(_directory, "batch-*.jsonl").OrderByDescending(p => p, StringComparer.Ordinal).FirstOrDefault()
            : null;

    public IReadOnlyList<MoveRecord> ReadLatest()
    {
        var path = LatestBatchPath;
        return path is null ? [] : ReadBatch(path);
    }

    /// <summary>Moves every file in the latest batch back where it came from.</summary>
    public UndoResult UndoLatest()
    {
        var path = LatestBatchPath;
        if (path is null)
            return new UndoResult(0, []);

        var records = ReadBatch(path);
        var failed = new List<MoveRecord>();
        var errors = new List<string>();
        int restored = 0;

        foreach (var record in Enumerable.Reverse(records))
        {
            try
            {
                if (!File.Exists(record.To))
                    throw new IOException($"'{record.To}' no longer exists.");
                if (File.Exists(record.From))
                    throw new IOException($"'{record.From}' already exists.");
                Directory.CreateDirectory(Path.GetDirectoryName(record.From)!);
                File.Move(record.To, record.From);
                restored++;
            }
            catch (Exception ex)
            {
                failed.Add(record);
                errors.Add($"{Path.GetFileName(record.To)}: {ex.Message}");
            }
        }

        if (failed.Count == 0)
        {
            File.Delete(path);
        }
        else
        {
            // Keep only what could not be reverted so the user can retry after fixing it.
            failed.Reverse();
            File.WriteAllLines(path, failed.Select(r => JsonSerializer.Serialize(r)));
        }

        return new UndoResult(restored, errors);
    }

    private static List<MoveRecord> ReadBatch(string path) =>
        File.ReadLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonSerializer.Deserialize<MoveRecord>(l)!)
            .ToList();
}

public sealed class UndoBatch
{
    public string FilePath { get; }

    internal UndoBatch(string filePath)
    {
        FilePath = filePath;
    }

    public void Append(MoveRecord record) =>
        File.AppendAllText(FilePath, JsonSerializer.Serialize(record) + Environment.NewLine);
}
