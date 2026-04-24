using System.Diagnostics;
using Serilog;

namespace PcUsageTracker.App;

static class Program
{
    const string MutexName = @"Global\PcUsageTracker.SingleInstance.v1";

    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, name: MutexName, out var createdNew);
        if (!createdNew)
        {
            // Second instance: M5에서 기존 인스턴스 팝업 열기 구현 예정. M1은 조용히 종료.
            return;
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var logDir = Path.Combine(appData, "PcUsageTracker", "logs");
        Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                path: Path.Combine(logDir, "app-.log"),
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 14)
            .CreateLogger();

        try
        {
            Log.Information("PcUsageTracker starting (pid={Pid}, args={Args})", Environment.ProcessId, args);
            var startedFromLogin = args.Any(a => string.Equals(a, "--startup", StringComparison.OrdinalIgnoreCase));

            ApplicationConfiguration.Initialize();
            using var trayContext = new TrayContext(startedFromLogin);
            Application.Run(trayContext);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Fatal error in main loop");
            throw;
        }
        finally
        {
            Log.Information("PcUsageTracker shutting down");
            Log.CloseAndFlush();
        }
    }
}
