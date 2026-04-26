using Microsoft.Win32;
using Serilog;

namespace PcUsageTracker.App.Autostart;

/// <summary>
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Run 등록/해제.
/// 그룹 정책 등으로 쓰기 실패 시 예외 대신 false/로그로 처리.
/// </summary>
internal static class RunKeyRegistrar
{
    const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "PcUsageTracker";

    public static bool IsRegistered()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(KeyPath);
            return k?.GetValue(ValueName) is string s && !string.IsNullOrEmpty(s);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "RunKeyRegistrar.IsRegistered failed");
            return false;
        }
    }

    public static string? GetRegisteredExePath()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (k?.GetValue(ValueName) is not string s) return null;
            return ParseExePath(s);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "RunKeyRegistrar.GetRegisteredExePath failed");
            return null;
        }
    }

    // Run 키 값 포맷: "<exe>" <args...>  또는  <exe> <args...>  또는 단순 <exe>.
    // 닫는 따옴표가 없는 손상된 값은 null로 방어 처리.
    internal static string? ParseExePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        if (trimmed[0] == '"')
        {
            int end = trimmed.IndexOf('"', 1);
            return end > 1 ? trimmed.Substring(1, end - 1) : null;
        }
        int space = trimmed.IndexOf(' ');
        return space > 0 ? trimmed[..space] : trimmed;
    }

    public static bool ReconcilePath(string currentExePath)
    {
        var registered = GetRegisteredExePath();
        if (registered is null) return false;
        if (string.Equals(registered, currentExePath, StringComparison.OrdinalIgnoreCase)) return false;
        Log.Information("Autostart path drift: registered={Registered} current={Current} — updating",
            registered, currentExePath);
        return Register(currentExePath);
    }

    public static bool Register(string exePath)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true)
                          ?? Registry.CurrentUser.CreateSubKey(KeyPath);
            if (k is null) return false;
            k.SetValue(ValueName, $"\"{exePath}\" --startup", RegistryValueKind.String);
            Log.Information("Autostart registered: {Path}", exePath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "RunKeyRegistrar.Register failed");
            return false;
        }
    }

    public static bool Unregister()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            if (k is null) return true;
            k.DeleteValue(ValueName, throwOnMissingValue: false);
            Log.Information("Autostart unregistered");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "RunKeyRegistrar.Unregister failed");
            return false;
        }
    }
}
