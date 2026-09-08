using System.Diagnostics;
using PcUsageTracker.Core.Updates;
using Serilog;

namespace PcUsageTracker.App;

static class Program
{
    const string MutexName = @"Global\PcUsageTracker.SingleInstance.v1";

    [STAThread]
    static int Main(string[] args)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dataDir = Path.Combine(appData, "PcUsageTracker");
        var logDir = Path.Combine(dataDir, "logs");
        Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                path: Path.Combine(logDir, "app-.log"),
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 14,
                shared: true)
            .CreateLogger();

        // The downloaded executable is the update helper. It must run alongside the old process,
        // so helper mode is handled before acquiring the application's single-instance mutex.
        if (UpdateApplier.IsApplyInvocation(args))
        {
            try
            {
                var sourcePath = Environment.ProcessPath;
                string? parseError = sourcePath is null ? "Cannot resolve the helper executable path." : null;
                UpdateApplyRequest? request = null;
                if (sourcePath is null ||
                    !UpdateApplier.TryParseApplyArguments(
                        args,
                        sourcePath,
                        out request,
                        out parseError) || request is null)
                {
                    Log.Error("Update helper invocation rejected: {Error}", parseError ?? "invalid arguments");
                    return 2;
                }

                var applier = new UpdateApplier();
                var acceptance = applier.ValidateAndSignalAcceptance(request);
                if (!acceptance.Accepted)
                {
                    Log.Error("Update helper rejected the request: {Error}", acceptance.Error);
                    return 2;
                }
                if (!UpdateApplier.WaitForAcceptanceAcknowledgementAsync(
                        request.AcceptanceMarkerPath,
                        UpdateApplier.AcceptanceAcknowledgementTimeout,
                        CancellationToken.None).GetAwaiter().GetResult())
                {
                    Log.Error("Update parent did not acknowledge helper acceptance; update canceled safely");
                    return 2;
                }

                var result = applier.ApplyAsync(request, CancellationToken.None)
                    .GetAwaiter().GetResult();
                if (!result.Succeeded)
                    Log.Error("Update apply failed (rolledBack={RolledBack}): {Error}",
                        result.RolledBack, result.Error);
                else
                    Log.Information("Update applied successfully to {Target}", request.TargetPath);
                return result.Succeeded ? 0 : 3;
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Fatal error in update helper");
                return 4;
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        using var mutex = new Mutex(initiallyOwned: true, name: MutexName, out var createdNew);
        if (!createdNew)
        {
            Log.Information("Another PcUsageTracker instance is already running");
            Log.CloseAndFlush();
            return 0;
        }

        try
        {
            Log.Information("PcUsageTracker starting (pid={Pid}, args={Args})", Environment.ProcessId, args);
            var startedFromLogin = args.Any(a => string.Equals(a, "--startup", StringComparison.OrdinalIgnoreCase));
            UpdateApplier.TryGetReadyMarker(
                args,
                Path.Combine(dataDir, "updates"),
                out var readyMarker);

            ApplicationConfiguration.Initialize();
            using var trayContext = new TrayContext(startedFromLogin);
            if (readyMarker is not null)
            {
                UpdateApplier.SignalReady(readyMarker, Environment.ProcessId);
                Log.Information("Update ready marker written: {Marker}", readyMarker);
            }
            Application.Run(trayContext);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Fatal error in main loop");
            return 1;
        }
        finally
        {
            Log.Information("PcUsageTracker shutting down");
            Log.CloseAndFlush();
        }
    }
}
