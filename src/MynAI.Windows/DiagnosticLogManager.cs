using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MynAI.Windows;

public static class DiagnosticLogManager
{
    private const long MaxLogBytes = 2 * 1024 * 1024;
    private const int MaxLinesPerSource = 100;
    private const int BackupCount = 3;
    private static readonly object Gate = new();

    public static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MynAssistant", "Logs");

    public static string StructuredLogPath => Path.Combine(LogDirectory, "diagnostics.jsonl");

    public static void WriteError(
        string code,
        string component,
        string message,
        Exception exception,
        string? correlationId = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfNeeded(StructuredLogPath);
                var record = new
                {
                    code = string.IsNullOrWhiteSpace(code) ? "MYN-UI-999" : code,
                    category = "runtime",
                    severity = "error",
                    component = string.IsNullOrWhiteSpace(component) ? "Unknown" : component,
                    message = Redact(message),
                    technicalDetails = Redact($"{exception.GetType().Name}: {exception.Message}"),
                    timestampUtc = DateTimeOffset.UtcNow,
                    correlationId = string.IsNullOrWhiteSpace(correlationId)
                        ? Guid.NewGuid().ToString("N")
                        : correlationId
                };
                File.AppendAllText(
                    StructuredLogPath,
                    JsonSerializer.Serialize(record) + Environment.NewLine,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch
        {
            // Diagnostics must never crash the UI or hide the original failure.
        }

        // The reporter is inert unless the user explicitly opted in and a
        // protected MynAssistant account session is available.
        AutomaticDiagnosticEmailReporter.Queue(code, component, message);
    }

    public static IReadOnlyList<string> ReadRecentLines()
    {
        var entries = new List<(DateTime Timestamp, string Line)>();
        AddLines(StructuredLogPath, entries);
        AddLines(Path.Combine(LogDirectory, "agent-startup.log"), entries);
        AddLines(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MynAssistant", "DesktopAgent", "bridge.log"), entries);

        return entries
            .OrderByDescending(entry => entry.Timestamp)
            .Take(MaxLinesPerSource * 3)
            .Select(entry => entry.Line)
            .ToArray();
    }

    public static string BuildSupportReport()
    {
        var bridgeStatus = DesktopBridgeStartupManager.ReadRuntimeStatus();
        var logs = ReadRecentLines();
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version?.ToString() ?? "bilinmiyor";
        var content = new StringBuilder();
        content.AppendLine("MynAssistant destek tanı raporu");
        content.AppendLine($"Rapor zamanı UTC: {DateTimeOffset.UtcNow:O}");
        content.AppendLine($"Uygulama sürümü: {version}");
        content.AppendLine($"Windows: {Environment.OSVersion.VersionString}");
        content.AppendLine($"Mimari: {RuntimeInformation.ProcessArchitecture}");
        content.AppendLine($"Agent süreç durumu: {(AgentStartupManager.IsRunning() ? "Çalışıyor" : "Kapalı")}");
        content.AppendLine($"Desktop Bridge süreç durumu: {(DesktopBridgeStartupManager.IsRunning() ? "Çalışıyor" : "Kapalı")}");
        content.AppendLine($"Bridge bağlantı durumu: {Redact(bridgeStatus?.State ?? "bilinmiyor")}");
        content.AppendLine($"Son heartbeat/durum zamanı: {(bridgeStatus?.UpdatedAt.ToUniversalTime().ToString("O") ?? "bilinmiyor")}");
        content.AppendLine();
        content.AppendLine("Son günlük kayıtları (hassas değerler maskelenmiştir):");
        content.AppendLine(logs.Count == 0 ? "(Günlük kaydı bulunamadı.)" : string.Join(Environment.NewLine, logs.Select(Redact)));
        return content.ToString();
    }

    public static void ClearKnownLogs()
    {
        var paths = new[]
        {
            StructuredLogPath,
            Path.Combine(LogDirectory, "agent-startup.log"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MynAssistant", "DesktopAgent", "bridge.log")
        };

        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                WriteError("MYN-UI-004", "Diagnostics", "Günlük dosyası temizlenemedi.", ex);
                throw new IOException("Bir veya daha fazla günlük dosyası silinemedi.", ex);
            }
        }
    }

    public static void OpenLogDirectory()
    {
        Directory.CreateDirectory(LogDirectory);
        ProcessLauncher.OpenDirectory(LogDirectory);
    }

    private static void AddLines(string path, List<(DateTime Timestamp, string Line)> target)
    {
        try
        {
            if (!File.Exists(path))
                return;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length > MaxLogBytes)
                stream.Seek(-MaxLogBytes, SeekOrigin.End);

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            if (stream.Position > 0)
                _ = reader.ReadLine();

            var lines = new Queue<string>();
            while (reader.ReadLine() is { } line)
            {
                lines.Enqueue(Redact(line));
                while (lines.Count > MaxLinesPerSource)
                    lines.Dequeue();
            }

            var fallback = File.GetLastWriteTimeUtc(path);
            foreach (var line in lines)
            {
                var timestamp = TryReadTimestamp(line) ?? fallback;
                target.Add((timestamp, line));
            }
        }
        catch (Exception ex)
        {
            target.Add((DateTime.UtcNow, $"[Günlük okunamadı] {Path.GetFileName(path)}: {Redact(ex.Message)}"));
        }
    }

