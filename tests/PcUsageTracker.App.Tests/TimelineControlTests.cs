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
