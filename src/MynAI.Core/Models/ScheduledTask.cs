namespace MynAI.Core.Models;

public enum ScheduledAction
{
    Shutdown,
    Sleep,
    Lock,
    Restart}

public enum ScheduledTaskStatus
{
    Pending,
    Cancelled,
    Executing,
    Completed,
    Failed
}

public sealed class ScheduledTask
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public ScheduledAction Action { get; init; }

    public DateTimeOffset ExecuteAt { get; init; }

    public ScheduledTaskStatus Status { get; set; } = ScheduledTaskStatus.Pending;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public string? Description { get; init; }
}
