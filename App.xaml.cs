using System;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace SeansSteamIdler;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            System.Windows.MessageBox.Show((args.ExceptionObject as Exception)?.ToString() ?? "Unknown fatal error",
                "Sean's Steam Idler fatal error");

        DispatcherUnhandledException += (s, args) =>
        {
            System.Windows.MessageBox.Show(args.Exception.ToString(), "Sean's Steam Idler startup error");
            args.Handled = true;
        };

        base.OnStartup(e);

        try
        {
            ApplicationThemeManager.Apply(
                ApplicationTheme.Dark,
                WindowBackdropType.Mica,
                true);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.ToString(), "Sean's Steam Idler theme error");
        }
    }
}
