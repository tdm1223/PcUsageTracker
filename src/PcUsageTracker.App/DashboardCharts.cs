using PcUsageTracker.Core.Reporting;

namespace PcUsageTracker.App;

internal sealed class TimelineControl : Control
{
    const int RasterWidth = 2048;
    static readonly Color TrackBackColor = Color.FromArgb(232, 235, 239);

    readonly ToolTip _toolTip = new();
    IReadOnlyList<TimelinePaintItem> _segments = Array.Empty<TimelinePaintItem>();
    IReadOnlyList<TimelineHourTick> _ticks = Array.Empty<TimelineHourTick>();
    int[] _hitIndexes = [];
    Bitmap? _raster;
    TimeZoneInfo _zone = TimeZoneInfo.Local;
    string _summary = "No timeline data loaded.";
    int _hoveredIndex = -1;
    int _focusedIndex = -1;

    public TimelineControl()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        SetStyle(ControlStyles.UserPaint |
                 ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.Selectable, true);
        MinimumSize = new Size(100, 76);
        TabStop = true;
        AccessibleName = "24 hour usage timeline";
        AccessibleRole = AccessibleRole.Graphic;
    }

    public void SetData(DateOnly day, TimeZoneInfo zone, IReadOnlyList<TimelineSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(segments);
        _zone = zone;
        var (dayStart, dayEnd) = Aggregator.DayRange(day, zone);
        var projected = segments
            .Select(segment => new TimelinePaintItem(
                segment,
                TimelineCoordinates.Project(segment, dayStart, dayEnd)))
            .Where(item => item.Coordinates.EndFraction > item.Coordinates.StartFraction)
            .OrderBy(item => item.Coordinates.StartFraction)
            .ThenBy(item => item.Coordinates.EndFraction)
            .ToArray();
        _segments = Coalesce(projected);
        _ticks = TimelineCoordinates.CreateHourTicks(day, zone, dayStart, dayEnd)
            .Where(tick => tick.LocalHour % 3 == 0 || tick.IsTransition || tick.Fraction is 0d or 1d)
            .ToArray();
        BuildRaster();
        var totalSeconds = segments.Sum(segment => (long)segment.TotalSeconds);
        _summary = segments.Count == 0
            ? $"No activity recorded on {day:yyyy-MM-dd}."
            : $"{_segments.Count} visible timeline spans on {day:yyyy-MM-dd}, " +
              $"totaling {FormatDuration(totalSeconds)}. Applications are colored; idle spans are empty.";
        _hoveredIndex = -1;
        if (_segments.Count == 0) _focusedIndex = -1;
        else if (_focusedIndex >= _segments.Count) _focusedIndex = _segments.Count - 1;
        else if (Focused && _focusedIndex < 0) _focusedIndex = 0;
        UpdateAccessibleItem();
        _toolTip.SetToolTip(this, null);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(BackColor);

        var scale = DeviceDpi / 96f;
        var left = (int)Math.Round(10 * scale);
        var right = (int)Math.Round(10 * scale);
        var top = (int)Math.Round(8 * scale);
        var labelHeight = (int)Math.Round(30 * scale);
        var track = new Rectangle(
            left,
            top,
            Math.Max(1, ClientSize.Width - left - right),
            Math.Max(12, ClientSize.Height - top - labelHeight - (int)Math.Round(5 * scale)));

        using (var background = new SolidBrush(TrackBackColor))
            e.Graphics.FillRectangle(background, track);

        if (_raster is not null)
        {
            var previousMode = e.Graphics.InterpolationMode;
            e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            e.Graphics.DrawImage(_raster, track);
            e.Graphics.InterpolationMode = previousMode;
        }

        if (_segments.Count == 0)
        {
            TextRenderer.DrawText(
                e.Graphics,
                "No activity recorded",
                Font,
                track,
                SystemColors.GrayText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine);
        }

        using var tickPen = new Pen(SystemColors.ControlDark);
        TimelineHourTick? previousTick = null;
        foreach (var tick in _ticks)
        {
            var x = track.Left + (int)Math.Round(track.Width * tick.Fraction);
            e.Graphics.DrawLine(tickPen, x, track.Bottom, x, track.Bottom + (int)Math.Round(3 * scale));
            var label = tick.Label;
            var width = TextRenderer.MeasureText(label, Font).Width;
            var repeatedLine = tick.IsTransition && previousTick is { } previous &&
                               previous.LocalHour == tick.LocalHour;
            TextRenderer.DrawText(
                e.Graphics,
                label,
                Font,
                new Point(
                    Math.Clamp(x - width / 2, 0, Math.Max(0, ClientSize.Width - width)),
                    track.Bottom + 3 + (repeatedLine ? Font.Height : 0)),
                SystemColors.GrayText,
                TextFormatFlags.NoPadding);
            previousTick = tick;
        }

        if (Focused && _focusedIndex >= 0 && _focusedIndex < _segments.Count)
        {
            var focused = _segments[_focusedIndex].Coordinates;
            var x1 = track.Left + (int)Math.Floor(track.Width * focused.StartFraction);
            var x2 = track.Left + (int)Math.Ceiling(track.Width * focused.EndFraction);
            ControlPaint.DrawFocusRectangle(e.Graphics,
                Rectangle.FromLTRB(x1, track.Top, Math.Max(x1 + 2, x2), track.Bottom));
        }

        if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var fraction = ClientSize.Width <= 20
            ? -1
            : Math.Clamp((e.X - 10d * DeviceDpi / 96d) /
                         Math.Max(1d, ClientSize.Width - 20d * DeviceDpi / 96d), 0d, 1d);
        var foundIndex = fraction < 0 ? -1 : FindIndexAtFraction(fraction);
        if (foundIndex == _hoveredIndex) return;

        _hoveredIndex = foundIndex;
        if (foundIndex >= 0)
        {
            var segment = _segments[foundIndex].Segment;
            var start = TimeZoneInfo.ConvertTime(segment.StartAt, _zone);
            var end = TimeZoneInfo.ConvertTime(segment.EndAt, _zone);
            _toolTip.SetToolTip(this,
                $"{segment.DisplayName}\n{segment.CategoryName ?? (segment.IsIdle ? "Idle" : "Other")}\n" +
                $"{start:HH:mm:ss zzz} – {end:HH:mm:ss zzz}\n{FormatDuration(segment.TotalSeconds)}");
        }
        else
        {
            _toolTip.SetToolTip(this, null);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (_hoveredIndex >= 0)
        {
            _focusedIndex = _hoveredIndex;
            UpdateAccessibleItem();
            Invalidate();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoveredIndex = -1;
        _toolTip.SetToolTip(this, null);
    }

    int FindIndexAtFraction(double fraction)
    {
        if (_hitIndexes.Length == 0) return -1;
        var pixel = Math.Clamp((int)Math.Floor(fraction * _hitIndexes.Length), 0, _hitIndexes.Length - 1);
        return _hitIndexes[pixel];
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_segments.Count == 0)
        {
            base.OnKeyDown(e);
            return;
        }

        var next = e.KeyCode switch
        {
            Keys.Left or Keys.Up => Math.Max(0, _focusedIndex - 1),
            Keys.Right or Keys.Down => Math.Min(_segments.Count - 1, _focusedIndex + 1),
            Keys.Home => 0,
            Keys.End => _segments.Count - 1,
            _ => -1,
        };
        if (next < 0)
        {
            base.OnKeyDown(e);
            return;
        }

        _focusedIndex = next;
        UpdateAccessibleItem();
        Invalidate();
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    protected override bool IsInputKey(Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        return key is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End ||
               base.IsInputKey(keyData);
    }

    void UpdateAccessibleItem()
    {
        if (_focusedIndex < 0 || _focusedIndex >= _segments.Count)
        {
            AccessibleName = "24 hour usage timeline";
            AccessibleDescription = _summary;
            return;
        }

        var segment = _segments[_focusedIndex].Segment;
        var start = TimeZoneInfo.ConvertTime(segment.StartAt, _zone);
        var end = TimeZoneInfo.ConvertTime(segment.EndAt, _zone);
        AccessibleName = $"Timeline span {_focusedIndex + 1} of {_segments.Count}: {segment.DisplayName}";
        AccessibleDescription =
            $"{segment.CategoryName ?? (segment.IsIdle ? "Idle" : "Other")}, " +
            $"{start:HH:mm:ss zzz} to {end:HH:mm:ss zzz}, {FormatDuration(segment.TotalSeconds)}. " +
            "Use the arrow keys to move between spans.";
        AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
        AccessibilityNotifyClients(AccessibleEvents.DescriptionChange, -1);
    }

    static IReadOnlyList<TimelinePaintItem> Coalesce(IReadOnlyList<TimelinePaintItem> projected)
    {
        if (projected.Count == 0) return Array.Empty<TimelinePaintItem>();
        var result = new List<TimelinePaintItem>(projected.Count) { projected[0] };
        for (var index = 1; index < projected.Count; index++)
        {
            var next = projected[index];
            var previous = result[^1];
            var samePresentation =
                string.Equals(previous.Segment.ProcessName, next.Segment.ProcessName,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(previous.Segment.DisplayName, next.Segment.DisplayName, StringComparison.Ordinal) &&
                string.Equals(previous.Segment.CategoryName, next.Segment.CategoryName,
                    StringComparison.OrdinalIgnoreCase) &&
                previous.Segment.ColorRgb == next.Segment.ColorRgb;
            if (samePresentation && next.Coordinates.StartFraction <= previous.Coordinates.EndFraction)
            {
                result[^1] = new TimelinePaintItem(
                    previous.Segment with { EndAt = Later(previous.Segment.EndAt, next.Segment.EndAt) },
                    previous.Coordinates with
                    {
                        EndFraction = Math.Max(
                            previous.Coordinates.EndFraction,
                            next.Coordinates.EndFraction),
                    });
            }
            else
            {
                result.Add(next);
            }
        }
        return result;
    }

    void BuildRaster()
    {
        _raster?.Dispose();
        _raster = new Bitmap(RasterWidth, 1);
        _hitIndexes = Enumerable.Repeat(-1, RasterWidth).ToArray();
        using var graphics = Graphics.FromImage(_raster);
        graphics.Clear(TrackBackColor);
        using var brush = new SolidBrush(Color.Black);
        for (var index = 0; index < _segments.Count; index++)
        {
            var item = _segments[index];
            var start = Math.Clamp(
                (int)Math.Floor(item.Coordinates.StartFraction * RasterWidth), 0, RasterWidth - 1);
            var endExclusive = Math.Clamp(
                (int)Math.Ceiling(item.Coordinates.EndFraction * RasterWidth), start + 1, RasterWidth);
            if (!item.Segment.IsIdle)
            {
                brush.Color = ColorFromRgb(item.Segment.ColorRgb);
                graphics.FillRectangle(brush, start, 0, endExclusive - start, 1);
            }
            Array.Fill(_hitIndexes, index, start, endExclusive - start);
        }
    }

    static DateTimeOffset Later(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;

    readonly record struct TimelinePaintItem(TimelineSegment Segment, TimelineCoordinates Coordinates);

    static Color ColorFromRgb(int rgb) =>
        Color.FromArgb((rgb >> 16) & 0xff, (rgb >> 8) & 0xff, rgb & 0xff);

    static string FormatDuration(long seconds)
    {
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}"
            : $"{time.Minutes:D2}:{time.Seconds:D2}";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
            _raster?.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnEnter(EventArgs e)
    {
        base.OnEnter(e);
        if (_focusedIndex < 0 && _segments.Count > 0) _focusedIndex = 0;
        UpdateAccessibleItem();
        Invalidate();
    }

    protected override void OnLeave(EventArgs e)
    {
        base.OnLeave(e);
        AccessibleName = "24 hour usage timeline";
        AccessibleDescription = _summary;
        Invalidate();
    }
}

internal sealed class DailyUsageChartControl : Control
{
    static readonly Color ActiveColor = Color.FromArgb(66, 133, 244);
    static readonly Color IdleColor = Color.FromArgb(158, 158, 158);

    readonly ToolTip _toolTip = new();
    IReadOnlyList<DailyUsagePoint> _points = Array.Empty<DailyUsagePoint>();
    int _maxSeconds;
    int _hoveredIndex = -1;
    int _focusedIndex = -1;
    string _summary = "No daily usage data loaded.";

    public DailyUsageChartControl()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        SetStyle(ControlStyles.UserPaint |
                 ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.Selectable, true);
        MinimumSize = new Size(180, 140);
        TabStop = true;
        AccessibleName = "Daily active and idle usage chart";
        AccessibleRole = AccessibleRole.Graphic;
    }

    public void SetData(IReadOnlyList<DailyUsagePoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        _points = points;
        _maxSeconds = points.Count == 0 ? 0 : points.Max(point => point.TotalSeconds);
        var active = points.Sum(point => (long)point.ActiveSeconds);
        var idle = points.Sum(point => (long)point.IdleSeconds);
        _summary = points.Count == 0 || _maxSeconds == 0
            ? points.Count == 0
                ? "No daily usage data."
                : $"No activity from {points[0].Date:yyyy-MM-dd} to {points[^1].Date:yyyy-MM-dd}."
            : $"{points.Count} day trend from {points[0].Date:yyyy-MM-dd} to {points[^1].Date:yyyy-MM-dd}. " +
              $"Active {FormatDuration(active)}, idle {FormatDuration(idle)}.";
        _hoveredIndex = -1;
        if (points.Count == 0) _focusedIndex = -1;
        else if (_focusedIndex >= points.Count) _focusedIndex = points.Count - 1;
        else if (Focused && _focusedIndex < 0) _focusedIndex = 0;
        UpdateAccessibleItem();
        _toolTip.SetToolTip(this, null);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(BackColor);

        var scale = DeviceDpi / 96f;
        var left = (int)Math.Round(42 * scale);
        var right = (int)Math.Round(8 * scale);
        var top = (int)Math.Round(12 * scale);
        var bottom = (int)Math.Round(32 * scale);
        var plot = Rectangle.FromLTRB(
            left,
            top,
            Math.Max(left + 1, ClientSize.Width - right),
            Math.Max(top + 1, ClientSize.Height - bottom));

        var max = _maxSeconds;
        if (max <= 0)
        {
            TextRenderer.DrawText(
                e.Graphics,
                "No activity in this range",
                Font,
                plot,
                SystemColors.GrayText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine);
            DrawLegend(e.Graphics, scale);
            DrawFocusedDay(e.Graphics, plot);
            if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle);
            return;
        }

        using var gridPen = new Pen(Color.FromArgb(220, 223, 227));
        for (var line = 0; line <= 2; line++)
        {
            var y = plot.Bottom - plot.Height * line / 2;
            e.Graphics.DrawLine(gridPen, plot.Left, y, plot.Right, y);
            var seconds = max * line / 2;
            TextRenderer.DrawText(
                e.Graphics,
                FormatAxis(seconds),
                Font,
                new Rectangle(0, y - Font.Height / 2, left - (int)(4 * scale), Font.Height + 2),
                SystemColors.GrayText,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        var slot = plot.Width / (double)_points.Count;
        var gap = Math.Max(1d, Math.Min(5d * scale, slot * .25));
        using var activeBrush = new SolidBrush(ActiveColor);
        using var idlePen = new Pen(IdleColor) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
        for (var i = 0; i < _points.Count; i++)
        {
            var point = _points[i];
            var x = plot.Left + i * slot + gap / 2;
            var width = Math.Max(1d, slot - gap);
            var activeHeight = plot.Height * point.ActiveSeconds / (double)max;
            var idleHeight = plot.Height * point.IdleSeconds / (double)max;

            if (activeHeight > 0)
                e.Graphics.FillRectangle(activeBrush,
                    (float)x, (float)(plot.Bottom - activeHeight), (float)width, (float)activeHeight);
            if (idleHeight > 0)
                e.Graphics.DrawRectangle(idlePen,
                    (float)x, (float)(plot.Bottom - activeHeight - idleHeight),
                    (float)width, (float)Math.Max(1d, idleHeight));

            var showLabel = _points.Count <= 7 || i == 0 || i == _points.Count - 1 || i % 5 == 0;
            if (!showLabel) continue;
            var label = point.Date.ToString("M/d");
            var labelWidth = Math.Max((int)Math.Ceiling(slot * (_points.Count <= 7 ? 1.1 : 2.5)), 36);
            TextRenderer.DrawText(
                e.Graphics,
                label,
                Font,
                new Rectangle((int)(x + width / 2 - labelWidth / 2), plot.Bottom + 3, labelWidth, Font.Height + 2),
                SystemColors.GrayText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        }

        DrawLegend(e.Graphics, scale);
        DrawFocusedDay(e.Graphics, plot);
        if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var scale = DeviceDpi / 96f;
        var left = 42 * scale;
        var right = 8 * scale;
        var plotWidth = ClientSize.Width - left - right;
        var index = _points.Count == 0 || plotWidth <= 0 || e.X < left || e.X > left + plotWidth
            ? -1
            : Math.Clamp((int)((e.X - left) / (plotWidth / _points.Count)), 0, _points.Count - 1);
        if (index == _hoveredIndex) return;

        _hoveredIndex = index;
        if (index >= 0)
        {
            var point = _points[index];
            _toolTip.SetToolTip(this,
                $"{point.Date:yyyy-MM-dd}\nActive: {FormatDuration(point.ActiveSeconds)}\n" +
                $"Idle: {FormatDuration(point.IdleSeconds)}\nTotal: {FormatDuration(point.TotalSeconds)}");
        }
        else
        {
            _toolTip.SetToolTip(this, null);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (_hoveredIndex >= 0)
        {
            _focusedIndex = _hoveredIndex;
            UpdateAccessibleItem();
            Invalidate();
        }
        base.OnMouseDown(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_points.Count == 0)
        {
            base.OnKeyDown(e);
            return;
        }

        var next = e.KeyCode switch
        {
            Keys.Left or Keys.Up => Math.Max(0, _focusedIndex - 1),
            Keys.Right or Keys.Down => Math.Min(_points.Count - 1, _focusedIndex + 1),
            Keys.Home => 0,
            Keys.End => _points.Count - 1,
            _ => -1,
        };
        if (next < 0)
        {
            base.OnKeyDown(e);
            return;
        }

        _focusedIndex = next;
        UpdateAccessibleItem();
        Invalidate();
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    protected override bool IsInputKey(Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        return key is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End ||
               base.IsInputKey(keyData);
    }

    void UpdateAccessibleItem()
    {
        if (_focusedIndex < 0 || _focusedIndex >= _points.Count)
        {
            AccessibleName = "Daily active and idle usage chart";
            AccessibleDescription = _summary;
            return;
        }

        var point = _points[_focusedIndex];
        AccessibleName = $"Day {_focusedIndex + 1} of {_points.Count}: {point.Date:yyyy-MM-dd}";
        AccessibleDescription =
            $"Active {FormatDuration(point.ActiveSeconds)}, idle {FormatDuration(point.IdleSeconds)}, " +
            $"total {FormatDuration(point.TotalSeconds)}. Use the arrow keys to move between days.";
        AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
        AccessibilityNotifyClients(AccessibleEvents.DescriptionChange, -1);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoveredIndex = -1;
        _toolTip.SetToolTip(this, null);
    }

    void DrawLegend(Graphics graphics, float scale)
    {
        var y = Math.Max(0, ClientSize.Height - (int)Math.Round(18 * scale));
        var swatch = Math.Max(8, (int)Math.Round(10 * scale));
        using var active = new SolidBrush(ActiveColor);
        using var idle = new Pen(IdleColor) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
        graphics.FillRectangle(active, 8, y + 2, swatch, swatch);
        TextRenderer.DrawText(graphics, "Active", Font, new Point(11 + swatch, y), ForeColor,
            TextFormatFlags.NoPadding);
        var activeWidth = TextRenderer.MeasureText("Active", Font).Width;
        var idleX = 18 + swatch + activeWidth;
        graphics.DrawRectangle(idle, idleX, y + 2, swatch, swatch);
        TextRenderer.DrawText(graphics, "Idle", Font, new Point(idleX + swatch + 3, y), ForeColor,
            TextFormatFlags.NoPadding);
    }

    void DrawFocusedDay(Graphics graphics, Rectangle plot)
    {
        if (!Focused || _focusedIndex < 0 || _focusedIndex >= _points.Count) return;
        var focusedSlot = plot.Width / (double)_points.Count;
        var x = plot.Left + _focusedIndex * focusedSlot;
        ControlPaint.DrawFocusRectangle(graphics,
            new Rectangle((int)x, plot.Top, Math.Max(2, (int)Math.Ceiling(focusedSlot)), plot.Height));
    }

    static string FormatAxis(int seconds)
    {
        if (seconds >= 3_600) return $"{seconds / 3_600d:0.#}h";
        if (seconds >= 60) return $"{seconds / 60d:0.#}m";
        return $"{seconds}s";
    }

    static string FormatDuration(long seconds)
    {
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}"
            : $"{time.Minutes:D2}:{time.Seconds:D2}";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _toolTip.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnEnter(EventArgs e)
    {
        base.OnEnter(e);
        if (_focusedIndex < 0 && _points.Count > 0) _focusedIndex = 0;
        UpdateAccessibleItem();
        Invalidate();
    }

    protected override void OnLeave(EventArgs e)
    {
        base.OnLeave(e);
        AccessibleName = "Daily active and idle usage chart";
        AccessibleDescription = _summary;
        Invalidate();
    }
}
