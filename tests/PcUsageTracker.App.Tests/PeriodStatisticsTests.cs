using System.Reflection;
using System.Runtime.ExceptionServices;
using PcUsageTracker.App;
using PcUsageTracker.Core.Reporting;
using PcUsageTracker.Core.Sampling;
using PcUsageTracker.Core.Storage;
using Xunit;

namespace PcUsageTracker.App.Tests;

public class PeriodStatisticsTests
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void Period_view_combines_configured_groups_and_can_show_individual_processes()
    {
        RunOnSta(root =>
        {
            using var store = new SqliteStore(Path.Combine(root, "history.db"));
            var clock = new SystemClock();
            var localYesterday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, TimeZoneInfo.Local).DateTime).AddDays(-1);
            var (start, _) = Aggregator.DayRange(localYesterday, TimeZoneInfo.Local);
            var a = store.Open("test-alpha", start.AddHours(1)); store.Close(a, start.AddHours(1).AddMinutes(10));
            var b = store.Open("test-helper", start.AddHours(2)); store.Close(b, start.AddHours(2).AddMinutes(5));
            store.UpsertProcessPath("test-alpha", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"), start);
            ApplicationGroups.Save(store, [new ApplicationGroup("Test group", ["test-alpha", "test-helper"])]);
            using var host = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), Size = new Size(1100, 700) };
            using var iconCache = new AsyncIconCache(Path.Combine(root, "icons"));
            using var period = new PeriodStatisticsControl(store, clock, iconCache);
            host.Controls.Add(period);
            host.Show();
            period.EnsureLoaded();
            var grid = (DataGridView)typeof(PeriodStatisticsControl).GetField("_applications", Flags)!.GetValue(period)!;
            var grouped = (CheckBox)typeof(PeriodStatisticsControl).GetField("_grouped", Flags)!.GetValue(period)!;
            WaitFor(() => grid.Rows.Count == 1);
            Assert.Contains("Test group", grid.Rows[0].Cells["Name"].Value?.ToString());
            Assert.IsAssignableFrom<Image>(grid.Rows[0].Cells["Icon"].Value);
            Assert.NotSame(iconCache.EmptyImage, grid.Rows[0].Cells["Icon"].Value);
            Assert.Equal("0:15:00", grid.Rows[0].Cells["Duration"].Value);
            grouped.Checked = false;
            WaitFor(() => grid.Rows.Count == 2);
            Assert.Equal("0:10:00", grid.Rows[0].Cells["Duration"].Value);
            WaitFor(() => !ReferenceEquals(iconCache.EmptyImage, grid.Rows[0].Cells["Icon"].Value));
            var from = (DateTimePicker)typeof(PeriodStatisticsControl).GetField("_from", Flags)!.GetValue(period)!;
            var to = (DateTimePicker)typeof(PeriodStatisticsControl).GetField("_to", Flags)!.GetValue(period)!;
            var tracked = (Label)typeof(PeriodStatisticsControl).GetField("_trackedValue", Flags)!.GetValue(period)!;
            Assert.Equal("0:15:00", tracked.Text);
            from.Value = to.Value;
            to.Value = to.Value.AddDays(-1);
            typeof(PeriodStatisticsControl).GetMethod("LoadPeriod", Flags)!.Invoke(period, []);
            Assert.Empty(grid.Rows.Cast<DataGridViewRow>());
            Assert.Equal("—", tracked.Text);
        });
    }

    [Fact]
    public void Group_editor_saves_selected_processes_without_changing_history()
    {
        RunOnSta(root =>
        {
            using var store = new SqliteStore(Path.Combine(root, "history.db"));
            var now = DateTimeOffset.UtcNow;
            var a = store.Open("group-alpha", now); store.Close(a, now.AddMinutes(1));
            var b = store.Open("group-helper", now); store.Close(b, now.AddMinutes(1));
            using var form = new ApplicationGroupsForm(store);
            var name = (TextBox)typeof(ApplicationGroupsForm).GetField("_name", Flags)!.GetValue(form)!;
            var list = (CheckedListBox)typeof(ApplicationGroupsForm).GetField("_applications", Flags)!.GetValue(form)!;
            name.Text = "Bundle";
            for (var i = 0; i < list.Items.Count; i++) list.SetItemChecked(i, true);
            typeof(ApplicationGroupsForm).GetMethod("SaveSelected", Flags)!.Invoke(form, []);
            Assert.Equal(2, Assert.Single(ApplicationGroups.Load(store.Connection)).Processes.Count);
            Assert.Equal(2, store.EnumerateSessions().Count());
        });
    }

    static void WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(20); }
        Assert.True(condition(), "Timed out waiting for the period statistics.");
    }

    static void RunOnSta(Action<string> action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            var root = Path.Combine(Path.GetTempPath(), $"pcut-period-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try { UiText.Korean = false; action(root); }
            catch (Exception exception) { error = exception; }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
