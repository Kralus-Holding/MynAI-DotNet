using Microsoft.Data.Sqlite;

namespace MynAI.Agent.Storage;

public sealed class ScheduledTaskWarningStore
{
    private readonly string _connectionString;

    public ScheduledTaskWarningStore(string databasePath)
    {
        _connectionString =
            $"Data Source={databasePath}";

        using var connection =
            new SqliteConnection(_connectionString);

        connection.Open();

        using var command =
            connection.CreateCommand();

        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS ScheduledTaskWarningsV2
            (
                TaskId TEXT NOT NULL,
                WarningMinutes INTEGER NOT NULL,
                WarnedAt TEXT NOT NULL,
                PRIMARY KEY (TaskId, WarningMinutes)
            );
            """;

        command.ExecuteNonQuery();
    }

    public async Task<bool> HasWarningBeenSentAsync(
        Guid taskId,
        int warningMinutes,
        CancellationToken cancellationToken)
    {
        await using var connection =
            new SqliteConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);

        await using var command =
            connection.CreateCommand();

        command.CommandText =
            """
            SELECT 1
            FROM ScheduledTaskWarningsV2
            WHERE TaskId = $taskId
              AND WarningMinutes = $warningMinutes
            LIMIT 1;
            """;

        command.Parameters.AddWithValue(
            "$taskId",
            taskId.ToString());

        command.Parameters.AddWithValue(
            "$warningMinutes",
            warningMinutes);

        var result =
            await command.ExecuteScalarAsync(
                cancellationToken);

        return result is not null;
    }

    public async Task MarkWarningSentAsync(
        Guid taskId,
        int warningMinutes,
        CancellationToken cancellationToken)
    {
        await using var connection =
            new SqliteConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);

        await using var command =
            connection.CreateCommand();

        command.CommandText =
            """
            INSERT OR IGNORE INTO ScheduledTaskWarningsV2
                (TaskId, WarningMinutes, WarnedAt)
            VALUES
                ($taskId, $warningMinutes, $warnedAt);
            """;

        command.Parameters.AddWithValue(
            "$taskId",
            taskId.ToString());

        command.Parameters.AddWithValue(
            "$warningMinutes",
            warningMinutes);

        command.Parameters.AddWithValue(
            "$warnedAt",
            DateTimeOffset.Now.ToString("O"));

        await command.ExecuteNonQueryAsync(
            cancellationToken);
    }
}
