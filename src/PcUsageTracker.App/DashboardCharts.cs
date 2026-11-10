using PcUsageTracker.Core.Reporting;

namespace PcUsageTracker.App;

internal sealed class TimelineControl : Control, IMessageFilter
{
    const int RasterWidth = 2048;
    static readonly Color TrackBackColor = Color.FromArgb(232, 235, 239);
    static readonly Color IdleBackColor = Color.FromArgb(204, 210, 217);

    readonly ToolTip _toolTip = new();
    IReadOnlyList<TimelinePaintItem> _segments = Array.Empty<TimelinePaintItem>();
    IReadOnlyList<TimelineHourTick> _ticks = Array.Empty<TimelineHourTick>();
    int[] _hitIndexes = [];
    Bitmap? _raster;
    TimeZoneInfo _zone = TimeZoneInfo.Local;
    DateOnly? _day;
    DateTimeOffset _dayStart;
    DateTimeOffset _dayEnd;
    TimelineViewport _viewport = TimelineViewport.FullDay;
    string _summary = "No timeline data loaded.";
    int _hoveredIndex = -1;
    int _focusedIndex = -1;
    bool _dragging;
    bool _mousePressed;
    int _dragStartX;
    TimelineViewport _dragStartViewport;
    double? _selectedFraction;
    string? _highlightProcess;

