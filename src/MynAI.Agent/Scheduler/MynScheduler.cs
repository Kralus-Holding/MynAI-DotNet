using MynAI.Core.Interfaces;
using MynAI.Core.Models;
using MynAI.Agent.Notifications;
using MynAI.Agent.Storage;

namespace MynAI.Agent.Scheduler;

public sealed class MynScheduler
{
    private readonly object _lock = new();
    private readonly List<ScheduledTask> _tasks = [];

    private readonly IPowerService _powerService;
    private readonly IScheduledTaskStore _store;
    private readonly AgentNotificationService _notificationService;
    private readonly ScheduledTaskWarningStore _warningStore;

    public MynScheduler(
        IPowerService powerService,
        IScheduledTaskStore store,
        AgentNotificationService notificationService,
        ScheduledTaskWarningStore warningStore)
    {
        _powerService = powerService;
        _store = store;
        _notificationService = notificationService;
        _warningStore = warningStore;
    }

    public async Task InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        await _store.InitializeAsync(
            cancellationToken);

        var tasks =
            await _store.GetAllAsync(
                cancellationToken);

        // An Executing task survived an Agent restart. Re-queuing it can replay
        // destructive work (such as shutdown/restart) on every boot, so fail it
        // and persist that decision instead.
        foreach (var task in tasks.Where(
                     x => x.Status == ScheduledTaskStatus.Executing))
        {
            task.Status = ScheduledTaskStatus.Failed;
            await _store.UpdateStatusAsync(
                task.Id,
                ScheduledTaskStatus.Failed,
                cancellationToken);
            Console.WriteLine(
                $"[Scheduler] Task {task.Id} was interrupted while executing; " +
                "marked failed to prevent duplicate execution.");
        }

        lock (_lock)
        {
            _tasks.Clear();
            _tasks.AddRange(tasks);
        }
    }

    public async Task AddAsync(
        ScheduledTask task,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _tasks.Add(task);
        }

        try
        {
            await _store.SaveAsync(
                task,
                cancellationToken);
        }
        catch
        {
            lock (_lock)
            {
                _tasks.RemoveAll(
                    x => x.Id == task.Id);
            }

            throw;
        }
    }

    public async Task<bool> CancelAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        ScheduledTask? task;

        lock (_lock)
        {
            task =
                _tasks.FirstOrDefault(
                    x => x.Id == taskId);

            if (task is null)
                return false;

            if (task.Status !=
                ScheduledTaskStatus.Pending)
                return false;

            task.Status =
                ScheduledTaskStatus.Cancelled;
        }

        try
        {
            await _store.UpdateStatusAsync(
                taskId,
                ScheduledTaskStatus.Cancelled,
                cancellationToken);

            return true;
        }
        catch
        {
            lock (_lock)
            {
                task.Status =
                    ScheduledTaskStatus.Pending;
            }

            throw;
        }
    }

    public IReadOnlyList<ScheduledTask> GetTasks()
    {
        lock (_lock)
        {
            return _tasks
                .OrderBy(
                    x => x.ExecuteAt)
                .ToList()
                .AsReadOnly();
        }
    }

    public async Task RunAsync(
        CancellationToken cancellationToken)
    {
        using var timer =
            new PeriodicTimer(
                TimeSpan.FromSeconds(1));

        while (await timer.WaitForNextTickAsync(
                   cancellationToken))
        {
            await CheckTasksAsync(
                cancellationToken);
        }
    }

    private async Task CheckTasksAsync(
        CancellationToken cancellationToken)
    {
        List<ScheduledTask> pendingTasks;

        lock (_lock)
        {
            pendingTasks =
                _tasks
                    .Where(x =>
                        x.Status ==
                            ScheduledTaskStatus.Pending)
                    .ToList();
        }

        var now =
            DateTimeOffset.Now;

        foreach (var task in pendingTasks)
        {
            var remaining =
                task.ExecuteAt - now;

            if (remaining > TimeSpan.Zero &&
                remaining <= TimeSpan.FromMinutes(10) &&
                remaining > TimeSpan.FromMinutes(5))
            {
                await ProcessWarningAsync(
                    task,
                    10,
                    remaining,
                    cancellationToken);
            }
            else if (remaining > TimeSpan.Zero &&
                     remaining <= TimeSpan.FromMinutes(5))
            {
                await ProcessWarningAsync(
                    task,
                    5,
                    remaining,
                    cancellationToken);
            }
        }

        List<ScheduledTask> dueTasks;

        lock (_lock)
        {
            var currentTime =
                DateTimeOffset.Now;

            dueTasks =
                _tasks
                    .Where(x =>
                        x.Status ==
                            ScheduledTaskStatus.Pending &&
                        x.ExecuteAt <= currentTime)
                    .ToList();

            foreach (var task in dueTasks)
            {
                task.Status =
                    ScheduledTaskStatus.Executing;
            }
        }

        foreach (var task in dueTasks)
        {
            try
            {
                await _store.UpdateStatusAsync(
                    task.Id,
                    ScheduledTaskStatus.Executing,
                    cancellationToken);

                switch (task.Action)
                {
                    case ScheduledAction.Shutdown:
                        await _powerService.ShutdownAsync(
                            cancellationToken);
                        break;

                    case ScheduledAction.Sleep:
                        await _powerService.SleepAsync(
                            cancellationToken);
                        break;

                    case ScheduledAction.Lock:
                        await _powerService.LockAsync(
                            cancellationToken);
                        break;

                    case ScheduledAction.Restart:
                        await _powerService.RestartAsync();
                        break;

                    default:
                        throw new InvalidOperationException(
                            $"Desteklenmeyen eylem: {task.Action}");
                }

                task.Status =
                    ScheduledTaskStatus.Completed;

                await _store.UpdateStatusAsync(
                    task.Id,
                    ScheduledTaskStatus.Completed,
                    cancellationToken);
            }
            catch
            {
                task.Status =
                    ScheduledTaskStatus.Failed;

                await _store.UpdateStatusAsync(
                    task.Id,
                    ScheduledTaskStatus.Failed,
                    cancellationToken);
            }
        }
    }

    private async Task ProcessWarningAsync(
        ScheduledTask task,
        int warningMinutes,
        TimeSpan remaining,
        CancellationToken cancellationToken)
    {
        var alreadySent =
            await _warningStore.HasWarningBeenSentAsync(
                task.Id,
                warningMinutes,
                cancellationToken);

        if (alreadySent)
            return;

        bool sent;

        if (warningMinutes == 10)
        {
            sent =
                _notificationService.SendScheduledWarning(
                    task,
                    remaining);
        }
        else
        {
            sent =
                _notificationService.SendFiveMinuteWarning(
                    task);
        }

        if (!sent)
            return;

        await _warningStore.MarkWarningSentAsync(
            task.Id,
            warningMinutes,
            cancellationToken);
    }
}
