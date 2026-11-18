using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Sortr.App.Services;

/// <summary>Hands files to Windows: open with the default app, or reveal in Explorer.</summary>
public static class ShellActions
{
    public static void Open(string path, Window? owner = null)
    {
        if (!File.Exists(path))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            if (owner is not null)
                MessageBox.Show(owner, ex.Message, "Could not open file", MessageBoxButton.OK, MessageBoxImage.Warning);
            else
                MessageBox.Show(ex.Message, "Could not open file", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public static void ShowInExplorer(string path)
    {
        if (File.Exists(path))
            Process.Start("explorer.exe", $"/select,\"{path}\"");
    }
}
