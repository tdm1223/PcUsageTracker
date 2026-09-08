using Microsoft.Data.Sqlite;
using PcUsageTracker.Core.Models;
using PcUsageTracker.Core.Sampling;

namespace PcUsageTracker.Core.Reporting;

/// <summary>
/// sessions 테이블에서 기간별 상위-N 프로세스 집계를 제공.
/// 진행 중 세션(end_at IS NULL)은 fallback으로 nowUtc 기준 duration 계산.
/// </summary>
public sealed class Aggregator
{
    public const int MaximumDailyTotalsDays = 366;

    // UNION ALL keeps both branches sargable: closed sessions use the v6 end/start index,
    // while open sessions use the partial open/start index. A COALESCE(end_at, now) predicate
    // prevents SQLite from using either index efficiently.
    const string OverlappingSessionsCte = """
        WITH overlapping AS (
          SELECT id, process_name, start_at, end_at
          FROM sessions
          WHERE end_at IS NOT NULL
            AND end_at > $from
            AND start_at < $to
          UNION ALL
          SELECT id, process_name, start_at, end_at
          FROM sessions
          WHERE end_at IS NULL
            AND $now > $from
            AND start_at < $to
        )
        """;

    readonly SqliteConnection _conn;

    public Aggregator(SqliteConnection conn)
    {
        _conn = conn ?? throw new ArgumentNullException(nameof(conn));
    }

