using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using DutyRouletteLogger.Models;

namespace DutyRouletteLogger.Data;

/// <summary>
/// Repository for accessing the SQLite database of instance runs.
///
/// Robustness notes (replacing the original "Data Protection" request, which is an
/// ASP.NET Core concept that doesn't apply to a local SQLite file):
/// - journal_mode=WAL: reduces the risk of corruption on a game/plugin crash, since changes
///   are written to a write-ahead log before being applied to the database.
/// - busy_timeout: avoids "database is locked" exceptions on concurrent access.
/// - Any failure opening/migrating the database marks <see cref="IsAvailable"/> as false;
///   whoever calls this repository (<see cref="Services.InstanceTrackerService"/>) should,
///   in that case, just log the event via <see cref="LogService"/> (visible in /xllog) —
///   nothing is persisted until the database comes back.
/// </summary>
public sealed class InstanceRepository : IDisposable
{
    private const int SchemaVersion = 2;

    private readonly string databasePath;
    private readonly SemaphoreSlim connectionLock = new(1, 1);
    private SqliteConnection? connection;
    private bool disposed;

    public bool IsAvailable { get; private set; }

    /// <summary>
    /// Raised right after a new run is successfully inserted (<see cref="InsertRunStartAsync"/>),
    /// carrying the new row's Id. The Run History window subscribes to this to refresh itself
    /// automatically instead of waiting for the user to click "Refresh". May fire on a
    /// background thread — subscribers should only do cheap, thread-safe work (e.g. flipping a
    /// flag / kicking off their own async refresh), not touch ImGui state directly.
    /// </summary>
    public event Action<long>? RunInserted;

    public InstanceRepository(string databaseDirectory)
    {
        Directory.CreateDirectory(databaseDirectory);
        databasePath = Path.Combine(databaseDirectory, "DutyRouletteLogger.db");
    }

    public async Task InitializeAsync()
    {
        try
        {
            // Ensures the native SQLite provider (e_sqlite3) is registered.
            // Harmless if it's already been initialized by other code.
            SQLitePCL.Batteries_V2.Init();

            connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync().ConfigureAwait(false);

            await ExecuteNonQueryAsync(connection, "PRAGMA journal_mode=WAL;").ConfigureAwait(false);
            await ExecuteNonQueryAsync(connection, "PRAGMA synchronous=NORMAL;").ConfigureAwait(false);
            await ExecuteNonQueryAsync(connection, "PRAGMA busy_timeout=5000;").ConfigureAwait(false);

            await MigrateAsync(connection).ConfigureAwait(false);

            IsAvailable = true;
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            DutyRouletteLoggerPlugin.Log.Error(ex, "DutyRouletteLogger: failed to initialize the SQLite database. " +
                                              "Instance events will only show up in the Dalamud log (/xllog) until the database comes back.");

            // Tries to quarantine a corrupted database file so it doesn't permanently block
            // the plugin's next startups.
            TryQuarantineCorruptDatabase();
        }
    }

    private void TryQuarantineCorruptDatabase()
    {
        try
        {
            connection?.Dispose();
            connection = null;

            if (File.Exists(databasePath))
            {
                var quarantinePath = databasePath + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
                File.Move(databasePath, quarantinePath);
                DutyRouletteLoggerPlugin.Log.Warning($"DutyRouletteLogger: possibly corrupted database moved to {quarantinePath}.");
            }
        }
        catch (Exception ex)
        {
            DutyRouletteLoggerPlugin.Log.Error(ex, "DutyRouletteLogger: failed to quarantine the corrupted SQLite database.");
        }
    }

