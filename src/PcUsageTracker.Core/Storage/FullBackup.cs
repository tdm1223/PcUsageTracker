using System.IO.Compression;
using Microsoft.Data.Sqlite;

namespace PcUsageTracker.Core.Storage;

/// <summary>A portable snapshot of the database and persistent application icons.</summary>
public static class FullBackup
{
    const string DatabaseEntry = "history.db";
    const long MaxDatabaseBytes = 4L * 1024 * 1024 * 1024;
    const long MaxIconBytes = 4L * 1024 * 1024;
    const long MaxIconsBytes = 512L * 1024 * 1024;
    static readonly string[] RequiredTables =
        ["schema_version", "sessions", "processes", "excluded_processes", "settings", "categories", "application_rules"];
    static readonly Dictionary<string, string[]> RequiredColumns = new()
    {
        ["schema_version"] = ["version"],
        ["sessions"] = ["id", "process_name", "start_at", "end_at", "duration_sec", "last_seen_at"],
        ["processes"] = ["name", "exe_path", "last_seen_at"],
        ["excluded_processes"] = ["name", "reason", "excluded_at"],
        ["settings"] = ["key", "value"],
        ["categories"] = ["id", "name", "color_rgb", "sort_order"],
        ["application_rules"] = ["process_name", "alias", "category_id", "color_override_rgb", "updated_at"],
    };

    public static void Export(SqliteStore store, string iconDirectory, string destination)
    {
        ArgumentNullException.ThrowIfNull(store);
        Export(store.Connection, iconDirectory, destination);
    }

    public static void Export(SqliteConnection source, string iconDirectory, string destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        var target = Path.GetFullPath(destination);
        var database = Path.GetFullPath(source.DataSource);
        if (string.Equals(target, database, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target, database + "-wal", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target, database + "-shm", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The backup cannot replace the live database or its journal.", nameof(destination));
        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        var temporaryArchive = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        var temporaryDatabase = Path.Combine(Path.GetTempPath(), $"pcut-backup-{Guid.NewGuid():N}.db");
        try
        {
            using (var snapshot = OpenDatabase(temporaryDatabase, SqliteOpenMode.ReadWriteCreate))
                source.BackupDatabase(snapshot);
            if (new FileInfo(temporaryDatabase).Length > MaxDatabaseBytes)
                throw new InvalidDataException("The database exceeds the backup size limit.");
            using (var archive = ZipFile.Open(temporaryArchive, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(temporaryDatabase, DatabaseEntry, CompressionLevel.Optimal);
                long iconBytes = 0;
                if (Directory.Exists(iconDirectory))
                    foreach (var path in Directory.EnumerateFiles(iconDirectory, "*.png"))
                    {
                        var name = Path.GetFileName(path);
                        if (!IsIconName(name)) continue;
                        var length = new FileInfo(path).Length;
                        iconBytes += length;
                        if (length > MaxIconBytes || iconBytes > MaxIconsBytes)
                            throw new InvalidDataException("Cached icons exceed the backup size limit.");
                        archive.CreateEntryFromFile(path, $"icons/{name}", CompressionLevel.Optimal);
                    }
            }
            File.Move(temporaryArchive, target, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryDatabase);
            TryDelete(temporaryArchive);
        }
    }

    /// <summary>Validates before replacing the database; icon copy failures are reported separately.</summary>
    public static int Restore(SqliteStore store, string iconDirectory, string backupPath, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(store);
        var tempDir = Path.Combine(Path.GetTempPath(), $"pcut-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            using var archive = ZipFile.OpenRead(backupPath);
            var entries = archive.Entries.Where(entry => entry.FullName == DatabaseEntry).ToArray();
            if (entries.Length != 1 || entries[0].Length <= 0 || entries[0].Length > MaxDatabaseBytes)
                throw new InvalidDataException("Backup must contain one valid history database.");
            var dbPath = Path.Combine(tempDir, DatabaseEntry);
            entries[0].ExtractToFile(dbPath);
            var iconEntries = archive.Entries.Where(entry => entry.FullName.StartsWith("icons/", StringComparison.Ordinal)).ToArray();
            if (iconEntries.Any(entry => !IsIconName(entry.FullName[6..]) || entry.Length > MaxIconBytes) ||
                iconEntries.Sum(entry => entry.Length) > MaxIconsBytes ||
                iconEntries.Select(entry => entry.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != iconEntries.Length)
                throw new InvalidDataException("Invalid icon entries in backup.");
            foreach (var entry in iconEntries)
                entry.ExtractToFile(Path.Combine(tempDir, entry.FullName[6..]));

            using (var snapshot = OpenDatabase(dbPath, SqliteOpenMode.ReadWrite))
            {
                Validate(snapshot);
                using var close = snapshot.CreateCommand();
                close.CommandText = """
                    UPDATE sessions
                    SET end_at = MAX(start_at, MIN($now, COALESCE(last_seen_at, start_at))),
                        duration_sec = MAX(start_at, MIN($now, COALESCE(last_seen_at, start_at))) - start_at,
                        last_seen_at = MAX(start_at, MIN($now, COALESCE(last_seen_at, start_at)))
                    WHERE end_at IS NULL;
                    """;
                close.Parameters.AddWithValue("$now", nowUtc.ToUnixTimeSeconds());
                close.ExecuteNonQuery();
                // SQLite's backup API commits the replacement atomically, even in WAL mode.
                snapshot.BackupDatabase(store.Connection);
            }

            var failures = 0;
            try { Directory.CreateDirectory(iconDirectory); }
            catch (IOException) { return iconEntries.Length; }
            catch (UnauthorizedAccessException) { return iconEntries.Length; }
            foreach (var entry in iconEntries)
            {
                try { File.Copy(Path.Combine(tempDir, entry.FullName[6..]), Path.Combine(iconDirectory, entry.FullName[6..]), true); }
                catch (IOException) { failures++; }
                catch (UnauthorizedAccessException) { failures++; }
            }
            return failures;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    static SqliteConnection OpenDatabase(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = mode, Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    static void Validate(SqliteConnection database)
    {
        using var command = database.CreateCommand();
        command.CommandText = "PRAGMA quick_check";
        if (!string.Equals(command.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Backup database failed its integrity check.");
        command.CommandText = "SELECT MAX(version) FROM schema_version";
        if (Convert.ToInt32(command.ExecuteScalar()) != SqliteStore.CurrentSchemaVersion)
            throw new InvalidDataException("Backup database version is not supported.");
        foreach (var table in RequiredTables)
        {
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$name", table);
            if (Convert.ToInt32(command.ExecuteScalar()) != 1)
                throw new InvalidDataException($"Backup database is missing {table}.");
        }
        command.Parameters.Clear();
        foreach (var (table, columns) in RequiredColumns)
        {
            command.CommandText = $"PRAGMA table_info({table})";
            using var tableInfo = command.ExecuteReader();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (tableInfo.Read()) names.Add(tableInfo.GetString(1));
            if (columns.Any(column => !names.Contains(column)))
                throw new InvalidDataException($"Backup database has an incompatible {table} table.");
        }
        command.CommandText = "SELECT COUNT(*) FROM categories WHERE id = 6";
        if (Convert.ToInt32(command.ExecuteScalar()) != 1)
            throw new InvalidDataException("Backup database is missing its default category.");
        command.CommandText = "PRAGMA foreign_key_check";
        using var reader = command.ExecuteReader();
        if (reader.Read()) throw new InvalidDataException("Backup database contains invalid references.");
    }

    static bool IsIconName(string name) => name.Length == 68 && name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
        name.AsSpan(0, 64).ToString().All(Uri.IsHexDigit);

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
