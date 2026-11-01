using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;
using System.Threading;
using System.Windows;

namespace MynAI.Windows;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    private const string SingleInstanceName = @"Local\MynAssistant.UI.SingleInstance";
    private const string ActivationEventName = @"Local\MynAssistant.Ui.Activate";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _singleInstanceMutex = new Mutex(
                initiallyOwned: true,
                name: SingleInstanceName,
                createdNew: out var createdNew);

            if (!createdNew)
            {
                SignalExistingWindow();
                Shutdown(0);
                return;
            }

            // Start the IPC server before constructing the UI. The window starts its
            // scheduled-task refresh timer immediately; showing it first caused a startup
            // race where its first pipe requests timed out before MynAgent was listening.
            try
            {
                AgentStartupManager.SetEnabled(true);
                AgentStartupManager.StartAgent();
            }
            catch (Exception agentError)
            {
                DiagnosticLogManager.WriteError(
                    "MYN-AGENT-001",
                    "Agent",
                    "MynAssistant UI opened, but its background Agent could not be started.",
                    agentError);
            }

            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.ToString(),
                "MynAssistant başlatılamadı",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static void SignalExistingWindow()
    {
        try
        {
            using var activationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
            activationEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The first UI instance may still be creating its activation event.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Existing MynAssistant window activation failed: {ex.Message}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Another instance did not acquire ownership; no action is needed.
        }
        finally
        {
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
        }

        base.OnExit(e);
    }
}
