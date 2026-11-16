using System.Reflection;
using System.Runtime.ExceptionServices;
using PcUsageTracker.App;
using PcUsageTracker.Core.Reporting;
using PcUsageTracker.Core.Sampling;
using PcUsageTracker.Core.Storage;
using Xunit;

namespace PcUsageTracker.App.Tests;

public class UiTextTests
{
    [Fact]
    public void Korean_report_translates_main_controls_without_changing_selection_values()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"pcut-language-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                UiText.Korean = true;
                using var store = new SqliteStore(Path.Combine(root, "data.db"));
                using var form = new ReportForm(new SystemClock(), store, () => { });
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var tabs = (TabControl)typeof(ReportForm).GetField("_tabs", flags)!.GetValue(form)!;
                var days = (ComboBox)typeof(ReportForm).GetField("_dailyRange", flags)!.GetValue(form)!;
                Assert.Equal("대시보드", tabs.TabPages[0].Text);
                Assert.Contains(tabs.TabPages.Cast<TabPage>(), page => page.Text == "기간 통계");
                Assert.Equal(0, days.SelectedIndex);
                Assert.Equal("7일", days.SelectedItem);
                Assert.Equal("게임", UiText.Category("Game"));
                var grid = (DataGridView)typeof(ReportForm).GetField("_dashboardGrid", flags)!.GetValue(form)!;
                var options = (Dictionary<DataGridView, (TextBox Search, ComboBox Sort)>)typeof(ReportForm)
                    .GetField("_gridOptions", flags)!.GetValue(form)!;
                ReportEntry[] entries = [new("demo", 60, categoryName: "Game"), new("__idle__", 10, displayName: "(Idle)")];
                typeof(ReportForm).GetMethod("Populate", flags)!.Invoke(form, [grid, entries]);
                options[grid].Search.Text = "게임";
                Assert.Single(grid.Rows.Cast<DataGridViewRow>());
                Assert.Equal("demo", grid.Rows[0].Cells["Process"].Value);
                options[grid].Search.Text = "자리 비움";
                Assert.Single(grid.Rows.Cast<DataGridViewRow>());
                Assert.Equal("(자리 비움)", grid.Rows[0].Cells["Process"].Value);
                var chosen = DateOnly.FromDateTime(DateTime.Today).AddDays(-3);
                form.RestoreView(chosen, 2);
                var view = form.CaptureView();
                UiText.Korean = false;
                using var english = new ReportForm(new SystemClock(), store, () => { });
                english.RestoreView(view.Day, view.TabIndex);
                Assert.Equal((chosen, 2), english.CaptureView());
                var englishTabs = (TabControl)typeof(ReportForm).GetField("_tabs", flags)!.GetValue(english)!;
                Assert.Equal("Dashboard", englishTabs.TabPages[0].Text);
            }
            catch (Exception exception) { error = exception; }
            finally
            {
                UiText.Korean = false;
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
