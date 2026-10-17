namespace MynAI.Core.Interfaces;

public interface IPowerService
{
    Task ShutdownAsync(CancellationToken cancellationToken = default);
    Task RestartAsync(CancellationToken cancellationToken = default);

    Task SleepAsync(CancellationToken cancellationToken = default);

    Task LockAsync(CancellationToken cancellationToken = default);
}
