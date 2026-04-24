using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace PcUsageTracker.App.Assets;

/// <summary>
/// WakaTime 스타일의 미니멀한 트레이 아이콘을 런타임에 생성한다.
/// 다크 원형 배경 + 틸(teal) 원호 wedge. paused 상태는 데사튜레이트.
/// 반환된 Icon의 HICON은 수동으로 DestroyIcon 해야 한다 — <see cref="OwnedIcon"/> 사용.
/// </summary>
public static class IconFactory
{
    public static OwnedIcon CreateTrayIcon(bool paused = false)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var bg = paused ? Color.FromArgb(90, 90, 96) : Color.FromArgb(32, 36, 42);
            var fg = paused ? Color.FromArgb(150, 150, 150) : Color.FromArgb(60, 200, 170);
            var ring = paused ? Color.FromArgb(120, 120, 120) : Color.FromArgb(80, 210, 180);

            // 원형 다크 배경
            using (var brush = new SolidBrush(bg))
                g.FillEllipse(brush, 1, 1, size - 2, size - 2);

            // 얇은 외곽 링 (시계 느낌)
            using (var pen = new Pen(ring, 1.5f))
                g.DrawEllipse(pen, 2, 2, size - 4, size - 4);

            // 틸 wedge (-90°부터 시계방향 135°)
            using (var brush = new SolidBrush(fg))
                g.FillPie(brush, 7, 7, size - 14, size - 14, -90, 135);

            // 중심점
            using (var brush = new SolidBrush(Color.FromArgb(220, bg)))
                g.FillEllipse(brush, size / 2 - 2, size / 2 - 2, 4, 4);
        }

        var hicon = bmp.GetHicon();
        return new OwnedIcon(hicon);
    }
}

/// <summary>HICON을 소유하는 Icon. Dispose 시 DestroyIcon을 호출한다.</summary>
public sealed class OwnedIcon : IDisposable
{
    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);

    readonly IntPtr _handle;
    public Icon Icon { get; }

    public OwnedIcon(IntPtr handle)
    {
        _handle = handle;
        Icon = (Icon)Icon.FromHandle(handle).Clone();
    }

    public void Dispose()
    {
        Icon.Dispose();
        if (_handle != IntPtr.Zero) DestroyIcon(_handle);
    }
}
