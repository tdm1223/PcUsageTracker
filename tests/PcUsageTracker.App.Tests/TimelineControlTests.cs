using System.Reflection;
using System.Runtime.ExceptionServices;
using PcUsageTracker.App;
using PcUsageTracker.Core.Reporting;
using Xunit;

namespace PcUsageTracker.App.Tests;

public class TimelineControlTests
{
    [Fact]
    public void Paint_before_data_is_loaded_does_not_throw()
    {
        RunOnSta(() =>
        {
            using var control = new TimelineControl { Size = new Size(1000, 100) };
            Paint(control);
        });
    }

    [Fact]
    public void Paint_after_loading_an_empty_day_does_not_throw()
    {
        RunOnSta(() =>
        {
            using var control = new TimelineControl { Size = new Size(1000, 100) };
            control.SetData(new DateOnly(2026, 11, 10), TimeZoneInfo.Utc, Array.Empty<TimelineSegment>());
            Paint(control);
        });
    }

    [Fact]
    public void Clicking_a_segment_exposes_process_and_session_details()
    {
        RunOnSta(() =>
        {
            using var control = new TimelineControl { Size = new Size(980, 100) };
            var day = new DateOnly(2026, 11, 10);
            var start = new DateTimeOffset(2026, 11, 10, 2, 0, 0, TimeSpan.Zero);
            control.SetData(day, TimeZoneInfo.Utc, [new TimelineSegment("alpha", "Alpha", "Game", 0xff0000, start, start.AddHours(1))]);
            string? process = null, detail = null;
            control.ProcessSelected += value => process = value;
            control.SelectionDetailsChanged += value => detail = value;
            Mouse(control, "OnMouseDown", 110, 25);
            Mouse(control, "OnMouseUp", 110, 25);
            Assert.Equal("alpha", process);
            Assert.Contains("Alpha", detail);
            Assert.Contains("02:00:00–03:00:00", detail);
            Assert.Contains("1:00:00", detail);
        });
    }

    [Fact]
    public void Dragging_zoomed_timeline_moves_the_visible_range_and_reset_restores_it()
    {
        RunOnSta(() =>
        {
            using var control = new TimelineControl { Size = new Size(980, 100) };
            control.SetData(new DateOnly(2026, 11, 10), TimeZoneInfo.Utc, Array.Empty<TimelineSegment>());
            typeof(TimelineControl).GetField("_viewport", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(control, new TimelineViewport(.25, .5));
            string? range = null;
            control.VisibleRangeChanged += value => range = value;
            Mouse(control, "OnMouseDown", 500, 25);
            Mouse(control, "OnMouseMove", 300, 25);
            Mouse(control, "OnMouseUp", 300, 25);
            Assert.NotNull(range);
            Assert.NotEqual("06:00–12:00", range);
            control.ResetView();
            Assert.Equal("00:00–24:00", range);
        });
    }

    [Fact]
    public void Daily_chart_click_selects_the_corresponding_date()
    {
        RunOnSta(() =>
        {
            using var chart = new DailyUsageChartControl { Size = new Size(400, 200) };
            var day = new DateOnly(2026, 11, 10);
            chart.SetData([new DailyUsagePoint(day, 30, 0), new DailyUsagePoint(day.AddDays(1), 0, 0)]);
            DateOnly? selected = null;
            chart.DaySelected += value => selected = value;
            Mouse(chart, "OnMouseDown", 100, 50);
            Assert.Equal(day, selected);
            Mouse(chart, "OnMouseDown", 300, 50);
            Assert.Equal(day.AddDays(1), selected);
        });
    }

    [Fact]
    public void Daily_chart_does_not_select_a_future_date()
    {
        RunOnSta(() =>
        {
            using var chart = new DailyUsageChartControl { Size = new Size(400, 200) };
            var today = new DateOnly(2026, 11, 12);
            chart.SetData([new DailyUsagePoint(today, 30, 0), new DailyUsagePoint(today.AddDays(1), 0, 0)], today, today);
            DateOnly? selected = null;
            chart.DaySelected += value => selected = value;
            Mouse(chart, "OnMouseDown", 300, 50);
            Assert.Null(selected);
            Mouse(chart, "OnMouseDown", 100, 50);
            Assert.Equal(today, selected);
        });
    }

    static void Mouse(Control control, string method, int x, int y) =>
        control.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(control, [new MouseEventArgs(MouseButtons.Left, 1, x, y, 0)]);

    static void Paint(TimelineControl control)
    {
        using var bitmap = new Bitmap(control.Width, control.Height);
        using var graphics = Graphics.FromImage(bitmap);
        using var args = new PaintEventArgs(graphics, control.ClientRectangle);
        try
        {
            typeof(TimelineControl).GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(control, [args]);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is { } inner)
        {
            ExceptionDispatchInfo.Capture(inner).Throw();
        }
    }

    static void RunOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
