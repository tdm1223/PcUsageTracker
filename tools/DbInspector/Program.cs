using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Data.Sqlite;

// --probe <name1> [<name2> ...] : QueryFullProcessImageName으로 실행 중인 프로세스 경로를 조회
if (args.Length > 0 && args[0] == "--probe")
{
    var targets = args.Skip(1).ToArray();
    if (targets.Length == 0)
    {
        Console.Error.WriteLine("Usage: DbInspector --probe <process_name> [<process_name> ...]");
        return 2;
    }

    int unresolved = 0;
    foreach (var name in targets)
    {
        var procs = Process.GetProcessesByName(name);
        if (procs.Length == 0)
        {
            Console.WriteLine($"  {name,-25}  (not running)");
            unresolved++;
            continue;
        }
        foreach (var p in procs)
        {
            var path = QueryExePath((uint)p.Id);
            var status = path is null ? "(null - access denied)" : path;
            if (path is null) unresolved++;
            Console.WriteLine($"  {p.ProcessName,-25}  pid={p.Id,-8}  {status}");
            p.Dispose();
        }
    }
    return unresolved == 0 ? 0 : 1;
}

var dbPath = args.Length > 0
    ? args[0]
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                   "PcUsageTracker", "history.db");

if (!File.Exists(dbPath))
{
    Console.Error.WriteLine($"DB not found: {dbPath}");
    return 1;
}

var cs = new SqliteConnectionStringBuilder
{
    DataSource = dbPath,
    Mode = SqliteOpenMode.ReadOnly,
}.ConnectionString;

using var conn = new SqliteConnection(cs);
conn.Open();

Console.WriteLine($"DB: {dbPath}");

using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "SELECT COUNT(*) FROM sessions;";
    Console.WriteLine($"Total sessions: {Convert.ToInt64(cmd.ExecuteScalar())}");
}

using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "SELECT COUNT(*) FROM sessions WHERE end_at IS NULL;";
    Console.WriteLine($"Open sessions (end_at NULL): {Convert.ToInt64(cmd.ExecuteScalar())}");
}

using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = """
        SELECT id, process_name, start_at, end_at, duration_sec
        FROM sessions
        ORDER BY id DESC
        LIMIT 20;
        """;
    using var r = cmd.ExecuteReader();
    Console.WriteLine("\nLatest 20 sessions:");
    Console.WriteLine($"  {"id",5}  {"proc",-25}  {"start (utc)",19}  {"end",19}  dur");
    while (r.Read())
    {
        var id = r.GetInt64(0);
        var proc = r.GetString(1);
        var start = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(2)).ToString("yyyy-MM-dd HH:mm:ss");
        var end = r.IsDBNull(3)
            ? "NULL".PadLeft(19)
            : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(3)).ToString("yyyy-MM-dd HH:mm:ss");
        var dur = r.IsDBNull(4) ? "NULL" : r.GetInt32(4).ToString() + "s";
        Console.WriteLine($"  {id,5}  {proc,-25}  {start}  {end}  {dur}");
    }
}

using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = """
        SELECT process_name, COALESCE(SUM(duration_sec), 0) AS total
        FROM sessions
        WHERE end_at IS NOT NULL
        GROUP BY process_name
        ORDER BY total DESC
        LIMIT 10;
        """;
    using var r = cmd.ExecuteReader();
    Console.WriteLine("\nTop 10 by total duration:");
    while (r.Read())
    {
        Console.WriteLine($"  {r.GetString(0),-25}  {r.GetInt64(1)}s");
    }
}

try
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT name, exe_path, last_seen_at FROM processes ORDER BY last_seen_at DESC LIMIT 20;";
    using var r = cmd.ExecuteReader();
    Console.WriteLine("\nProcess paths (top 20 most recent):");
    while (r.Read())
    {
        var seen = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(2)).ToLocalTime().ToString("MM-dd HH:mm");
        var exePath = r.IsDBNull(1) ? "(null)" : r.GetString(1);
        Console.WriteLine($"  [{seen}] {r.GetString(0),-25}  {exePath}");
    }
}
catch (Microsoft.Data.Sqlite.SqliteException)
{
    Console.WriteLine("\n(processes table not present — schema v1 DB)");
}

return 0;

static string? QueryExePath(uint pid)
{
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
    if (handle == IntPtr.Zero) return null;
    try
    {
        var sb = new StringBuilder(1024);
        uint size = (uint)sb.Capacity;
        return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString(0, (int)size) : null;
    }
    finally { CloseHandle(handle); }
}

[DllImport("kernel32.dll", SetLastError = true)]
static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

[DllImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool CloseHandle(IntPtr handle);

[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool QueryFullProcessImageName(IntPtr hProcess, uint flags, StringBuilder exeName, ref uint size);
