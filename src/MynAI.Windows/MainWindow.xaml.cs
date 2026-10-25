using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MynAI.Core.Commands;
using MynAI.Core.Models;

namespace MynAI.Windows;

public partial class MainWindow : Window
{
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Drawing.Icon? _trayIconImage;
    private System.IO.Stream? _trayIconStream;
    private bool _closingApplication;

    private SettingsWindow? _settingsWindow;
    private const double NormalWidth = 820;
    private const double NormalHeight = 448;
    private const double ExpandThreshold = 48;

    private readonly AgentClient _agentClient = new();
    private readonly ObservableCollection<string> _recentCommands = [];
    private readonly ObservableCollection<ScheduledTask> _scheduledTasks = [];
    private readonly DispatcherTimer _refreshTimer;

    private bool _expanded;
    private bool _dragging;
    private Point _dragStart;

    public MainWindow()
    {
        InitializeComponent();

        InitializeTrayIcon();

        Width = NormalWidth;
        Height = NormalHeight;

        RecentCommandsList.ItemsSource = _recentCommands;
        ScheduledTasksList.ItemsSource = _scheduledTasks;

        LoadLogo();

        Loaded += MainWindow_Loaded;

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };

        _refreshTimer.Tick += async (_, _) =>
        {
            await RefreshTasksAsync();
        };

