using MynAI.Core.Models;

namespace MynAI.Core.Interfaces;

public interface IScheduledTaskStore
{
    Task InitializeAsync(
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        ScheduledTask task,
        CancellationToken cancellationToken = default);

    Task<List<ScheduledTask>> GetAllAsync(
        CancellationToken cancellationToken = default);

    Task UpdateStatusAsync(
        Guid taskId,
        ScheduledTaskStatus status,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        Guid taskId,
        CancellationToken cancellationToken = default);
}
