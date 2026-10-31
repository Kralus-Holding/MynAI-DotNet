using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using MynAI.Windows.Auth;

namespace MynAI.Windows;

/// <summary>
/// Sends one bounded diagnostic bundle after an explicit user opt-in.
/// The local log remains authoritative; email failure never interrupts the app.
/// </summary>
internal static class AutomaticDiagnosticEmailReporter
{
    private const string Endpoint =
        "https://mynassistant-server.onrender.com/api/v1/diagnostics/report";
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(5);
    private static readonly object Gate = new();
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(70)
    };
    private static readonly string StateDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MynAI");
    private static readonly string LastAttemptPath =
        Path.Combine(StateDirectory, "diagnostics-email-last-attempt.txt");
    private static readonly string StatusPath =
        Path.Combine(StateDirectory, "diagnostics-email-status.log");

    public static void Queue(string code, string component, string message)
    {
        try
        {
            if (!new MynAISettingsStore().Load().SendDiagnosticsAutomatically)
                return;

            var session = FirebaseDesktopSessionStore.Load();
            if (session is null)
                return;

            lock (Gate)
            {
                Directory.CreateDirectory(StateDirectory);
                var lastAttempt = ReadLastAttempt();
                var now = DateTimeOffset.UtcNow;
                if (lastAttempt is not null && now - lastAttempt.Value < MinimumInterval)
                    return;

                File.WriteAllText(
                    LastAttemptPath,
                    now.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            _ = Task.Run(() => SendAsync(session, code, component, message));
        }
        catch
        {
            // Automatic reporting is optional and must never change the original error path.
        }
    }

    private static async Task SendAsync(
        FirebaseDesktopSession session,
        string code,
        string component,
        string message)
    {
        try
        {
            var pairingClient = new DesktopPairingClient();
            var idToken = await pairingClient.GetFreshIdTokenAsync(session);
            var report = DiagnosticLogManager.BuildSupportReport();
            if (report.Length > 60_000)
                report = report[..60_000] + Environment.NewLine + "[rapor sınırı nedeniyle kesildi]";

            var appVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
                ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                ?? "unknown";
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = JsonContent.Create(
                    new
                    {
                        source = "windows",
                        appVersion,
                        errorCode = string.IsNullOrWhiteSpace(code) ? "MYN-UNKNOWN" : code[..Math.Min(code.Length, 80)],
                        message = $"{component}: {message}",
                        details = report
                    },
                    options: new JsonSerializerOptions(JsonSerializerDefaults.Web))
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                WriteStatus($"Otomatik tanı e-postası gönderilemedi; HTTP {(int)response.StatusCode}.");
                return;
            }

            WriteStatus("Otomatik tanı e-postası destek ekibine gönderildi.");
        }
        catch (Exception ex)
        {
            WriteStatus($"Otomatik tanı e-postası gönderilemedi; {ex.GetType().Name}.");
        }
    }

    private static DateTimeOffset? ReadLastAttempt()
    {
        try
        {
            if (!File.Exists(LastAttemptPath))
                return null;
            var raw = File.ReadAllText(LastAttemptPath).Trim();
            return long.TryParse(
                raw,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var milliseconds)
                ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteStatus(string message)
    {
        try
        {
            Directory.CreateDirectory(StateDirectory);
            File.AppendAllText(
                StatusPath,
                $"[{DateTimeOffset.UtcNow:O}] {message}{Environment.NewLine}");
        }
        catch
        {
            // The reporter status log is best-effort and deliberately separate from runtime errors.
        }
    }
}