        _refreshTimer.Start();
    }

    private void LoadLogo()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Resources",
            "mynassistant-logo.png");

        if (!File.Exists(path))
        {
            System.Diagnostics.Debug.WriteLine(
                $"MynAssistant logo file was not found: {path}");
            return;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();

            LogoImage.Source = image;

            // Load the shared PNG only after XAML initialization. The .ico is
            // reserved for executable/tray branding and is not decoded by WPF here.
            Icon = image;
        }
        catch (Exception ex)
        {
            // Branding is optional: a bad/missing image must never prevent
            // the assistant window from opening.
            System.Diagnostics.Debug.WriteLine(
                $"MynAssistant window logo could not be loaded: {ex}");
        }
    }

    private async void MainWindow_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        DockNormal();
        StartConfiguredDesktopBridge();
        await RefreshTasksAsync();
    }

    private void StartConfiguredDesktopBridge()
    {
        // Remote PC control is opt-in via the installer task or Settings.
        if (!DesktopBridgeStartupManager.IsEnabled())
            return;

        if (DesktopBridgeStartupManager.TryGetExecutablePath() is null ||
            !File.Exists(DesktopBridgeStartupManager.GetOAuthConfigPath()))
            return;

        try
        {
            // Normalize legacy Run-key entries to hidden launch and start this session.
            DesktopBridgeStartupManager.SetEnabled(true);
            DesktopBridgeStartupManager.StartAgent();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"MynAssistant remote PC bridge could not start: {ex}");
        }
    }

    private void DockNormal()
    {
        _expanded = false;

        Width = NormalWidth;
        Height = NormalHeight;

        var workArea = SystemParameters.WorkArea;

        Left =
            workArea.Left +
            (workArea.Width - Width) / 2;

        Top =
            workArea.Bottom - Height;
    }

    private void ExpandWindow()
    {
        _expanded = true;

        Left = 0;
        Top = 0;

        Width =
            SystemParameters.PrimaryScreenWidth;

        Height =
            SystemParameters.PrimaryScreenHeight;
    }

    private void Handle_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            if (_expanded)
                DockNormal();
            else
                ExpandWindow();

            e.Handled = true;
            return;
        }

        _dragging = true;
        _dragStart = e.GetPosition(this);

        ((UIElement)sender).CaptureMouse();
    }

    private void Handle_MouseMove(
        object sender,
        MouseEventArgs e)
    {
        if (!_dragging)
            return;

        var current = e.GetPosition(this);

        var deltaY =
            current.Y - _dragStart.Y;

        if (!_expanded &&
            deltaY <= -ExpandThreshold)
        {
            _dragging = false;

            ((UIElement)sender)
                .ReleaseMouseCapture();

            ExpandWindow();

            return;
        }

        if (_expanded &&
            deltaY >= ExpandThreshold)
        {
            _dragging = false;

            ((UIElement)sender)
                .ReleaseMouseCapture();

            DockNormal();
        }
    }

    private void Handle_MouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        _dragging = false;

        ((UIElement)sender)
            .ReleaseMouseCapture();
    }

    private void InitializeTrayIcon()
    {
        try
        {
            var resource =
                System.Windows.Application.GetResourceStream(
                    new Uri(
                        "Resources/myn.ico",
                        UriKind.Relative));

            if (resource is null)
                return;

            _trayIconStream =
                new System.IO.MemoryStream();

            resource.Stream.CopyTo(
                _trayIconStream);

            _trayIconStream.Position = 0;

            _trayIconImage =
                new System.Drawing.Icon(
                    _trayIconStream);

            _trayIcon =
                new System.Windows.Forms.NotifyIcon
                {
                    Icon = _trayIconImage,
                    Visible = true,
                    Text = "MynAssistant"
                };

            var menu =
                new System.Windows.Forms.ContextMenuStrip();

            var openItem =
                new System.Windows.Forms.ToolStripMenuItem(
                    "MynAssistant'ı aç");

            openItem.Click +=
                (_, _) => RestoreFromTray();

            menu.Items.Add(openItem);

            menu.Items.Add(
                new System.Windows.Forms.ToolStripSeparator());

            var exitItem =
                new System.Windows.Forms.ToolStripMenuItem(
                    "Çıkış");

            exitItem.Click +=
                (_, _) => ExitFromTray();

            menu.Items.Add(exitItem);

            _trayIcon.ContextMenuStrip = menu;

            _trayIcon.DoubleClick +=
                (_, _) => RestoreFromTray();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Tray başlatılamadı: {ex.Message}");
        }
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();

        WindowState =
            WindowState.Normal;

        Activate();

        Topmost = true;
        Topmost = false;
    }

    private void ExitFromTray()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;

        _trayIconImage?.Dispose();
        _trayIconImage = null;

        _trayIconStream?.Dispose();
        _trayIconStream = null;

        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _trayIcon?.Dispose();
        _trayIcon = null;

        _trayIconImage?.Dispose();
        _trayIconImage = null;

        _trayIconStream?.Dispose();
        _trayIconStream = null;

        base.OnClosed(e);
    }
    private void CollapseButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        ShowInTaskbar = false;
        Hide();
    }

    private void ExpandButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_expanded)
            DockNormal();
        else
            ExpandWindow();
    }

    private void CloseButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        ExitFromTray();
    }

    private async void SendButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        await SendCommandAsync();
    }
    private async void CommandInput_KeyDown(
        object sender,
        KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;

        await SendCommandAsync();
    }

    private async Task SendCommandAsync()
    {
        var text = CommandInput.Text.Trim();

        if (string.IsNullOrWhiteSpace(text))
            return;

        AddRecentCommand(text);

        if (!TryParseCommand(
                text,
                out var action,
                out var executeAt,
                out var description))
        {
            SetStatus(
                "Bu komut henüz desteklenmiyor.");

            CommandInput.Clear();
            return;
        }

        try
        {
            await _agentClient.CreateScheduledTaskAsync(
                action,
                executeAt,
                description);

            CommandInput.Clear();

            if (executeAt <= DateTimeOffset.Now.AddSeconds(2))
            {
                SetStatus("İşlem uygulanıyor.");
            }
            else
            {
                SetStatus(
                    $"Planlandı: {executeAt:HH:mm}");
            }

            await RefreshTasksAsync();
        }
        catch (Exception ex)
        {
            SetStatus(
                $"Agent bağlantı hatası: {ex.Message}");
        }
    }

    private static bool TryParseCommand(
        string text,
        out ScheduledAction action,
        out DateTimeOffset executeAt,
        out string description)
    {
        action = default;
        executeAt = default;
        description = text;

        var normalized = text.Trim();

        if (normalized.Contains(
                "yeniden başlat",
                StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(
                "yeniden baslat",
                StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(
                "restart",
                StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(
                "reboot",
                StringComparison.OrdinalIgnoreCase))
        {
            action = ScheduledAction.Restart;
        }
        else if (normalized.Contains(
                     "kapat",
                     StringComparison.OrdinalIgnoreCase) ||
                 normalized.Contains(
                     "shutdown",
                     StringComparison.OrdinalIgnoreCase))
        {
            action = ScheduledAction.Shutdown;
        }
        else if (normalized.Contains(
                     "uyu",
                     StringComparison.OrdinalIgnoreCase) ||
                 normalized.Contains(
                     "uyku",
                     StringComparison.OrdinalIgnoreCase) ||
                 normalized.Contains(
                     "sleep",
                     StringComparison.OrdinalIgnoreCase))
        {
            action = ScheduledAction.Sleep;
        }
        else if (normalized.Contains(
                     "kilitle",
                     StringComparison.OrdinalIgnoreCase) ||
                 normalized.Contains(
                     "kilit",
                     StringComparison.OrdinalIgnoreCase) ||
                 normalized.Contains(
                     "lock",
                     StringComparison.OrdinalIgnoreCase))
        {
            action = ScheduledAction.Lock;
        }
        else
        {
            return false;
        }

        var match = Regex.Match(
            normalized,
            @"(?<hour>\d{1,2})[.:](?<minute>\d{2})");

        if (!match.Success)
        {
            // Saat verilmediyse hemen uygula.
            executeAt = DateTimeOffset.Now;
            return true;
        }

        if (!int.TryParse(
                match.Groups["hour"].Value,
                out var hour))
        {
            return false;
        }

        if (!int.TryParse(
                match.Groups["minute"].Value,
                out var minute))
        {
            return false;
        }

        if (hour < 0 || hour > 23 || minute < 0 || minute > 59)
            return false;

        var now = DateTimeOffset.Now;

        var candidate = new DateTimeOffset(
            now.Year,
            now.Month,
            now.Day,
            hour,
            minute,
            0,
            now.Offset);

        if (candidate <= now)
            candidate = candidate.AddDays(1);

        executeAt = candidate;

        return true;
    }
    private void AddRecentCommand(
        string command)
    {
        _recentCommands.Insert(
            0,
            command);

        while (_recentCommands.Count > 10)
        {
            _recentCommands.RemoveAt(
                _recentCommands.Count - 1);
        }

        EmptyRecentText.Visibility =
            _recentCommands.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private async Task RefreshTasksAsync()
    {
        try
        {
            var tasks =
                await _agentClient
                    .GetScheduledTasksAsync();

            _scheduledTasks.Clear();

            foreach (var task in tasks
                .Where(x =>
                    x.Status ==
                    ScheduledTaskStatus.Pending)
                .OrderBy(x => x.ExecuteAt))
            {
                _scheduledTasks.Add(task);
            }

            PlannedCountText.Text =
                _scheduledTasks.Count.ToString();

            AgentStatusText.Text =
                "Agent: bağlı";

            AgentStatusText.Foreground =
                new SolidColorBrush(
                    Color.FromRgb(
                        126,
                        212,
                        145));

            if (_scheduledTasks.Count == 0)
            {
                SetStatus("Beklemede");
            }
        }
        catch
        {
            AgentStatusText.Text =
                "Agent: bağlantı yok";

            AgentStatusText.Foreground =
                new SolidColorBrush(
                    Color.FromRgb(
                        220,
                        125,
                        125));

            SetStatus(
                "Agent bağlantısı bekleniyor");
        }
    }

    private async void CancelTask_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not
            System.Windows.Controls.Button button)
            return;

        if (button.Tag is not Guid taskId)
            return;

        try
        {
            await _agentClient
                .CancelScheduledTaskAsync(taskId);

            SetStatus(
                "Görev iptal edildi.");

            await RefreshTasksAsync();
        }
        catch (Exception ex)
        {
            SetStatus(
                $"İptal başarısız: {ex.Message}");
        }
    }

    private void SettingsButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_settingsWindow is not null)
        {
            if (_settingsWindow.IsVisible)
            {
                _settingsWindow.Activate();
                return;
            }

            _settingsWindow = null;
        }

        _settingsWindow =
            new SettingsWindow
            {
                Owner = this
            };

        _settingsWindow.Closed +=
            (_, _) =>
            {
                _settingsWindow = null;
            };

        _settingsWindow.Show();
        _settingsWindow.Activate();
    }
    private void SetStatus(
        string text)
    {
        StatusText.Text =
            text == "Beklemede"
                ? "=Beklemede"
                : text;
    }
}


