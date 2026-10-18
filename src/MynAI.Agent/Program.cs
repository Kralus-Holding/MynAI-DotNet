using MynAI.Agent.IPC;
using MynAI.Agent.Notifications;
using MynAI.Agent.Power;
using MynAI.Agent.Scheduler;
using MynAI.Agent.Storage;

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