    private static async Task MigrateAsync(SqliteConnection conn)
    {
        var currentVersion = Convert.ToInt32(await ExecuteScalarAsync(conn, "PRAGMA user_version;").ConfigureAwait(false));

        if (currentVersion < 1)
        {
            const string createTable = """
                CREATE TABLE IF NOT EXISTS InstanceRuns (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    QueueType TEXT NOT NULL,
                    InstanceName TEXT NOT NULL,
                    EnterTimestamp TEXT NOT NULL,
                    ExitTimestamp TEXT,
                    RunDurationSeconds INTEGER,
                    PartySize INTEGER,
                    IsComplete INTEGER NOT NULL DEFAULT 0,
                    Role TEXT,
                    Notes TEXT
                );
                """;

            const string createIndex = """
                CREATE INDEX IF NOT EXISTS IX_InstanceRuns_EnterTimestamp ON InstanceRuns (EnterTimestamp);
                """;

            await ExecuteNonQueryAsync(conn, createTable).ConfigureAwait(false);
            await ExecuteNonQueryAsync(conn, createIndex).ConfigureAwait(false);
        }

        if (currentVersion < 2)
        {
            // SQLite's ADD COLUMN doesn't support IF NOT EXISTS, but this whole block only
            // runs once per database (gated by user_version), so that's not an issue here.
            await ExecuteNonQueryAsync(conn, "ALTER TABLE InstanceRuns ADD COLUMN Job TEXT;").ConfigureAwait(false);
            await ExecuteNonQueryAsync(conn, "ALTER TABLE InstanceRuns ADD COLUMN InstanceType TEXT;").ConfigureAwait(false);
            await ExecuteNonQueryAsync(conn, "ALTER TABLE InstanceRuns ADD COLUMN Expansion TEXT;").ConfigureAwait(false);
        }

        // Future incremental migrations go here as `if (currentVersion < 3) { ... }`.

        await ExecuteNonQueryAsync(conn, $"PRAGMA user_version = {SchemaVersion};").ConfigureAwait(false);
    }

    /// <summary>
    /// Inserts the start of a run and returns the generated Id. Throws if the database isn't
    /// available; the caller must check <see cref="IsAvailable"/> first.
    /// </summary>
    public async Task<long> InsertRunStartAsync(InstanceRun run)
    {
        EnsureAvailable();

        long newId;
        await connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            const string sql = """
                INSERT INTO InstanceRuns
                    (QueueType, InstanceName, EnterTimestamp, PartySize, IsComplete, Role, Job, InstanceType, Expansion, Notes)
                VALUES
                    ($queueType, $instanceName, $enterTimestamp, $partySize, 0, $role, $job, $instanceType, $expansion, $notes);
                SELECT last_insert_rowid();
                """;

            await using var command = connection!.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$queueType", run.QueueType);
            command.Parameters.AddWithValue("$instanceName", run.InstanceName);
            command.Parameters.AddWithValue("$enterTimestamp", run.EnterTimestampUtc.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$partySize", run.PartySize);
            command.Parameters.AddWithValue("$role", run.Role);
            command.Parameters.AddWithValue("$job", run.Job);
            command.Parameters.AddWithValue("$instanceType", run.InstanceType);
            command.Parameters.AddWithValue("$expansion", run.Expansion);
            command.Parameters.AddWithValue("$notes", (object?)run.Notes ?? DBNull.Value);

            var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
            newId = Convert.ToInt64(result);
        }
        finally
        {
            connectionLock.Release();
        }

        // Raised outside the try/finally above: RunInserted's subscribers (the Run History
        // window) kick off their own refresh, which calls back into GetRunsAsync and would
        // deadlock trying to re-acquire connectionLock if we were still holding it here.
        RunInserted?.Invoke(newId);

        return newId;
    }

