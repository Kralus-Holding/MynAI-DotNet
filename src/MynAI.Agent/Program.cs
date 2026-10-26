using MynAI.Agent.IPC;
using MynAI.Agent.Notifications;
using MynAI.Agent.Power;
using MynAI.Agent.Scheduler;
using MynAI.Agent.Storage;
using System.Text;

// This process is built as a Windows GUI executable so the Agent never creates
// a console window during normal use. Preserve diagnosability in LocalAppData.
try
{
    var logDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MynAssistant",
        "Logs");
    Directory.CreateDirectory(logDirectory);
    var logPath = Path.Combine(logDirectory, $"MynAgent-{DateTime.Now:yyyyMMdd}.log");
    var logFile = new StreamWriter(
        new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
    {
        AutoFlush = true
    };
    var synchronizedWriter = TextWriter.Synchronized(logFile);
    Console.SetOut(synchronizedWriter);
    Console.SetError(synchronizedWriter);
    Console.WriteLine($"MynAgent started at {DateTimeOffset.Now:O}");
    Console.WriteLine($"Agent log: {logPath}");
}
catch
{
    // Logging is best-effort; a logging failure must not allocate a console
    // or stop the scheduler from starting.
}

AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
{
    try { Console.Error.WriteLine($"Unhandled Agent exception: {eventArgs.ExceptionObject}"); } catch { }
};

var dataDirectory = Path.Combine(
    Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData),
    "MynAI");

Directory.CreateDirectory(dataDirectory);

var databasePath = Path.Combine(
    dataDirectory,
    "mynai.db");

var store = new ScheduledTaskStore(databasePath);
var powerService = new WindowsPowerService();
var notificationService = new AgentNotificationService();
var warningStore = new ScheduledTaskWarningStore(databasePath);

var scheduler = new MynScheduler(
    powerService,
    store,
    notificationService,
    warningStore);

var pipeServer = new AgentPipeServer(scheduler);

using var cancellationTokenSource =
    new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellationTokenSource.Cancel();
};

await scheduler.InitializeAsync(
    cancellationTokenSource.Token);

Console.WriteLine("=== MynAgent (MynAssistant) ===");
Console.WriteLine(
    $"Saat: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
Console.WriteLine(
    $"Database: {databasePath}");
Console.WriteLine(
    $"Yüklü görev sayısı: {scheduler.GetTasks().Count}");
Console.WriteLine("Scheduler aktif.");
Console.WriteLine("Scheduler IPC kanalı aktif.");
Console.WriteLine("Durdurmak için Ctrl+C.");

var schedulerTask = scheduler.RunAsync(
    cancellationTokenSource.Token);

var pipeTask = pipeServer.RunAsync(
    cancellationTokenSource.Token);

try
{
    await Task.WhenAll(
        schedulerTask,
        pipeTask);
}
catch (OperationCanceledException)
{
    Console.WriteLine("MynAgent durduruldu.");
}