internal sealed class AgentClient
{
    private const string PipeName = "MynAI.Agent";

    private static readonly JsonSerializerOptions
        JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

    public async Task<List<ScheduledTask>>
        GetScheduledTasksAsync()
    {
        var request = new AgentRequest
        {
            Type =
                AgentRequestType.GetScheduledTasks
        };

        using var document =
            await SendAsync(request);

        var root =
            document.RootElement;

        if (!root
            .GetProperty("success")
            .GetBoolean())
        {
            throw new InvalidOperationException(
                root.GetProperty("error")
                    .GetString()
                ?? "Agent isteği başarısız.");
        }

        return root
            .GetProperty("tasks")
            .Deserialize<List<ScheduledTask>>(
                JsonOptions)
            ?? [];
    }

    public async Task<ScheduledTask>
        CreateScheduledTaskAsync(
            ScheduledAction action,
            DateTimeOffset executeAt,
            string description)
    {
        var request =
            new AgentRequest
            {
                Type =
                    AgentRequestType.CreateScheduledTask,

                Action =
                    action.ToString(),

                ExecuteAt =
                    executeAt,

                Description =
                    description
            };

        using var document =
            await SendAsync(request);

        var root =
            document.RootElement;

        if (!root
            .GetProperty("success")
            .GetBoolean())
        {
            throw new InvalidOperationException(
                root.GetProperty("error")
                    .GetString()
                ?? "Agent isteği başarısız.");
        }

        return root
            .GetProperty("task")
            .Deserialize<ScheduledTask>(
                JsonOptions)
            ?? throw new InvalidOperationException(
                "Agent görev döndürmedi.");
    }

