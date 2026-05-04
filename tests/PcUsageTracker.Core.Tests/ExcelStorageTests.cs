using FluentAssertions;
using Microsoft.Data.Sqlite;
using PcUsageTracker.Core.Storage;

namespace PcUsageTracker.Core.Tests;

public class ExcelStorageTests : IDisposable
{
    readonly string _dbA;
    readonly string _dbB;
    readonly string _xlsx;

    public ExcelStorageTests()
    {
        var tag = Guid.NewGuid().ToString("N");
        _dbA = Path.Combine(Path.GetTempPath(), $"pcut-xlsx-a-{tag}.db");
        _dbB = Path.Combine(Path.GetTempPath(), $"pcut-xlsx-b-{tag}.db");
        _xlsx = Path.Combine(Path.GetTempPath(), $"pcut-xlsx-{tag}.xlsx");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var db in new[] { _dbA, _dbB })
            foreach (var suffix in new[] { "", "-shm", "-wal" })
                try { File.Delete(db + suffix); } catch { }
        try { File.Delete(_xlsx); } catch { }
    }

    static DateTimeOffset T(int seconds) => DateTimeOffset.FromUnixTimeSeconds(1_800_000_000 + seconds);

    [Fact]
    public void roundtrip_replace_preserves_all_session_fields()
    {
        using (var src = new SqliteStore(_dbA))
        {
            var id1 = src.Open("chrome", T(0));
            src.Close(id1, T(60));
            var id2 = src.Open("code", T(100));
            src.Close(id2, T(250));
            src.UpsertProcessPath("chrome", @"C:\Apps\chrome.exe", T(0));

            ExcelStorage.Export(src, _xlsx).Should().Be(2);
        }
        SqliteConnection.ClearAllPools();

        using var dst = new SqliteStore(_dbB);
        var imported = ExcelStorage.Import(dst, _xlsx, ImportMode.Replace);
        imported.Should().Be(2);

        var rows = dst.EnumerateSessions().ToList();
        rows.Should().HaveCount(2);

        var chrome = rows.Single(r => r.ProcessName == "chrome");
        chrome.StartAtUnix.Should().Be(T(0).ToUnixTimeSeconds());
        chrome.EndAtUnix.Should().Be(T(60).ToUnixTimeSeconds());
        chrome.DurationSec.Should().Be(60);
        chrome.ExePath.Should().Be(@"C:\Apps\chrome.exe");

        var code = rows.Single(r => r.ProcessName == "code");
        code.DurationSec.Should().Be(150);
    }

    [Fact]
    public void roundtrip_preserves_open_session_with_null_end()
    {
        using (var src = new SqliteStore(_dbA))
        {
            src.Open("chrome", T(0)); // open — end_at NULL
            ExcelStorage.Export(src, _xlsx).Should().Be(1);
        }
        SqliteConnection.ClearAllPools();

        using var dst = new SqliteStore(_dbB);
        ExcelStorage.Import(dst, _xlsx, ImportMode.Replace);

        var rows = dst.EnumerateSessions().ToList();
        rows.Should().HaveCount(1);
        rows[0].EndAtUnix.Should().BeNull();
        rows[0].DurationSec.Should().BeNull();
    }

    [Fact]
    public void append_mode_keeps_existing_rows()
    {
        // 소스에 2 row 익스포트
        using (var src = new SqliteStore(_dbA))
        {
            var id1 = src.Open("chrome", T(0)); src.Close(id1, T(10));
            var id2 = src.Open("code", T(20)); src.Close(id2, T(30));
            ExcelStorage.Export(src, _xlsx).Should().Be(2);
        }
        SqliteConnection.ClearAllPools();

        // 대상에 이미 1 row가 있음
        using var dst = new SqliteStore(_dbB);
        var existing = dst.Open("notepad", T(500));
        dst.Close(existing, T(560));

        ExcelStorage.Import(dst, _xlsx, ImportMode.Append).Should().Be(2);

        var rows = dst.EnumerateSessions().ToList();
        rows.Should().HaveCount(3);
        rows.Select(r => r.ProcessName).Should().Contain(new[] { "notepad", "chrome", "code" });
    }

    [Fact]
    public void replace_mode_deletes_existing_then_inserts()
    {
        using (var src = new SqliteStore(_dbA))
        {
            var id = src.Open("chrome", T(0));
            src.Close(id, T(10));
            ExcelStorage.Export(src, _xlsx).Should().Be(1);
        }
        SqliteConnection.ClearAllPools();

        using var dst = new SqliteStore(_dbB);
        var existing = dst.Open("notepad", T(500));
        dst.Close(existing, T(560));
        // 사용자 추가 exclusion이 있다고 가정
        dst.AddExclusion("MyExcluded", "user-hidden", T(0));

        ExcelStorage.Import(dst, _xlsx, ImportMode.Replace).Should().Be(1);

        var rows = dst.EnumerateSessions().ToList();
        rows.Should().HaveCount(1);
        rows[0].ProcessName.Should().Be("chrome");

        // exclusions 보존 검증
        dst.IsExcluded("MyExcluded").Should().BeTrue();
        dst.IsExcluded("StartMenuExperienceHost").Should().BeTrue();
    }

    [Fact]
    public void export_creates_directory_if_missing()
    {
        var subDir = Path.Combine(Path.GetTempPath(), $"pcut-xlsx-subdir-{Guid.NewGuid():N}");
        var nestedXlsx = Path.Combine(subDir, "nested", "out.xlsx");
        try
        {
            using var src = new SqliteStore(_dbA);
            var id = src.Open("foo", T(0));
            src.Close(id, T(5));
            ExcelStorage.Export(src, nestedXlsx);
            File.Exists(nestedXlsx).Should().BeTrue();
        }
        finally
        {
            try { Directory.Delete(subDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void import_throws_on_missing_file()
    {
        using var dst = new SqliteStore(_dbB);
        var act = () => ExcelStorage.Import(dst, Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.xlsx"), ImportMode.Append);
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void import_throws_on_invalid_iso_date()
    {
        // 직접 시트를 만들어 잘못된 날짜를 넣고 import 시 InvalidDataException 기대
        using (var wb = new ClosedXML.Excel.XLWorkbook())
        {
            var ws = wb.Worksheets.Add("Sessions");
            ws.Cell(1, 1).Value = "ProcessName";
            ws.Cell(1, 2).Value = "StartUtc";
            ws.Cell(1, 3).Value = "EndUtc";
            ws.Cell(1, 4).Value = "DurationSec";
            ws.Cell(1, 5).Value = "ExePath";
            ws.Cell(2, 1).Value = "chrome";
            ws.Cell(2, 2).Value = "not-a-date";
            ws.Cell(2, 3).Value = "";
            wb.SaveAs(_xlsx);
        }

        using var dst = new SqliteStore(_dbB);
        var act = () => ExcelStorage.Import(dst, _xlsx, ImportMode.Append);
        act.Should().Throw<InvalidDataException>();
    }
}
