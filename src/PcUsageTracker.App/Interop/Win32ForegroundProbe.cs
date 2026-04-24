using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using PcUsageTracker.Core.Sampling;
using Serilog;

namespace PcUsageTracker.App.Interop;

internal sealed class Win32ForegroundProbe : IForegroundProbe
{
    // PROCESS_QUERY_LIMITED_INFORMATION — Vista+. 보호/관리자 프로세스도 대부분 허용.
    // MainModule 경로(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ) 대비 훨씬 덜 제한적.
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryFullProcessImageName(IntPtr hProcess, uint flags, StringBuilder exeName, ref uint size);

    public ForegroundSnapshot? Sample()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;

        if (GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid == 0)
            return null;

        try
        {
            using var proc = Process.GetProcessById((int)pid);
            var name = proc.ProcessName;
            if (string.IsNullOrWhiteSpace(name))
                return new ForegroundSnapshot(ForegroundProcessNames.Unknown, null);

            var path = TryGetExePath(pid);
            return new ForegroundSnapshot(name, path);
        }
        catch (ArgumentException) { return null; }
        catch (Win32Exception ex)
        {
            Log.Debug(ex, "Access denied reading foreground pid={Pid}", pid);
            return new ForegroundSnapshot(ForegroundProcessNames.AccessDenied, null);
        }
        catch (InvalidOperationException ex)
        {
            Log.Debug(ex, "Foreground process already exited pid={Pid}", pid);
            return null;
        }
    }

    /// <summary>
    /// QueryFullProcessImageName으로 exe 경로를 조회. 실패 시 null.
    /// MainModule 기반 조회와 달리 elevated/protected 프로세스에도 대부분 작동한다.
    /// </summary>
    static string? TryGetExePath(uint pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;

        try
        {
            var buffer = new StringBuilder(1024);
            uint size = (uint)buffer.Capacity;
            if (QueryFullProcessImageName(handle, 0, buffer, ref size))
                return buffer.ToString(0, (int)size);
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
