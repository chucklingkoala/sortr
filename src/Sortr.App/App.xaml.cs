using System.Windows;
using Sortr.App.Services;
using Sortr.App.ViewModels;
using Sortr.Core;

namespace Sortr.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var viewModel = new MainViewModel(
            new DialogService(),
            new SettingsStore(AppPaths.SettingsFile),
            new UndoLog(AppPaths.UndoDirectory));

        MainWindow = new MainWindow(viewModel);
        MainWindow.Show();
    }
}
