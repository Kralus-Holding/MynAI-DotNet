namespace MynAI.Core.Commands;

public enum AgentRequestType
{
    CreateScheduledTask,
    CancelScheduledTask,
    GetScheduledTasks
}

public sealed class AgentRequest
{
    public AgentRequestType Type { get; init; }

    public string? TaskId { get; init; }

    public string? Action { get; init; }

    public DateTimeOffset? ExecuteAt { get; init; }

    public string? Description { get; init; }
}
