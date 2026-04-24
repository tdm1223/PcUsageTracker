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
