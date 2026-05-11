using System.Runtime.InteropServices;
using PcUsageTracker.Core.Sampling;
using Serilog;

namespace PcUsageTracker.App.Interop;

/// <summary>
/// GetLastInputInfo로 마지막 키보드/마우스 입력 이후 경과 시간을 측정한다.
/// dwTime은 system-up tick count(ms)이고 약 49.7일 주기로 wraparound한다 — unchecked uint 산술이 이를 자연 처리한다.
/// </summary>
internal sealed class Win32IdleProbe : IIdleProbe
{
    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    public TimeSpan IdleDuration
    {
        get
        {
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref lii))
            {
                Log.Debug("GetLastInputInfo failed (err={Err})", Marshal.GetLastWin32Error());
                return TimeSpan.Zero;
            }
            // Environment.TickCount는 signed int지만 커널의 monotonic uint 카운터를 그대로 reinterpret한 값이다.
            // (uint) 캐스트가 unsigned bit-pattern을 복원하고, unchecked 뺄셈이 양쪽 overflow boundary(~24.9일 signed flip,
            // ~49.7일 uint wrap)에서도 올바른 elapsed ms를 돌려준다.
            uint diff = unchecked((uint)Environment.TickCount - lii.dwTime);
            return TimeSpan.FromMilliseconds(diff);
        }
    }
}
