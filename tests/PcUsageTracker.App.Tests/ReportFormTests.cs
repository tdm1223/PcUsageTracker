using System.Reflection;
using System.Runtime.ExceptionServices;
using PcUsageTracker.App;
using PcUsageTracker.Core.Reporting;
using PcUsageTracker.Core.Sampling;
using PcUsageTracker.Core.Storage;
using Xunit;

namespace PcUsageTracker.App.Tests;

public class ReportFormTests
{
    const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void Daily_chart_centers_an_earlier_then_a_later_selected_day()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var folder = Path.Combine(Path.GetTempPath(), $"pcut-chart-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            try
            {
                using var store = new SqliteStore(Path.Combine(folder, "history.db"));
                using var form = new ReportForm(new SystemClock(), store, () => { });
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-30000, -30000);
                form.Show();
                var chart = (DailyUsageChartControl)typeof(ReportForm).GetField("_dailyChart", InstanceFields)!.GetValue(form)!;
                var picker = (DateTimePicker)typeof(ReportForm).GetField("_dashboardDatePicker", InstanceFields)!.GetValue(form)!;
                IReadOnlyList<DailyUsagePoint> Points() => (IReadOnlyList<DailyUsagePoint>)typeof(DailyUsageChartControl)
                    .GetField("_points", InstanceFields)!.GetValue(chart)!;
                void WaitFor(Func<bool> condition)
                {
                    var until = DateTime.UtcNow.AddSeconds(10);
                    while (!condition() && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(20); }
                    Assert.True(condition(), "Timed out waiting for the dashboard refresh.");
                }
                void ClickIndex(int index)
                {
                    var scale = chart.DeviceDpi / 96f;
                    var left = 42 * scale;
                    var width = chart.ClientSize.Width - 50 * scale;
                    var x = (int)(left + width * (index + .5) / Points().Count);
                    typeof(DailyUsageChartControl).GetMethod("OnMouseDown", InstanceFields)!
                        .Invoke(chart, [new MouseEventArgs(MouseButtons.Left, 1, x, (int)(20 * scale), 0)]);
                }
                DateOnly? AppliedDay() => (DateOnly?)typeof(ReportForm).GetField("_dashboardAppliedDay", InstanceFields)!.GetValue(form);

                WaitFor(() => Points().Count == 7);
                var earlier = Points()[1].Date;
                ClickIndex(1);
                WaitFor(() => AppliedDay() == earlier);
                Assert.Equal(earlier, DateOnly.FromDateTime(picker.Value));
                Assert.Equal(earlier, Points()[3].Date);
                var later = Points()[4].Date;
                ClickIndex(4);
                WaitFor(() => AppliedDay() == later);
                Assert.Equal(later, DateOnly.FromDateTime(picker.Value));
                Assert.Equal(later, Points()[3].Date);
            }
            catch (Exception exception) { error = exception; }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { Directory.Delete(folder, true); } catch (IOException) { }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    [Fact]
    public void Search_and_sort_keep_the_original_share_and_timeline_selects_the_matching_row()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var folder = Path.Combine(Path.GetTempPath(), $"pcut-ui-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            try
            {
                using var store = new SqliteStore(Path.Combine(folder, "history.db"));
                using var form = new ReportForm(new SystemClock(), store, () => { });
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-30000, -30000);
                form.Show();
                var grid = (DataGridView)typeof(ReportForm).GetField("_dashboardGrid", InstanceFields)!.GetValue(form)!;
                var options = (Dictionary<DataGridView, (TextBox Search, ComboBox Sort)>)typeof(ReportForm)
                    .GetField("_gridOptions", InstanceFields)!.GetValue(form)!;
                ReportEntry[] entries = [new("fake-beta", 900, displayName: "Beta"), new("fake-alpha", 2700, displayName: "Alpha")];
                typeof(ReportForm).GetMethod("Populate", InstanceFields)!.Invoke(form, [grid, entries]);
                options[grid].Search.Text = "beta";
                Assert.Single(grid.Rows.Cast<DataGridViewRow>());
                Assert.Equal("25.0%", grid.Rows[0].Cells["Share"].Value);
                options[grid].Search.Text = "";
                options[grid].Sort.SelectedIndex = 3;
                Assert.Equal("Beta", grid.Rows[0].Cells["Process"].Value);

                var timeline = (TimelineControl)typeof(ReportForm).GetField("_timeline", InstanceFields)!.GetValue(form)!;
                var start = new DateTimeOffset(2026, 11, 10, 2, 0, 0, TimeSpan.Zero);
                timeline.SetData(new DateOnly(2026, 11, 10), TimeZoneInfo.Utc,
                    [new TimelineSegment("fake-alpha", "Alpha", "Work", 0x123456, start, start.AddHours(1))]);
                var margin = 10d * timeline.DeviceDpi / 96d;
                var click = (int)(margin + (timeline.Width - 2 * margin) * 2.5 / 24);
                var mouse = new MouseEventArgs(MouseButtons.Left, 1, click, 25, 0);
                typeof(TimelineControl).GetMethod("OnMouseDown", InstanceFields)!.Invoke(timeline, [mouse]);
                typeof(TimelineControl).GetMethod("OnMouseUp", InstanceFields)!.Invoke(timeline, [mouse]);
                Assert.Single(grid.SelectedRows.Cast<DataGridViewRow>());
                Assert.Equal("Alpha", grid.SelectedRows[0].Cells["Process"].Value);
            }
            catch (Exception exception) { error = exception; }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { Directory.Delete(folder, true); } catch (IOException) { }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
