using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;
using System.Windows;

namespace MynAI.Windows;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(
        StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            var mainWindow =
                new MainWindow();

            MainWindow =
                mainWindow;

            GlobalHotkeyService.Attach(
                mainWindow);

            mainWindow.Show();

            // Local PC tasks/IPC do not depend on a phone account session.
            AgentStartupManager.EnsureAutomaticStartup();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.ToString(),
                "MynAI başlatılamadı",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(1);
        }
    }
}