    /// <summary>
    /// Marks a run as finished (completed or abandoned) and records its duration.
    /// </summary>
    public async Task CompleteRunAsync(long id, DateTime exitTimestampUtc, long durationSeconds, bool isComplete)
    {
        EnsureAvailable();

        await connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            const string sql = """
                UPDATE InstanceRuns
                SET ExitTimestamp = $exitTimestamp,
                    RunDurationSeconds = $duration,
                    IsComplete = $isComplete
                WHERE Id = $id;
                """;

            await using var command = connection!.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$exitTimestamp", exitTimestampUtc.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$duration", durationSeconds);
            command.Parameters.AddWithValue("$isComplete", isComplete ? 1 : 0);
            command.Parameters.AddWithValue("$id", id);

            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            connectionLock.Release();
        }
    }

    /// <summary>
    /// Appends a note to a run without changing its status (e.g. "Wipe detected").
    /// </summary>
    public async Task AppendNoteAsync(long id, string note)
    {
        EnsureAvailable();

        await connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            const string sql = """
                UPDATE InstanceRuns
                SET Notes = CASE
                                WHEN Notes IS NULL OR Notes = '' THEN $note
                                ELSE Notes || ' | ' || $note
                            END
                WHERE Id = $id;
                """;

            await using var command = connection!.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$note", note);
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            connectionLock.Release();
        }
    }

    public async Task<List<InstanceRun>> GetRunsAsync(RunFilter filter)
    {
        if (!IsAvailable)
        {
            return new List<InstanceRun>();
        }

        await connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var (whereClause, parameters) = BuildWhereClause(filter);
            var orderByClause = BuildOrderByClause(filter);

            var sql = $"SELECT * FROM InstanceRuns {whereClause} {orderByClause};";

            await using var command = connection!.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            var results = new List<InstanceRun>();
            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);

            // Column ordinals are the same for every row in this result set; computing them
            // once here avoids repeating the by-name lookup (GetOrdinal) on every row.
            var ordinals = new RunColumnOrdinals(reader);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                results.Add(ReadRun(reader, ordinals));
            }

            return results;
        }
        finally
        {
            connectionLock.Release();
        }
    }

    public async Task<InstanceStats> GetStatsAsync(RunFilter filter)
    {
        var runs = await GetRunsAsync(filter).ConfigureAwait(false);
        return ComputeStats(runs);
    }

    /// <summary>
    /// Computes the aggregated statistics from an already-loaded list of runs, without
    /// touching the database. The UI uses this so it doesn't repeat the same query that
    /// already ran to populate the history table (it already has the <see cref="InstanceRun"/>
    /// list in hand).
    /// </summary>
    public static InstanceStats ComputeStats(List<InstanceRun> runs)
    {
        var stats = new InstanceStats
        {
            TotalRuns = runs.Count,
            CompletedRuns = runs.Count(r => r.ExitTimestampUtc is not null && r.IsComplete),
            AbandonedRuns = runs.Count(r => r.ExitTimestampUtc is not null && !r.IsComplete),
            InProgressRuns = runs.Count(r => r.ExitTimestampUtc is null),
        };

        stats.AverageDurationSecondsByQueueType = runs
            .Where(r => r.IsComplete && r.RunDurationSeconds is not null)
            .GroupBy(r => r.QueueType)
            .ToDictionary(g => g.Key, g => g.Average(r => r.RunDurationSeconds!.Value));

        var mostPlayedInstance = runs
            .GroupBy(r => r.InstanceName)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        if (mostPlayedInstance is not null)
        {
            stats.MostPlayedInstance = mostPlayedInstance.Key;
            stats.MostPlayedInstanceCount = mostPlayedInstance.Count();
        }

        var mostUsedQueueType = runs
            .GroupBy(r => r.QueueType)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        if (mostUsedQueueType is not null)
        {
            stats.MostUsedQueueType = mostUsedQueueType.Key;
            stats.MostUsedQueueTypeCount = mostUsedQueueType.Count();
        }

        return stats;
    }

    /// <summary>
    /// Removes a specific run from history (e.g. manually deleted from the UI).
    /// </summary>
    public async Task DeleteRunAsync(long id)
    {
        EnsureAvailable();

        await connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var command = connection!.CreateCommand();
            command.CommandText = "DELETE FROM InstanceRuns WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            connectionLock.Release();
        }
    }

    public async Task<List<string>> GetDistinctQueueTypesAsync()
    {
        if (!IsAvailable)
        {
            return new List<string>();
        }

        await connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var command = connection!.CreateCommand();
            command.CommandText = "SELECT DISTINCT QueueType FROM InstanceRuns ORDER BY QueueType;";

            var results = new List<string>();
            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                results.Add(reader.GetString(0));
            }

            return results;
        }
        finally
        {
            connectionLock.Release();
        }
    }

    /// <summary>
    /// Removes runs older than <paramref name="retentionDays"/>. If &lt;= 0, does nothing
    /// (retention "forever").
    /// </summary>
    public async Task PurgeOldRunsAsync(int retentionDays)
    {
        if (!IsAvailable || retentionDays <= 0)
        {
            return;
        }

        await connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var cutoffUtc = DateTime.UtcNow.AddDays(-retentionDays).ToString("O", CultureInfo.InvariantCulture);

            await using var command = connection!.CreateCommand();
            command.CommandText = "DELETE FROM InstanceRuns WHERE EnterTimestamp < $cutoff;";
            command.Parameters.AddWithValue("$cutoff", cutoffUtc);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            connectionLock.Release();
        }
    }

    /// <summary>
    /// Exports the filtered runs to a CSV file and returns the path of the created file.
    /// </summary>
    public async Task<string> ExportCsvAsync(string directory, RunFilter filter)
    {
        var runs = await GetRunsAsync(filter).ConfigureAwait(false);

        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, $"DutyRouletteLogger_Export_{DateTime.Now:yyyy-MM-dd_HHmmss}.csv");

        var builder = new StringBuilder();
        builder.AppendLine("QueueType,InstanceName,InstanceType,Expansion,EnterTimestampUtc,ExitTimestampUtc,DurationSeconds,PartySize,Status,Role,Job,Notes");

        foreach (var run in runs)
        {
            builder.AppendLine(string.Join(",",
                CsvEscape(run.QueueType),
                CsvEscape(run.InstanceName),
                CsvEscape(run.InstanceType),
                CsvEscape(run.Expansion),
                CsvEscape(run.EnterTimestampUtc.ToString("O", CultureInfo.InvariantCulture)),
                CsvEscape(run.ExitTimestampUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty),
                CsvEscape(run.RunDurationSeconds?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                CsvEscape(run.PartySize.ToString(CultureInfo.InvariantCulture)),
                CsvEscape(run.StatusLabel),
                CsvEscape(run.Role),
                CsvEscape(run.Job),
                CsvEscape(run.Notes ?? string.Empty)));
        }

        await File.WriteAllTextAsync(filePath, builder.ToString(), Encoding.UTF8).ConfigureAwait(false);
        return filePath;
    }

    private static string CsvEscape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }

    private static (string Sql, List<(string Name, object Value)> Parameters) BuildWhereClause(RunFilter filter)
    {
        var conditions = new List<string>();
        var parameters = new List<(string, object)>();

        if (!string.IsNullOrWhiteSpace(filter.QueueType))
        {
            conditions.Add("QueueType = $filterQueueType");
            parameters.Add(("$filterQueueType", filter.QueueType));
        }

        if (!string.IsNullOrWhiteSpace(filter.InstanceNameContains))
        {
            conditions.Add("InstanceName LIKE $filterInstanceName");
            parameters.Add(("$filterInstanceName", $"%{filter.InstanceNameContains}%"));
        }

        if (filter.PeriodDays is > 0)
        {
            conditions.Add("EnterTimestamp >= $filterCutoff");
            parameters.Add(("$filterCutoff", DateTime.UtcNow.AddDays(-filter.PeriodDays.Value).ToString("O", CultureInfo.InvariantCulture)));
        }

        switch (filter.Status)
        {
            case StatusFilter.Completed:
                conditions.Add("(ExitTimestamp IS NOT NULL AND IsComplete = 1)");
                break;
            case StatusFilter.Abandoned:
                conditions.Add("(ExitTimestamp IS NOT NULL AND IsComplete = 0)");
                break;
            case StatusFilter.InProgress:
                conditions.Add("ExitTimestamp IS NULL");
                break;
            case StatusFilter.All:
            default:
                break;
        }

        var sql = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : string.Empty;
        return (sql, parameters);
    }

    private static string BuildOrderByClause(RunFilter filter)
    {
        var column = filter.SortColumn switch
        {
            RunSortColumn.Duration => "RunDurationSeconds",
            RunSortColumn.QueueType => "QueueType",
            RunSortColumn.InstanceName => "InstanceName",
            RunSortColumn.PartySize => "PartySize",
            RunSortColumn.Status => "IsComplete",
            RunSortColumn.InstanceType => "InstanceType",
            RunSortColumn.Job => "Job",
            RunSortColumn.EnterTimestamp => "EnterTimestamp",
            _ => "EnterTimestamp",
        };

        var direction = filter.SortAscending ? "ASC" : "DESC";
        return $"ORDER BY {column} {direction}";
    }

    /// <summary>
    /// Column ordinals resolved once per result set (see <see cref="GetRunsAsync"/>), instead
    /// of calling <see cref="SqliteDataReader.GetOrdinal"/> (a by-name lookup) on every row read.
    /// </summary>
    private readonly struct RunColumnOrdinals
    {
        public readonly int Id;
        public readonly int QueueType;
        public readonly int InstanceName;
        public readonly int EnterTimestamp;
        public readonly int ExitTimestamp;
        public readonly int RunDurationSeconds;
        public readonly int PartySize;
        public readonly int IsComplete;
        public readonly int Role;
        public readonly int Job;
        public readonly int InstanceType;
        public readonly int Expansion;
        public readonly int Notes;

        public RunColumnOrdinals(SqliteDataReader reader)
        {
            Id = reader.GetOrdinal("Id");
            QueueType = reader.GetOrdinal("QueueType");
            InstanceName = reader.GetOrdinal("InstanceName");
            EnterTimestamp = reader.GetOrdinal("EnterTimestamp");
            ExitTimestamp = reader.GetOrdinal("ExitTimestamp");
            RunDurationSeconds = reader.GetOrdinal("RunDurationSeconds");
            PartySize = reader.GetOrdinal("PartySize");
            IsComplete = reader.GetOrdinal("IsComplete");
            Role = reader.GetOrdinal("Role");
            Job = reader.GetOrdinal("Job");
            InstanceType = reader.GetOrdinal("InstanceType");
            Expansion = reader.GetOrdinal("Expansion");
            Notes = reader.GetOrdinal("Notes");
        }
    }

    private static InstanceRun ReadRun(SqliteDataReader reader, RunColumnOrdinals ordinals)
    {
        return new InstanceRun
        {
            Id = reader.GetInt64(ordinals.Id),
            QueueType = reader.GetString(ordinals.QueueType),
            InstanceName = reader.GetString(ordinals.InstanceName),
            EnterTimestampUtc = DateTime.Parse(reader.GetString(ordinals.EnterTimestamp), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            ExitTimestampUtc = reader.IsDBNull(ordinals.ExitTimestamp)
                ? null
                : DateTime.Parse(reader.GetString(ordinals.ExitTimestamp), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            RunDurationSeconds = reader.IsDBNull(ordinals.RunDurationSeconds)
                ? null
                : reader.GetInt64(ordinals.RunDurationSeconds),
            PartySize = reader.IsDBNull(ordinals.PartySize) ? 0 : reader.GetInt32(ordinals.PartySize),
            IsComplete = reader.GetInt32(ordinals.IsComplete) != 0,
            Role = reader.IsDBNull(ordinals.Role) ? "Unknown" : reader.GetString(ordinals.Role),
            Job = reader.IsDBNull(ordinals.Job) ? "Unknown" : reader.GetString(ordinals.Job),
            InstanceType = reader.IsDBNull(ordinals.InstanceType) ? "Unknown" : reader.GetString(ordinals.InstanceType),
            Expansion = reader.IsDBNull(ordinals.Expansion) ? "Unknown" : reader.GetString(ordinals.Expansion),
            Notes = reader.IsDBNull(ordinals.Notes) ? null : reader.GetString(ordinals.Notes),
        };
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection conn, string sql)
    {
        await using var command = conn.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<object?> ExecuteScalarAsync(SqliteConnection conn, string sql)
    {
        await using var command = conn.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync().ConfigureAwait(false);
    }

    private void EnsureAvailable()
    {
        if (!IsAvailable || connection is null)
        {
            throw new InvalidOperationException("DutyRouletteLogger: database unavailable.");
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        connection?.Dispose();
        connectionLock.Dispose();
    }
}
