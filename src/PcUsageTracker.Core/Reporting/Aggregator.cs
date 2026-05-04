using Microsoft.Data.Sqlite;

namespace PcUsageTracker.Core.Reporting;

/// <summary>
/// sessions 테이블에서 기간별 상위-N 프로세스 집계를 제공.
/// 진행 중 세션(end_at IS NULL)은 fallback으로 nowUtc 기준 duration 계산.
/// </summary>
public sealed class Aggregator
{
    readonly SqliteConnection _conn;

    public Aggregator(SqliteConnection conn)
    {
        _conn = conn ?? throw new ArgumentNullException(nameof(conn));
    }

    /// <summary>지정 기간 [fromUtc, toUtc)의 프로세스별 누적 상위 N개.</summary>
    public IReadOnlyList<ReportEntry> TopN(DateTimeOffset fromUtc, DateTimeOffset toUtc, DateTimeOffset nowUtc, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        if (toUtc <= fromUtc) return Array.Empty<ReportEntry>();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            WITH clipped AS (
              SELECT
                process_name,
                MAX(start_at, $from) AS s,
                MIN(COALESCE(end_at, $now), $to) AS e
              FROM sessions
              WHERE start_at < $to
                AND COALESCE(end_at, $now) > $from
            ),
            summed AS (
              SELECT process_name, SUM(e - s) AS total
              FROM clipped
              WHERE e > s
              GROUP BY process_name
            )
            SELECT s.process_name, s.total, p.exe_path
            FROM summed s
            LEFT JOIN processes p ON p.name = s.process_name
            ORDER BY s.total DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$from", fromUtc.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$to", toUtc.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$now", nowUtc.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$limit", limit);

        var result = new List<ReportEntry>(limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var path = r.IsDBNull(2) ? null : r.GetString(2);
            result.Add(new ReportEntry(r.GetString(0), Convert.ToInt32(r.GetInt64(1)), path));
        }
        return result;
    }

    /// <summary>전 기간 누적 상위 N개.</summary>
    public IReadOnlyList<ReportEntry> AllTime(DateTimeOffset nowUtc, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            WITH summed AS (
              SELECT process_name, SUM(COALESCE(end_at, $now) - start_at) AS total
              FROM sessions
              GROUP BY process_name
            )
            SELECT s.process_name, s.total, p.exe_path
            FROM summed s
            LEFT JOIN processes p ON p.name = s.process_name
            ORDER BY s.total DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$now", nowUtc.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$limit", limit);

        var result = new List<ReportEntry>(limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var path = r.IsDBNull(2) ? null : r.GetString(2);
            result.Add(new ReportEntry(r.GetString(0), Convert.ToInt32(r.GetInt64(1)), path));
        }
        return result;
    }

    /// <summary>local time 기준 오늘 00:00 ~ 내일 00:00 을 UTC 범위로 반환.</summary>
    public static (DateTimeOffset fromUtc, DateTimeOffset toUtc) TodayRange(DateTimeOffset nowUtc)
    {
        var local = nowUtc.ToLocalTime();
        var startLocal = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, local.Offset);
        var endLocal = startLocal.AddDays(1);
        return (startLocal.ToUniversalTime(), endLocal.ToUniversalTime());
    }

    /// <summary>local time 기준 이번 주(월요일 00:00) ~ 다음 주 월요일 00:00 을 UTC 범위로 반환.</summary>
    public static (DateTimeOffset fromUtc, DateTimeOffset toUtc) ThisWeekRange(DateTimeOffset nowUtc)
    {
        var local = nowUtc.ToLocalTime();
        int offset = ((int)local.DayOfWeek + 6) % 7; // Monday=0 ... Sunday=6
        var mondayLocal = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, local.Offset)
                           .AddDays(-offset);
        var nextMondayLocal = mondayLocal.AddDays(7);
        return (mondayLocal.ToUniversalTime(), nextMondayLocal.ToUniversalTime());
    }

    /// <summary>local time 기준 이번 달 1일 00:00 ~ 다음 달 1일 00:00 을 UTC 범위로 반환.</summary>
    public static (DateTimeOffset fromUtc, DateTimeOffset toUtc) ThisMonthRange(DateTimeOffset nowUtc)
    {
        var local = nowUtc.ToLocalTime();
        var startLocal = new DateTimeOffset(local.Year, local.Month, 1, 0, 0, 0, local.Offset);
        var nextLocal = startLocal.AddMonths(1);
        return (startLocal.ToUniversalTime(), nextLocal.ToUniversalTime());
    }
}