    public async Task
        CancelScheduledTaskAsync(
            Guid taskId)
    {
        var request =
            new AgentRequest
            {
                Type =
                    AgentRequestType.CancelScheduledTask,

                TaskId =
                    taskId.ToString()
            };

        using var document =
            await SendAsync(request);

        var root =
            document.RootElement;

        if (!root
            .GetProperty("success")
            .GetBoolean())
        {
            throw new InvalidOperationException(
                root.GetProperty("error")
                    .GetString()
                ?? "Görev iptal edilemedi.");
        }
    }

    private static async Task<JsonDocument>
        SendAsync(AgentRequest request)
    {
        await using var pipe =
            new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

        await pipe.ConnectAsync(3000);

        using var reader =
            new StreamReader(
                pipe,
                Encoding.UTF8,
                leaveOpen: true);

        await using var writer =
            new StreamWriter(
                pipe,
                new UTF8Encoding(false),
                leaveOpen: true)
            {
                AutoFlush = true
            };

        await writer.WriteLineAsync(
            JsonSerializer.Serialize(request));

        var response =
            await reader.ReadLineAsync();

        if (string.IsNullOrWhiteSpace(response))
        {
            throw new InvalidOperationException(
                "Agent boş cevap verdi.");
        }

        return JsonDocument.Parse(response);
    }
}