    public event Action<string?>? ProcessSelected;
    public event Action<string>? SelectionDetailsChanged;
    public event Action<string>? VisibleRangeChanged;

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
        Application.AddMessageFilter(this);
    }

    public void SetData(DateOnly day, TimeZoneInfo zone, IReadOnlyList<TimelineSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(segments);
        if (_day != day || !_zone.Equals(zone))
        {
            _dragging = false;
            _mousePressed = false;
            Capture = false;
            _viewport = TimelineViewport.FullDay;
            _selectedFraction = null;
            _highlightProcess = null;
        }
        _day = day;
        _zone = zone;
        var (dayStart, dayEnd) = Aggregator.DayRange(day, zone);
        _dayStart = dayStart;
        _dayEnd = dayEnd;
        var projected = segments
            .Select(segment => new TimelinePaintItem(
                segment,
                TimelineCoordinates.Project(segment, dayStart, dayEnd)))
            .Where(item => item.Coordinates.EndFraction > item.Coordinates.StartFraction)
            .OrderBy(item => item.Coordinates.StartFraction)
            .ThenBy(item => item.Coordinates.EndFraction)
            .ToArray();
        _segments = Coalesce(projected);
        _ticks = TimelineCoordinates.CreateHourTicks(day, zone, dayStart, dayEnd);
        BuildRaster();
        var totalSeconds = segments.Sum(segment => (long)segment.TotalSeconds);
        _summary = segments.Count == 0
            ? $"No activity recorded on {day:yyyy-MM-dd}."
            : $"{_segments.Count} visible timeline spans on {day:yyyy-MM-dd}, " +
              $"totaling {FormatDuration(totalSeconds)}. Applications are colored; idle spans are hatched.";
        _hoveredIndex = -1;
        if (_segments.Count == 0) _focusedIndex = -1;
        else if (_focusedIndex >= _segments.Count) _focusedIndex = _segments.Count - 1;
        else if (Focused && _focusedIndex < 0) _focusedIndex = 0;
        UpdateAccessibleItem();
        _toolTip.SetToolTip(this, null);
        NotifyVisibleRange();
        NotifySelectionDetails();
        Invalidate();
    }

    public void HighlightProcess(string? processName)
    {
        if (string.Equals(_highlightProcess, processName, StringComparison.OrdinalIgnoreCase)) return;
        _highlightProcess = processName;
        BuildRaster();
        Invalidate();
    }

    public void ResetView()
    {
        _viewport = TimelineViewport.FullDay;
        _hoveredIndex = -1;
        _toolTip.SetToolTip(this, null);
        BuildRaster();
        NotifyVisibleRange();
        Invalidate();
    }

    void NotifyVisibleRange()
    {
        if (_day is null) return;
        string FormatBoundary(double fraction)
        {
            if (fraction >= 1) return "24:00";
            var instant = _dayStart.AddTicks((long)((_dayEnd - _dayStart).Ticks * fraction));
            var local = TimeZoneInfo.ConvertTime(instant, _zone);
            return _zone.IsAmbiguousTime(local.DateTime) ? $"{local:HH:mm zzz}" : $"{local:HH:mm}";
        }
        VisibleRangeChanged?.Invoke($"{FormatBoundary(_viewport.Start)}–{FormatBoundary(_viewport.End)}");
    }

    void NotifySelectionDetails()
    {
        if (_selectedFraction is not { } fraction)
        {
            SelectionDetailsChanged?.Invoke("Click the timeline to inspect a time.");
            return;
        }
        var instant = _dayStart.AddTicks((long)((_dayEnd - _dayStart).Ticks * fraction));
        var local = TimeZoneInfo.ConvertTime(instant, _zone);
        var index = FindSegmentAtDayFraction(fraction);
        if (index < 0)
        {
            SelectionDetailsChanged?.Invoke($"Selected {local:HH:mm:ss} · No recorded activity");
            return;
        }
        var segment = _segments[index].Segment;
        var start = TimeZoneInfo.ConvertTime(segment.StartAt, _zone);
        var end = TimeZoneInfo.ConvertTime(segment.EndAt, _zone);
        SelectionDetailsChanged?.Invoke($"Selected {local:HH:mm:ss} · {segment.DisplayName} · " +
            $"{start:HH:mm:ss}–{end:HH:mm:ss} · {FormatDuration(segment.TotalSeconds)}");
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
            var previousPixelOffset = e.Graphics.PixelOffsetMode;
            e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            // A one-pixel-high raster needs centered sampling when stretched vertically.
            e.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            e.Graphics.DrawImage(_raster, track);
            e.Graphics.InterpolationMode = previousMode;
            e.Graphics.PixelOffsetMode = previousPixelOffset;
        }

        using (var hatch = new System.Drawing.Drawing2D.HatchBrush(
                   System.Drawing.Drawing2D.HatchStyle.ForwardDiagonal, Color.FromArgb(145, 153, 162), IdleBackColor))
        {
            for (var pixel = 0; pixel < _hitIndexes.Length;)
            {
                var index = _hitIndexes[pixel];
                var end = pixel + 1;
                while (end < _hitIndexes.Length && _hitIndexes[end] == index) end++;
                if (index >= 0 && _segments[index].Segment.IsIdle)
                {
                    var x1 = track.Left + track.Width * pixel / _hitIndexes.Length;
                    var x2 = track.Left + (int)Math.Ceiling(track.Width * end / (double)_hitIndexes.Length);
                    e.Graphics.FillRectangle(hatch, x1, track.Top, Math.Max(1, x2 - x1), track.Height);
                }
                pixel = end;
            }
        }

        if (_segments.Count == 0)
        {
            TextRenderer.DrawText(
                e.Graphics,
                _day is null ? "Loading timeline..." : "No activity recorded",
                Font,
                track,
                SystemColors.GrayText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine);
        }

        using var tickPen = new Pen(SystemColors.ControlDark);
        TimelineHourTick? previousTick = null;
        foreach (var tick in VisibleTicks())
        {
            var x = track.Left + (int)Math.Round(track.Width * (tick.Fraction - _viewport.Start) / _viewport.Width);
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
            if (focused.EndFraction >= _viewport.Start && focused.StartFraction <= _viewport.End)
            {
                var x1 = track.Left + (int)Math.Floor(track.Width * Math.Max(0, (focused.StartFraction - _viewport.Start) / _viewport.Width));
                var x2 = track.Left + (int)Math.Ceiling(track.Width * Math.Min(1, (focused.EndFraction - _viewport.Start) / _viewport.Width));
                ControlPaint.DrawFocusRectangle(e.Graphics,
                    Rectangle.FromLTRB(x1, track.Top, Math.Max(x1 + 2, x2), track.Bottom));
            }
        }

        if (_selectedFraction is { } selected && selected >= _viewport.Start && selected <= _viewport.End)
        {
            var x = Math.Clamp(track.Left + (int)Math.Round(track.Width * (selected - _viewport.Start) / _viewport.Width), track.Left, track.Right - 1);
            using var outline = new Pen(Color.White, Math.Max(3, 3 * scale));
            using var line = new Pen(Color.FromArgb(32, 38, 48), Math.Max(1, scale));
            e.Graphics.DrawLine(outline, x, track.Top, x, track.Bottom - 1);
            e.Graphics.DrawLine(line, x, track.Top, x, track.Bottom - 1);

            var instant = _dayStart.AddTicks((long)((_dayEnd - _dayStart).Ticks * selected));
            var local = TimeZoneInfo.ConvertTime(instant, _zone);
            var index = FindSegmentAtDayFraction(selected);
            var name = index < 0 ? "No activity" : _segments[index].Segment.DisplayName;
            var text = $"{local:HH:mm:ss}  {name}";
            var boxWidth = Math.Min(track.Width, TextRenderer.MeasureText(text, Font).Width + (int)(10 * scale));
            var boxHeight = Math.Min(track.Height, Font.Height + (int)(6 * scale));
            var boxX = Math.Clamp(x + (int)(5 * scale), track.Left, Math.Max(track.Left, track.Right - boxWidth));
            var box = new Rectangle(boxX, track.Top, boxWidth, boxHeight);
            using var background = new SolidBrush(Color.FromArgb(245, 255, 255, 255));
            e.Graphics.FillRectangle(background, box);
            TextRenderer.DrawText(e.Graphics, text, Font, box, Color.FromArgb(32, 38, 48),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }

        if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_mousePressed && !_dragging && _dragStartViewport.Width < 1 &&
            Math.Abs(e.X - _dragStartX) >= Math.Max(1, SystemInformation.DragSize.Width / 2))
            _dragging = true;
        if (_dragging)
        {
            var trackWidth = Math.Max(1d, ClientSize.Width - 20d * DeviceDpi / 96d);
            var offset = -((e.X - _dragStartX) / trackWidth) * _dragStartViewport.Width;
            var next = _dragStartViewport.Pan(offset);
            if (next != _viewport)
            {
                _viewport = next;
                _hoveredIndex = -1;
                _toolTip.SetToolTip(this, null);
                BuildRaster();
                NotifyVisibleRange();
                Invalidate();
            }
            return;
        }
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
        if (e.Button == MouseButtons.Left && _day is not null)
        {
            _mousePressed = true;
            _dragging = false;
            _dragStartX = e.X;
            _dragStartViewport = _viewport;
            Capture = true;
            _toolTip.SetToolTip(this, null);
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && _mousePressed)
        {
            if (!_dragging && ClientRectangle.Contains(e.Location))
            {
                var margin = 10d * DeviceDpi / 96d;
                var position = Math.Clamp((e.X - margin) / Math.Max(1, ClientSize.Width - 2 * margin), 0, 1);
                _selectedFraction = _viewport.Start + position * _viewport.Width;
                _focusedIndex = FindSegmentAtDayFraction(_selectedFraction.Value);
                UpdateAccessibleItem();
                var process = _focusedIndex >= 0 ? _segments[_focusedIndex].Segment.ProcessName : null;
                HighlightProcess(process);
                ProcessSelected?.Invoke(process);
                NotifySelectionDetails();
                Invalidate();
            }
            _dragging = false;
            _mousePressed = false;
            Capture = false;
        }
        base.OnMouseUp(e);
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        if (!Capture)
        {
            _dragging = false;
            _mousePressed = false;
        }
        base.OnMouseCaptureChanged(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoveredIndex = -1;
        _toolTip.SetToolTip(this, null);
    }

    public bool PreFilterMessage(ref Message m)
    {
        const int mouseWheel = 0x020A;
        if (m.Msg != mouseWheel || !Visible || IsDisposed || FindForm() != Form.ActiveForm ||
            !RectangleToScreen(ClientRectangle).Contains(Cursor.Position) || _day is null)
            return false;

        var delta = unchecked((short)((m.WParam.ToInt64() >> 16) & 0xffff));
        if (delta == 0) return false;
        var x = PointToClient(Cursor.Position).X;
        var margin = 10d * DeviceDpi / 96d;
        var anchor = Math.Clamp((x - margin) / Math.Max(1, ClientSize.Width - 2 * margin), 0, 1);
        var minWidth = Math.Min(1, TimeSpan.FromMinutes(15).Ticks / (double)(_dayEnd - _dayStart).Ticks);
        _viewport = _viewport.Zoom(anchor, Math.Pow(0.8, delta / 120d), minWidth);
        _hoveredIndex = -1;
        _toolTip.SetToolTip(this, null);
        BuildRaster();
        NotifyVisibleRange();
        Invalidate();
        return true;
    }

    IEnumerable<TimelineHourTick> VisibleTicks()
    {
        if (_day is null || _dayEnd <= _dayStart)
            return Array.Empty<TimelineHourTick>();

        var minutes = (_dayEnd - _dayStart).TotalMinutes * _viewport.Width;
        if (minutes >= 180)
        {
            var step = minutes > 720 ? 3 : 1;
            return _ticks.Where(tick => tick.Fraction >= _viewport.Start && tick.Fraction <= _viewport.End &&
                (tick.LocalHour % step == 0 || tick.IsTransition || tick.Fraction is 0d or 1d));
        }

        var interval = minutes > 90 ? 30 : minutes > 45 ? 15 : minutes > 20 ? 5 : 2;
        var ticks = new List<TimelineHourTick>();
        var startMinute = (int)Math.Ceiling((_dayEnd - _dayStart).TotalMinutes * _viewport.Start / interval) * interval;
        for (var minute = startMinute; minute <= (_dayEnd - _dayStart).TotalMinutes * _viewport.End; minute += interval)
        {
            var instant = _dayStart.AddMinutes(minute);
            var local = TimeZoneInfo.ConvertTime(instant, _zone);
            var ambiguous = _zone.IsAmbiguousTime(local.DateTime);
            var label = ambiguous ? $"{local:HH:mm zzz}" : $"{local:HH:mm}";
            ticks.Add(new TimelineHourTick(instant, minute / (_dayEnd - _dayStart).TotalMinutes,
                label, local.Hour, ambiguous, false));
        }
        return ticks;
    }

    int FindIndexAtFraction(double fraction)
    {
        if (_hitIndexes.Length == 0) return -1;
        var pixel = Math.Clamp((int)Math.Floor(fraction * _hitIndexes.Length), 0, _hitIndexes.Length - 1);
        return _hitIndexes[pixel];
    }

    int FindSegmentAtDayFraction(double fraction)
    {
        for (var index = _segments.Count - 1; index >= 0; index--)
        {
            var coordinates = _segments[index].Coordinates;
            if (coordinates.StartFraction <= fraction && fraction < coordinates.EndFraction) return index;
        }
        return -1;
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
        var coordinates = _segments[next].Coordinates;
        var revealed = _viewport.Reveal((coordinates.StartFraction + coordinates.EndFraction) / 2);
        if (revealed != _viewport)
        {
            _viewport = revealed;
            BuildRaster();
            NotifyVisibleRange();
        }
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
            "Use the arrow keys to move between spans. Use the mouse wheel to zoom and drag to pan.";
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
            if (item.Coordinates.EndFraction <= _viewport.Start || item.Coordinates.StartFraction >= _viewport.End)
                continue;
            var start = Math.Clamp(
                (int)Math.Floor((item.Coordinates.StartFraction - _viewport.Start) / _viewport.Width * RasterWidth), 0, RasterWidth - 1);
            var endExclusive = Math.Clamp(
                (int)Math.Ceiling((item.Coordinates.EndFraction - _viewport.Start) / _viewport.Width * RasterWidth), start + 1, RasterWidth);
            if (!item.Segment.IsIdle)
            {
                var color = ColorFromRgb(item.Segment.ColorRgb);
                if (_highlightProcess is not null && !string.Equals(
                        item.Segment.ProcessName, _highlightProcess, StringComparison.OrdinalIgnoreCase))
                    color = Color.FromArgb((color.R + 3 * TrackBackColor.R) / 4,
                        (color.G + 3 * TrackBackColor.G) / 4, (color.B + 3 * TrackBackColor.B) / 4);
                brush.Color = color;
                graphics.FillRectangle(brush, start, 0, endExclusive - start, 1);
            }
            else
            {
                brush.Color = IdleBackColor;
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
            Application.RemoveMessageFilter(this);
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

    public event Action<DateOnly>? DaySelected;

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
        var scale = DeviceDpi / 96f;
        var left = 42 * scale;
        var right = ClientSize.Width - 8 * scale;
        var top = 12 * scale;
        var bottom = ClientSize.Height - 32 * scale;
        if (e.Button == MouseButtons.Left && _points.Count > 0 && right > left &&
            e.X >= left && e.X < right && e.Y >= top && e.Y <= bottom)
        {
            _focusedIndex = Math.Clamp((int)((e.X - left) / ((right - left) / _points.Count)), 0, _points.Count - 1);
            UpdateAccessibleItem();
            Invalidate();
            DaySelected?.Invoke(_points[_focusedIndex].Date);
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

        if (e.KeyCode is Keys.Enter or Keys.Space && _focusedIndex >= 0 && _focusedIndex < _points.Count)
        {
            DaySelected?.Invoke(_points[_focusedIndex].Date);
            e.Handled = true;
            e.SuppressKeyPress = true;
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
            $"total {FormatDuration(point.TotalSeconds)}. Use the arrow keys to move between days and Enter to select.";
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
