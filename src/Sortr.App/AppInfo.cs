using System.Reflection;

namespace Sortr.App;

public static class AppInfo
{
    /// <summary>
    /// The app version, e.g. "1.1.0". Release builds take it from the git tag (see release.yml);
    /// the "+commit" suffix the SDK appends to the informational version is dropped.
    /// </summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
            return informational.Split('+')[0];
        return assembly.GetName().Version?.ToString(3) ?? "";
    }
}
