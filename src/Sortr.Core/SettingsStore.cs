using System.Text.Json;

namespace Sortr.Core;

public sealed record AppSettings(string? SourceFolder = null, string? TargetsFolder = null);

public static class AppPaths
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sortr");

    public static string UndoDirectory => Path.Combine(DataDirectory, "undo");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
}

public sealed class SettingsStore
{
    private readonly string _path;

    public SettingsStore(string path)
    {
        _path = path;
    }

    public AppSettings Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception)
        {
            // A corrupt settings file shouldn't stop the app from starting.
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }
}
