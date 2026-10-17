using Microsoft.Data.Sqlite;
using MynAI.Core.Interfaces;
using MynAI.Core.Models;

namespace MynAI.Agent.Storage;

public sealed class ScheduledTaskStore : IScheduledTaskStore
{
    private readonly string _connectionString;

    public ScheduledTaskStore(string databasePath)
    {
        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = $"Data Source={fullPath}";
    }

    public async Task InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            new SqliteConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();

        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ScheduledTasks
            (
                Id TEXT PRIMARY KEY,
                Action INTEGER NOT NULL,
                ExecuteAt TEXT NOT NULL,
                Status INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL,
                Description TEXT NULL
            );
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveAsync(
        ScheduledTask task,
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            new SqliteConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO ScheduledTasks
            (
                Id,
                Action,
                ExecuteAt,
                Status,
                CreatedAt,
                Description
            )
            VALUES
            (
                $id,
                $action,
                $executeAt,
                $status,
                $createdAt,
                $description
            )
            ON CONFLICT(Id) DO UPDATE SET
                Action = excluded.Action,
                ExecuteAt = excluded.ExecuteAt,
                Status = excluded.Status,
                CreatedAt = excluded.CreatedAt,
                Description = excluded.Description;
            """;

        command.Parameters.AddWithValue(
            "$id",
            task.Id.ToString());

        command.Parameters.AddWithValue(
            "$action",
            (int)task.Action);

        command.Parameters.AddWithValue(
            "$executeAt",
            task.ExecuteAt.ToString("O"));

        command.Parameters.AddWithValue(
            "$status",
            (int)task.Status);

        command.Parameters.AddWithValue(
            "$createdAt",
            task.CreatedAt.ToString("O"));

        command.Parameters.AddWithValue(
            "$description",
            (object?)task.Description ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(
            cancellationToken);
    }

    public async Task<List<ScheduledTask>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        var tasks = new List<ScheduledTask>();

        await using var connection =
            new SqliteConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();

        command.CommandText = """
            SELECT
                Id,
                Action,
                ExecuteAt,
                Status,
                CreatedAt,
                Description
            FROM ScheduledTasks
            ORDER BY ExecuteAt;
            """;

        await using var reader =
            await command.ExecuteReaderAsync(
                cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            tasks.Add(new ScheduledTask
            {
                Id = Guid.Parse(reader.GetString(0)),
                Action = (ScheduledAction)reader.GetInt32(1),
                ExecuteAt =
                    DateTimeOffset.Parse(reader.GetString(2)),
                Status =
                    (ScheduledTaskStatus)reader.GetInt32(3),
                CreatedAt =
                    DateTimeOffset.Parse(reader.GetString(4)),
                Description =
                    reader.IsDBNull(5)
                        ? null
                        : reader.GetString(5)
            });
        }

        return tasks;
    }

    public async Task UpdateStatusAsync(
        Guid taskId,
        ScheduledTaskStatus status,
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            new SqliteConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();

        command.CommandText = """
            UPDATE ScheduledTasks
            SET Status = $status
            WHERE Id = $id;
            """;

        command.Parameters.AddWithValue(
            "$id",
            taskId.ToString());

        command.Parameters.AddWithValue(
            "$status",
            (int)status);

        await command.ExecuteNonQueryAsync(
            cancellationToken);
    }

    public async Task DeleteAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            new SqliteConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();

        command.CommandText = """
            DELETE FROM ScheduledTasks
            WHERE Id = $id;
            """;

        command.Parameters.AddWithValue(
            "$id",
            taskId.ToString());

        await command.ExecuteNonQueryAsync(
            cancellationToken);
    }
}