    /// <summary>
    /// Returns individual session slices overlapping a local calendar day. Open sessions are
    /// clipped to <paramref name="nowUtc"/> and no future portion is returned.
    /// </summary>
    public IReadOnlyList<TimelineSegment> Timeline(
        DateOnly day,
        DateTimeOffset nowUtc,
        TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var (dayStart, dayEnd) = DayRange(day, zone);
        var effectiveEnd = Earlier(dayEnd, nowUtc.ToUniversalTime());
        if (effectiveEnd <= dayStart) return Array.Empty<TimelineSegment>();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = OverlappingSessionsCte + """
            SELECT
              CASE WHEN s.process_name = $idle COLLATE NOCASE THEN $idle ELSE s.process_name END AS process_name,
              MAX(s.start_at, $from) AS clipped_start,
              MIN(COALESCE(s.end_at, $now), $to) AS clipped_end,
              (SELECT p.exe_path
                 FROM processes p
                WHERE p.name = s.process_name COLLATE NOCASE
                ORDER BY (p.exe_path IS NOT NULL) DESC, p.last_seen_at DESC, p.name COLLATE BINARY
                LIMIT 1) AS exe_path,
              CASE WHEN s.process_name = $idle COLLATE NOCASE THEN '(Idle)'
                   ELSE COALESCE(NULLIF(TRIM(ar.alias), ''), s.process_name) END AS display_name,
              CASE WHEN s.process_name = $idle COLLATE NOCASE THEN NULL
                   ELSE COALESCE(c.name, other.name) END AS category_name,
              CASE WHEN s.process_name = $idle COLLATE NOCASE THEN $idleColor
                   ELSE COALESCE(ar.color_override_rgb, c.color_rgb, other.color_rgb) END AS resolved_color
            FROM overlapping s
            JOIN categories other ON other.id = $otherId
            LEFT JOIN application_rules ar ON ar.process_name = s.process_name COLLATE NOCASE
            LEFT JOIN categories c ON c.id = ar.category_id
            ORDER BY clipped_start, s.id;
            """;
        cmd.Parameters.AddWithValue("$from", dayStart.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$to", effectiveEnd.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$now", nowUtc.ToUnixTimeSeconds());
        AddPresentationParameters(cmd);

        var result = new List<TimelineSegment>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var startUnix = r.GetInt64(1);
            var endUnix = r.GetInt64(2);
            if (endUnix <= startUnix) continue;

            result.Add(new TimelineSegment(
                ProcessName: r.GetString(0),
                DisplayName: r.GetString(4),
                CategoryName: r.IsDBNull(5) ? null : r.GetString(5),
                ColorRgb: r.GetInt32(6),
                StartAt: DateTimeOffset.FromUnixTimeSeconds(startUnix),
                EndAt: DateTimeOffset.FromUnixTimeSeconds(endUnix),
                ExePath: r.IsDBNull(3) ? null : r.GetString(3)));
        }
        return result;
    }

    /// <summary>
    /// Returns exactly <paramref name="dayCount"/> local-day points ending at
    /// <paramref name="throughDay"/>, including zero-valued points for days without data.
    /// Sessions are fetched once for the complete range and split at time-zone-aware day
    /// boundaries, including 23-hour and 25-hour DST days.
    /// </summary>
    public IReadOnlyList<DailyUsagePoint> DailyTotals(
        DateOnly throughDay,
        int dayCount,
        DateTimeOffset nowUtc,
        TimeZoneInfo zone)
    {
        if (dayCount is <= 0 or > MaximumDailyTotalsDays)
            throw new ArgumentOutOfRangeException(
                nameof(dayCount),
                $"Day count must be between 1 and {MaximumDailyTotalsDays}.");
        ArgumentNullException.ThrowIfNull(zone);

        if (throughDay == DateOnly.MaxValue)
            throw new ArgumentOutOfRangeException(
                nameof(throughDay),
                "The final day must have a representable following day boundary.");

        DateOnly firstDay;
        try
        {
            firstDay = throughDay.AddDays(1 - dayCount);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ArgumentOutOfRangeException(nameof(dayCount), "The requested date range exceeds DateOnly limits.");
        }

        var boundaries = new DateTimeOffset[dayCount + 1];
        try
        {
            for (var i = 0; i <= dayCount; i++)
                boundaries[i] = StartOfLocalDay(firstDay.AddDays(i), zone);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ArgumentOutOfRangeException(
                nameof(throughDay),
                "The requested local date range cannot be represented in this time zone.");
        }

        var active = new long[dayCount];
        var idle = new long[dayCount];
        var rangeStartUnix = boundaries[0].ToUnixTimeSeconds();
        var rangeEnd = Earlier(boundaries[^1], nowUtc.ToUniversalTime());
        var rangeEndUnix = rangeEnd.ToUnixTimeSeconds();

        if (rangeEndUnix > rangeStartUnix)
        {
            foreach (var session in ReadSessionSlices(rangeStartUnix, rangeEndUnix, nowUtc.ToUnixTimeSeconds()))
            {
                for (var dayIndex = 0; dayIndex < dayCount; dayIndex++)
                {
                    var dayStartUnix = boundaries[dayIndex].ToUnixTimeSeconds();
                    var dayEndUnix = Math.Min(boundaries[dayIndex + 1].ToUnixTimeSeconds(), rangeEndUnix);
                    if (session.EndUnix <= dayStartUnix) break;
                    if (session.StartUnix >= dayEndUnix) continue;

                    var seconds = Math.Min(session.EndUnix, dayEndUnix) - Math.Max(session.StartUnix, dayStartUnix);
                    if (seconds <= 0) continue;
                    if (session.IsIdle) idle[dayIndex] += seconds;
                    else active[dayIndex] += seconds;
                }
            }
        }

        var result = new DailyUsagePoint[dayCount];
        for (var i = 0; i < dayCount; i++)
        {
            result[i] = new DailyUsagePoint(
                firstDay.AddDays(i),
                ToDurationSeconds(active[i]),
                ToDurationSeconds(idle[i]));
        }
        return result;
    }

    /// <summary>Process totals for one local calendar day, with current aliases and categories.</summary>
    public IReadOnlyList<ReportEntry> ApplicationTotals(
        DateOnly day,
        DateTimeOffset nowUtc,
        TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var (from, to) = DayRange(day, zone);
        return TopN(from, Earlier(to, nowUtc.ToUniversalTime()), nowUtc);
    }

    /// <summary>
    /// Category totals for one local calendar day. Rules are joined when this method is called,
    /// so edits immediately reclassify historical sessions. Idle is returned as a fixed entry.
    /// </summary>
    public IReadOnlyList<CategoryUsage> CategoryTotals(
        DateOnly day,
        DateTimeOffset nowUtc,
        TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var (dayStart, dayEnd) = DayRange(day, zone);
        var effectiveEnd = Earlier(dayEnd, nowUtc.ToUniversalTime());
        if (effectiveEnd <= dayStart) return Array.Empty<CategoryUsage>();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = OverlappingSessionsCte + """
            , clipped AS (
              SELECT
                CASE WHEN s.process_name = $idle COLLATE NOCASE THEN 1 ELSE 0 END AS is_idle,
                CASE WHEN s.process_name = $idle COLLATE NOCASE THEN NULL
                     ELSE COALESCE(c.name, other.name) END AS category_name,
                CASE WHEN s.process_name = $idle COLLATE NOCASE THEN $idleColor
                     ELSE COALESCE(c.color_rgb, other.color_rgb) END AS category_color,
                MAX(s.start_at, $from) AS clipped_start,
                MIN(COALESCE(s.end_at, $now), $to) AS clipped_end
              FROM overlapping s
              JOIN categories other ON other.id = $otherId
              LEFT JOIN application_rules ar ON ar.process_name = s.process_name COLLATE NOCASE
              LEFT JOIN categories c ON c.id = ar.category_id
            )
            SELECT category_name, SUM(clipped_end - clipped_start), category_color, is_idle
            FROM clipped
            WHERE clipped_end > clipped_start
            GROUP BY is_idle, category_name COLLATE NOCASE, category_color
            ORDER BY SUM(clipped_end - clipped_start) DESC, category_name COLLATE NOCASE;
            """;
        cmd.Parameters.AddWithValue("$from", dayStart.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$to", effectiveEnd.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$now", nowUtc.ToUnixTimeSeconds());
        AddPresentationParameters(cmd);

        var result = new List<CategoryUsage>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(new CategoryUsage(
                CategoryName: r.IsDBNull(0) ? null : r.GetString(0),
                TotalSeconds: ToDurationSeconds(r.GetInt64(1)),
                ColorRgb: r.GetInt32(2),
                IsIdle: r.GetInt32(3) != 0));
        }
        return result;
    }

    /// <summary>지정 기간 [fromUtc, toUtc)의 프로세스별 누적 상위 N개.</summary>
    public IReadOnlyList<ReportEntry> TopN(DateTimeOffset fromUtc, DateTimeOffset toUtc, DateTimeOffset nowUtc, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return ProcessTotals(fromUtc, toUtc, nowUtc, limit);
    }

    /// <summary>전 기간 누적 상위 N개.</summary>
    public IReadOnlyList<ReportEntry> AllTime(DateTimeOffset nowUtc, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return AllTimeTotals(nowUtc, limit);
    }

    /// <summary>지정 기간 [fromUtc, toUtc)의 프로세스별 누적 — 전체 행(무제한). total DESC 정렬.</summary>
    public IReadOnlyList<ReportEntry> TopN(DateTimeOffset fromUtc, DateTimeOffset toUtc, DateTimeOffset nowUtc)
    {
        return ProcessTotals(fromUtc, toUtc, nowUtc, limit: null);
    }

    /// <summary>전 기간 누적 — 전체 행(무제한). total DESC 정렬.</summary>
    public IReadOnlyList<ReportEntry> AllTime(DateTimeOffset nowUtc)
    {
        return AllTimeTotals(nowUtc, limit: null);
    }

    IReadOnlyList<ReportEntry> ProcessTotals(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        DateTimeOffset nowUtc,
        int? limit)
    {
        if (toUtc <= fromUtc) return Array.Empty<ReportEntry>();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = OverlappingSessionsCte + """
            , clipped AS (
              SELECT process_name,
                     MAX(start_at, $from) AS clipped_start,
                     MIN(COALESCE(end_at, $now), $to) AS clipped_end
              FROM overlapping
            ),
            summed AS (
              SELECT
                MIN(CASE WHEN process_name = $idle COLLATE NOCASE THEN $idle ELSE process_name END COLLATE BINARY)
                  AS process_name,
                SUM(clipped_end - clipped_start) AS total
              FROM clipped
              WHERE clipped_end > clipped_start
              GROUP BY CASE WHEN process_name = $idle COLLATE NOCASE THEN $idle ELSE process_name END COLLATE NOCASE
            )
            SELECT s.process_name, s.total,
              (SELECT p.exe_path
                 FROM processes p
                WHERE p.name = s.process_name COLLATE NOCASE
                ORDER BY (p.exe_path IS NOT NULL) DESC, p.last_seen_at DESC, p.name COLLATE BINARY
                LIMIT 1) AS exe_path,
              CASE WHEN s.process_name = $idle COLLATE NOCASE THEN '(Idle)'
                   ELSE COALESCE(NULLIF(TRIM(ar.alias), ''), s.process_name) END AS display_name,
              CASE WHEN s.process_name = $idle COLLATE NOCASE THEN NULL
                   ELSE COALESCE(c.name, other.name) END AS category_name,
              CASE WHEN s.process_name = $idle COLLATE NOCASE THEN $idleColor
                   ELSE COALESCE(ar.color_override_rgb, c.color_rgb, other.color_rgb) END AS resolved_color
            FROM summed s
            JOIN categories other ON other.id = $otherId
            LEFT JOIN application_rules ar ON ar.process_name = s.process_name COLLATE NOCASE
            LEFT JOIN categories c ON c.id = ar.category_id
            ORDER BY s.total DESC, s.process_name COLLATE NOCASE
            """ + (limit is null ? ";" : " LIMIT $limit;");
        cmd.Parameters.AddWithValue("$from", fromUtc.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$to", toUtc.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$now", nowUtc.ToUnixTimeSeconds());
        if (limit is { } value) cmd.Parameters.AddWithValue("$limit", value);
        AddPresentationParameters(cmd);

        var result = limit is { } capacity ? new List<ReportEntry>(capacity) : new List<ReportEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(ReadEntry(r));
        return result;
    }

    IReadOnlyList<ReportEntry> AllTimeTotals(DateTimeOffset nowUtc, int? limit)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            WITH normalized AS (
              SELECT
                CASE WHEN process_name = $idle COLLATE NOCASE THEN $idle ELSE process_name END AS process_name,
                MAX(COALESCE(end_at, $now) - start_at, 0) AS duration
              FROM sessions
            ),
            summed AS (
              SELECT MIN(process_name COLLATE BINARY) AS process_name, SUM(duration) AS total
              FROM normalized
              WHERE duration > 0
              GROUP BY process_name COLLATE NOCASE
            )
            SELECT s.process_name, s.total,
              (SELECT p.exe_path
                 FROM processes p
                WHERE p.name = s.process_name COLLATE NOCASE
                ORDER BY (p.exe_path IS NOT NULL) DESC, p.last_seen_at DESC, p.name COLLATE BINARY
                LIMIT 1) AS exe_path,
              CASE WHEN s.process_name = $idle COLLATE NOCASE THEN '(Idle)'
                   ELSE COALESCE(NULLIF(TRIM(ar.alias), ''), s.process_name) END AS display_name,
              CASE WHEN s.process_name = $idle COLLATE NOCASE THEN NULL
                   ELSE COALESCE(c.name, other.name) END AS category_name,
              CASE WHEN s.process_name = $idle COLLATE NOCASE THEN $idleColor
                   ELSE COALESCE(ar.color_override_rgb, c.color_rgb, other.color_rgb) END AS resolved_color
            FROM summed s
            JOIN categories other ON other.id = $otherId
            LEFT JOIN application_rules ar ON ar.process_name = s.process_name COLLATE NOCASE
            LEFT JOIN categories c ON c.id = ar.category_id
            ORDER BY s.total DESC, s.process_name COLLATE NOCASE
            """ + (limit is null ? ";" : " LIMIT $limit;");
        cmd.Parameters.AddWithValue("$now", nowUtc.ToUnixTimeSeconds());
        if (limit is { } value) cmd.Parameters.AddWithValue("$limit", value);
        AddPresentationParameters(cmd);

        var result = limit is { } capacity ? new List<ReportEntry>(capacity) : new List<ReportEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(ReadEntry(r));
        return result;
    }

    IEnumerable<SessionSlice> ReadSessionSlices(long fromUnix, long toUnix, long nowUnix)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = OverlappingSessionsCte + """
            SELECT
              CASE WHEN process_name = $idle COLLATE NOCASE THEN 1 ELSE 0 END AS is_idle,
              MAX(start_at, $from) AS clipped_start,
              MIN(COALESCE(end_at, $now), $to) AS clipped_end
            FROM overlapping
            ORDER BY clipped_start, id;
            """;
        cmd.Parameters.AddWithValue("$idle", IdleSentinel.Name);
        cmd.Parameters.AddWithValue("$from", fromUnix);
        cmd.Parameters.AddWithValue("$to", toUnix);
        cmd.Parameters.AddWithValue("$now", nowUnix);

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var startUnix = r.GetInt64(1);
            var endUnix = r.GetInt64(2);
            if (endUnix > startUnix)
                yield return new SessionSlice(r.GetInt32(0) != 0, startUnix, endUnix);
        }
    }

    readonly record struct SessionSlice(bool IsIdle, long StartUnix, long EndUnix);

    static int ToDurationSeconds(long seconds) => checked((int)seconds);

    static DateTimeOffset Earlier(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;

    static void AddPresentationParameters(SqliteCommand cmd)
    {
        cmd.Parameters.AddWithValue("$idle", IdleSentinel.Name);
        cmd.Parameters.AddWithValue("$idleColor", DefaultApplicationCategories.IdleColor);
        cmd.Parameters.AddWithValue("$otherId", DefaultApplicationCategories.OtherId);
    }

    static ReportEntry ReadEntry(SqliteDataReader r) => new(
        processName: r.GetString(0),
        totalSeconds: Convert.ToInt32(r.GetInt64(1)),
        exePath: r.IsDBNull(2) ? null : r.GetString(2),
        displayName: r.GetString(3),
        categoryName: r.IsDBNull(4) ? null : r.GetString(4),
        colorRgb: r.IsDBNull(5) ? null : r.GetInt32(5));

    /// <summary>
    /// Returns the UTC instants bounding a local calendar day in <paramref name="zone"/>.
    /// The duration can be 23 or 25 hours across daylight-saving transitions.
    /// </summary>
    public static (DateTimeOffset fromUtc, DateTimeOffset toUtc) DayRange(DateOnly day, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        DateOnly nextDay;
        try
        {
            nextDay = day.AddDays(1);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ArgumentOutOfRangeException(nameof(day), "A day range cannot start at DateOnly.MaxValue.");
        }

        try
        {
            return (StartOfLocalDay(day, zone), StartOfLocalDay(nextDay, zone));
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ArgumentOutOfRangeException(
                nameof(day),
                "The local day cannot be represented in this time zone.");
        }
    }

    static DateTimeOffset StartOfLocalDay(DateOnly day, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);

        // A few zones historically advanced their clock at midnight. In that case the civil
        // day begins at the first representable wall-clock time rather than throwing.
        while (zone.IsInvalidTime(local))
            local = local.AddMinutes(1);

        TimeSpan offset;
        if (zone.IsAmbiguousTime(local))
        {
            // The larger offset maps to the earlier UTC instant, so a repeated midnight is not
            // accidentally truncated from the day.
            offset = zone.GetAmbiguousTimeOffsets(local).Max();
        }
        else
        {
            offset = zone.GetUtcOffset(local);
        }

        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    /// <summary>local time 기준 오늘 00:00 ~ 내일 00:00 을 UTC 범위로 반환.</summary>
    public static (DateTimeOffset fromUtc, DateTimeOffset toUtc) TodayRange(DateTimeOffset nowUtc)
    {
        var local = TimeZoneInfo.ConvertTime(nowUtc, TimeZoneInfo.Local);
        return DayRange(DateOnly.FromDateTime(local.DateTime), TimeZoneInfo.Local);
    }

    /// <summary>local time 기준 이번 주(월요일 00:00) ~ 다음 주 월요일 00:00 을 UTC 범위로 반환.</summary>
    public static (DateTimeOffset fromUtc, DateTimeOffset toUtc) ThisWeekRange(DateTimeOffset nowUtc)
    {
        var local = TimeZoneInfo.ConvertTime(nowUtc, TimeZoneInfo.Local);
        var day = DateOnly.FromDateTime(local.DateTime);
        int offset = ((int)day.DayOfWeek + 6) % 7; // Monday=0 ... Sunday=6
        var monday = day.AddDays(-offset);
        return (StartOfLocalDay(monday, TimeZoneInfo.Local), StartOfLocalDay(monday.AddDays(7), TimeZoneInfo.Local));
    }

    /// <summary>local time 기준 이번 달 1일 00:00 ~ 다음 달 1일 00:00 을 UTC 범위로 반환.</summary>
    public static (DateTimeOffset fromUtc, DateTimeOffset toUtc) ThisMonthRange(DateTimeOffset nowUtc)
    {
        var local = TimeZoneInfo.ConvertTime(nowUtc, TimeZoneInfo.Local);
        var first = new DateOnly(local.Year, local.Month, 1);
        return (StartOfLocalDay(first, TimeZoneInfo.Local), StartOfLocalDay(first.AddMonths(1), TimeZoneInfo.Local));
    }
}
