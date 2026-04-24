using System.Runtime.InteropServices;
using Serilog;

namespace PcUsageTracker.App.Interop;

/// <summary>
/// WTS 세션 lock/unlock + 전원 suspend/resume broadcast를 수신하는 메시지 전용 창.
/// NativeWindow로 구현해서 별도 Form 의존성 없음.
/// </summary>
internal sealed class SessionEventsWindow : NativeWindow, IDisposable
{
    const int WM_WTSSESSION_CHANGE = 0x02B1;
    const int WM_POWERBROADCAST = 0x0218;
    const int WTS_SESSION_LOCK = 0x7;
    const int WTS_SESSION_UNLOCK = 0x8;
    const int PBT_APMSUSPEND = 0x4;
    const int PBT_APMRESUMESUSPEND = 0x7;
    const int PBT_APMRESUMEAUTOMATIC = 0x12;
    const int NOTIFY_FOR_THIS_SESSION = 0;

    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);

    [DllImport("wtsapi32.dll")]
    static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

    public event Action? Locked;
    public event Action? Unlocked;
    public event Action? Suspending;
    public event Action? Resuming;

    public SessionEventsWindow()
    {
        CreateHandle(new CreateParams());
        if (!WTSRegisterSessionNotification(Handle, NOTIFY_FOR_THIS_SESSION))
        {
            Log.Warning("WTSRegisterSessionNotification failed (err={Err}) — lock/unlock 감지 비활성",
                Marshal.GetLastWin32Error());
        }
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_WTSSESSION_CHANGE:
                {
                    var reason = m.WParam.ToInt32();
                    if (reason == WTS_SESSION_LOCK) Locked?.Invoke();
                    else if (reason == WTS_SESSION_UNLOCK) Unlocked?.Invoke();
                    break;
                }
            case WM_POWERBROADCAST:
                {
                    var code = m.WParam.ToInt32();
                    if (code == PBT_APMSUSPEND) Suspending?.Invoke();
                    else if (code == PBT_APMRESUMESUSPEND || code == PBT_APMRESUMEAUTOMATIC) Resuming?.Invoke();
                    break;
                }
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            try { WTSUnRegisterSessionNotification(Handle); }
            catch (Exception ex) { Log.Warning(ex, "WTSUnRegisterSessionNotification threw"); }
            DestroyHandle();
        }
    }
}