    private static DateTime? TryReadTimestamp(string line)
    {
        try
        {
            using var json = JsonDocument.Parse(line);
            if (json.RootElement.TryGetProperty("timestampUtc", out var timestamp)
                && timestamp.ValueKind == JsonValueKind.String
                && timestamp.TryGetDateTime(out var parsed))
                return parsed;
        }
        catch
        {
            // Legacy logs are plain text and use file time as their fallback timestamp.
        }

        var match = Regex.Match(line, @"^\[(?<timestamp>[^\]]+)\]");
        return match.Success && DateTime.TryParse(match.Groups["timestamp"].Value, out var value)
            ? value.ToUniversalTime()
            : null;
    }

    private static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var output = Regex.Replace(
            value,
            @"(?i)(refreshToken|idToken|access[_-]?token|client[_-]?secret|password|api[_-]?key)""?\s*[:=]\s*""?[^\s,;""'}]+",
            "$1=[MASKELENDİ]");
        output = Regex.Replace(output, @"(?i)Bearer\s+[A-Za-z0-9._~-]+", "Bearer [MASKELENDİ]");
        output = Regex.Replace(output, @"\bAIza[0-9A-Za-z_-]{20,}\b", "[API ANAHTARI MASKELENDİ]");
        output = Regex.Replace(output, @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", "[E-POSTA MASKELENDİ]", RegexOptions.IgnoreCase);
        output = Regex.Replace(output, @"(?i)[A-Z]:\\Users\\[^\\\s]+", "[KULLANICI KLASÖRÜ]");
        output = Regex.Replace(output, @"(?i)\b(ownerId|deviceId|uid)\b\s*[:=]\s*[^\s,;""']+", "$1=[MASKELENDİ]");
        output = Regex.Replace(output, @"\b(?:win|windows)-[A-Za-z0-9-]{6,}\b", "[CİHAZ KİMLİĞİ MASKELENDİ]");
        return output;
    }

    private static void RotateIfNeeded(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < MaxLogBytes)
            return;

        for (var index = BackupCount - 1; index >= 1; index--)
        {
            var source = $"{path}.{index}";
            var destination = $"{path}.{index + 1}";
            if (File.Exists(source))
                File.Move(source, destination, overwrite: true);
        }

        File.Move(path, $"{path}.1", overwrite: true);
    }
}

internal static class ProcessLauncher
{
    public static void OpenDirectory(string directory)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true
        });
    }
}
