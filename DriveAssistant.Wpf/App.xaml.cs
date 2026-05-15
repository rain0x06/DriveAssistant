using System.Configuration;
using System.Data;
using System.Windows;

namespace FATXTools.Wpf;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var startupArgs = e.Args ?? Array.Empty<string>();
        var runInsetSmoke = SettingsInsetSmokeRunner.IsRequested(startupArgs);

        WpfTheme.Apply(AppSettings.Load().Theme);
        base.OnStartup(e);

        if (runInsetSmoke)
        {
            SettingsInsetSmokeRunner.Run(this, startupArgs);
            return;
        }

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }
}
