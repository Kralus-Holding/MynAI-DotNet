using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using MynAI.Agent.Scheduler;
using MynAI.Core.Commands;
using MynAI.Core.Models;

namespace MynAI.Agent.IPC;

public sealed class AgentPipeServer
{
    private const string PipeName = "MynAI.Agent";
    private const int MaxPipeInstances = 10;

    private readonly MynScheduler _scheduler;

    public AgentPipeServer(MynScheduler scheduler)
    {
        _scheduler = scheduler;
    }

    public async Task RunAsync(
        CancellationToken cancellationToken)
    {
        var clientTasks = new List<Task>();

        while (!cancellationToken.IsCancellationRequested)
        {
            clientTasks.RemoveAll(
                task => task.IsCompleted);

            var pipe = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                MaxPipeInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await pipe.WaitForConnectionAsync(
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                break;
            }
            catch
            {
                await pipe.DisposeAsync();
                throw;
            }

            clientTasks.Add(
                HandleClientAndDisposeAsync(
                    pipe,
                    cancellationToken));
        }

        if (clientTasks.Count > 0)
        {
            await Task.WhenAll(clientTasks);
        }
    }

    private async Task HandleClientAndDisposeAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        await using (pipe)
        {
            await HandleClientAsync(
                pipe,
                cancellationToken);
        }
    }

    private async Task HandleClientAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            pipe,
            Encoding.UTF8,
            leaveOpen: true);

        await using var writer = new StreamWriter(
            pipe,
            new UTF8Encoding(false),
            leaveOpen: true)
        {
            AutoFlush = true
        };

        var requestJson =
            await reader.ReadLineAsync(
                cancellationToken);

        if (string.IsNullOrWhiteSpace(requestJson))
            return;

        AgentRequest? request;

        try
        {
            request =
                JsonSerializer.Deserialize<AgentRequest>(
                    requestJson);
        }
        catch
        {
            await writer.WriteLineAsync(
                """{"success":false,"error":"Invalid request"}""");

            return;
        }

        if (request is null)
        {
            await writer.WriteLineAsync(
                """{"success":false,"error":"Empty request"}""");

            return;
        }

        try
        {
            var response =
                await ProcessRequestAsync(
                    request,
                    cancellationToken);

            var responseJson =
                JsonSerializer.Serialize(response);

            await writer.WriteLineAsync(
                responseJson);
        }
        catch (Exception ex)
        {
            var errorResponse = new
            {
                success = false,
                error = ex.Message
            };

            await writer.WriteLineAsync(
                JsonSerializer.Serialize(
                    errorResponse));
        }
    }

    private async Task<object> ProcessRequestAsync(
        AgentRequest request,
        CancellationToken cancellationToken)
    {
        switch (request.Type)
        {
            case AgentRequestType.CreateScheduledTask:
            {
                if (!request.ExecuteAt.HasValue)
                {
                    throw new InvalidOperationException(
                        "ExecuteAt gerekli.");
                }

                if (string.IsNullOrWhiteSpace(
                        request.Action))
                {
                    throw new InvalidOperationException(
                        "Action gerekli.");
                }

                if (!Enum.TryParse<ScheduledAction>(
                        request.Action,
                        true,
                        out var action))
                {
                    throw new InvalidOperationException(
                        "Geçersiz action.");
                }

                var task = new ScheduledTask
                {
                    Action = action,
                    ExecuteAt = request.ExecuteAt.Value,
                    Description = request.Description
                };

                await _scheduler.AddAsync(
                    task,
                    cancellationToken);

                return new
                {
                    success = true,
                    taskId = task.Id,
                    task
                };
            }

            case AgentRequestType.CancelScheduledTask:
            {
                if (!Guid.TryParse(
                        request.TaskId,
                        out var taskId))
                {
                    throw new InvalidOperationException(
                        "Geçersiz taskId.");
                }

                var cancelled =
                    await _scheduler.CancelAsync(
                        taskId,
                        cancellationToken);

                return new
                {
                    success = cancelled,
                    taskId
                };
            }

            case AgentRequestType.GetScheduledTasks:
            {
                return new
                {
                    success = true,
                    tasks = _scheduler.GetTasks()
                };
            }

            default:
                throw new InvalidOperationException(
                    "Desteklenmeyen request tipi.");
        }
    }
}
