using ClosedXML.Excel;

namespace PcUsageTracker.Core.Storage;

/// <summary>
/// SqliteStore와 .xlsx 사이의 export/import. 단일 시트 'Sessions', 5컬럼:
/// ProcessName, StartUtc(ISO 8601), EndUtc(빈칸=open), DurationSec, ExePath.
/// 시간은 항상 UTC ISO 8601 — 사용자가 시트를 직접 수정해도 unambiguous.
/// </summary>
public static class ExcelStorage
{
    const string SheetName = "Sessions";

    static readonly string[] Headers =
    {
        "ProcessName", "StartUtc", "EndUtc", "DurationSec", "ExePath",
    };

    /// <summary>
    /// 모든 sessions를 xlsx 파일로 내보낸다. 디렉터리 미존재 시 자동 생성.
    /// 내보낸 row 수 반환(헤더 제외).
    /// </summary>
    public static int Export(SqliteStore store, string xlsxPath)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(xlsxPath);

        var dir = Path.GetDirectoryName(xlsxPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(SheetName);

        for (int i = 0; i < Headers.Length; i++)
            ws.Cell(1, i + 1).Value = Headers[i];
        ws.Range(1, 1, 1, Headers.Length).Style.Font.Bold = true;

        int row = 2;
        int written = 0;
        foreach (var s in store.EnumerateSessions())
        {
            ws.Cell(row, 1).Value = s.ProcessName;
            ws.Cell(row, 2).Value = ToIso(s.StartAtUnix);
            ws.Cell(row, 3).Value = s.EndAtUnix is { } e ? ToIso(e) : string.Empty;
            if (s.DurationSec is { } d) ws.Cell(row, 4).Value = d;
            else ws.Cell(row, 4).Value = Blank.Value;
            ws.Cell(row, 5).Value = s.ExePath ?? string.Empty;
            row++;
            written++;
        }

        ws.Columns().AdjustToContents();
        wb.SaveAs(xlsxPath);
        return written;
    }

    /// <summary>
    /// xlsx에서 sessions를 읽어 store에 import. 헤더 행은 자동 스킵.
    /// 모드별 동작:
    ///   Append  — 기존 데이터 유지, 신규 row 추가
    ///   Replace — sessions/processes 전체 wipe 후 신규 row insert (excluded_processes는 보존)
    /// 삽입된 row 수 반환.
    /// 파싱 실패 row는 스킵하지 않고 즉시 throw — 사용자가 잘못된 파일임을 알 수 있게.
    /// </summary>
    public static int Import(SqliteStore store, string xlsxPath, ImportMode mode)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(xlsxPath);
        if (!File.Exists(xlsxPath))
            throw new FileNotFoundException("Excel file not found", xlsxPath);

        using var wb = new XLWorkbook(xlsxPath);
        var ws = wb.Worksheets.FirstOrDefault(w => w.Name == SheetName) ?? wb.Worksheet(1);

        // 첫 행이 헤더인지 판정: 첫 셀이 'ProcessName' 이면 헤더로 간주.
        var firstUsedRow = ws.FirstRowUsed();
        if (firstUsedRow is null)
        {
            // 빈 시트라도 Replace 모드면 기존 데이터는 wipe.
            if (mode == ImportMode.Replace) store.ClearAllSessions();
            return 0;
        }

        var startRow = firstUsedRow.RowNumber();
        var endRow = ws.LastRowUsed()!.RowNumber();
        if (string.Equals(ws.Cell(startRow, 1).GetString(), "ProcessName", StringComparison.OrdinalIgnoreCase))
            startRow++;

        var parsed = new List<(string Name, DateTimeOffset Start, DateTimeOffset? End, string? Path)>(endRow - startRow + 1);
        for (int r = startRow; r <= endRow; r++)
        {
            var name = ws.Cell(r, 1).GetString().Trim();
            var startStr = ws.Cell(r, 2).GetString().Trim();
            var endStr = ws.Cell(r, 3).GetString().Trim();
            var pathStr = ws.Cell(r, 5).GetString().Trim();

            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(startStr))
                continue; // 완전 공백 행은 스킵

            if (string.IsNullOrEmpty(name))
                throw new InvalidDataException($"Row {r}: ProcessName is empty");
            var start = ParseIso(startStr, r, "StartUtc");
            DateTimeOffset? end = string.IsNullOrEmpty(endStr) ? null : ParseIso(endStr, r, "EndUtc");

            parsed.Add((name, start, end, string.IsNullOrEmpty(pathStr) ? null : pathStr));
        }

        if (mode == ImportMode.Replace)
            store.ClearAllSessions();

        foreach (var p in parsed)
            store.ImportSession(p.Name, p.Start, p.End, p.Path);

        return parsed.Count;
    }

    static string ToIso(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToString("yyyy-MM-ddTHH:mm:ssZ");

    static DateTimeOffset ParseIso(string s, int row, string column)
    {
        if (DateTimeOffset.TryParse(
                s,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dt))
            return dt;
        throw new InvalidDataException($"Row {row}: {column} '{s}' is not a valid ISO 8601 datetime");
    }
}

public enum ImportMode
{
    Append,
    Replace,
}
