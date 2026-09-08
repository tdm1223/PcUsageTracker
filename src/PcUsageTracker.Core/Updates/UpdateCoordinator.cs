using System.Globalization;

namespace PcUsageTracker.Core.Updates;

public enum UpdateAvailability
{
    UpToDate,
    Available,
}

public sealed record UpdateCheckResult(
    UpdateAvailability Availability,
    AppVersion CurrentVersion,
    GitHubRelease LatestRelease)
{
    public bool IsUpdateAvailable => Availability == UpdateAvailability.Available;
}

public sealed record PreparedUpdate(
    AppVersion Version,
    string FilePath,
    long Size,
    string Sha256Hex);

/// <summary>Coordinates version comparison, verified staging, daily scheduling, and cleanup.</summary>
public sealed class UpdateCoordinator
{
    public static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromDays(1);
    public static readonly TimeSpan StagingRetention = TimeSpan.FromDays(7);

    readonly IUpdateReleaseClient _releaseClient;
    readonly AppVersion _currentVersion;
    readonly string _stagingDirectory;

    public UpdateCoordinator(
        IUpdateReleaseClient releaseClient,
        AppVersion currentVersion,
        string stagingDirectory)
    {
        _releaseClient = releaseClient ?? throw new ArgumentNullException(nameof(releaseClient));
        _currentVersion = currentVersion;
        _stagingDirectory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(stagingDirectory)
                ? throw new ArgumentException("A staging directory is required.", nameof(stagingDirectory))
                : stagingDirectory);
    }

    public string StagingDirectory => _stagingDirectory;
    public AppVersion CurrentVersion => _currentVersion;

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        var release = await _releaseClient.GetLatestStableAsync(cancellationToken).ConfigureAwait(false);
        return new UpdateCheckResult(
            release.Version > _currentVersion ? UpdateAvailability.Available : UpdateAvailability.UpToDate,
            _currentVersion,
            release);
    }

    public async Task<PreparedUpdate> DownloadAsync(
        GitHubRelease release,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (release.Version <= _currentVersion)
            throw new UpdateException("The selected release is not newer than the running application.");
        if (!string.Equals(release.Asset.Name, release.Version.AssetName, StringComparison.Ordinal))
            throw new UpdateException("The selected release asset name is not exact.");

        Directory.CreateDirectory(_stagingDirectory);
        var temporaryPath = Path.Combine(
            _stagingDirectory,
            $".{release.Asset.Name}.{Guid.NewGuid():N}.download");
        var finalPath = Path.Combine(_stagingDirectory, release.Asset.Name);
        try
        {
            await _releaseClient.DownloadVerifiedAsync(release.Asset, temporaryPath, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(temporaryPath);
            if (!info.Exists || info.Length != release.Asset.Size)
                throw new UpdateException("The verified update staging file has an unexpected size.");
            File.Move(temporaryPath, finalPath, overwrite: true);
            return new PreparedUpdate(
                release.Version,
                finalPath,
                release.Asset.Size,
                release.Asset.Sha256Hex);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    public void CleanupStaleStaging(DateTimeOffset nowUtc, string? preservePath = null)
    {
        if (!Directory.Exists(_stagingDirectory)) return;
        var preserved = preservePath is null ? null : Path.GetFullPath(preservePath);
        foreach (var path in Directory.EnumerateFiles(_stagingDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (preserved is not null &&
                    string.Equals(fullPath, preserved, StringComparison.OrdinalIgnoreCase)) continue;
                var fileName = Path.GetFileName(fullPath);
                if (!fileName.EndsWith(".download", StringComparison.OrdinalIgnoreCase) &&
                    !fileName.EndsWith(".accept", StringComparison.OrdinalIgnoreCase) &&
                    !fileName.EndsWith(".ready", StringComparison.OrdinalIgnoreCase) &&
                    !(fileName.StartsWith("PcUsageTracker-v", StringComparison.OrdinalIgnoreCase) &&
                      fileName.EndsWith("-win-x64.exe", StringComparison.OrdinalIgnoreCase))) continue;
                if (nowUtc - File.GetLastWriteTimeUtc(fullPath) < StagingRetention) continue;
                File.Delete(fullPath);
            }
            catch
            {
                // Cleanup is best effort and must never prevent tracking from starting.
            }
        }
    }

    public static bool IsAutomaticCheckDue(string? lastCheckValue, DateTimeOffset nowUtc)
    {
        if (!TryParseStoredCheckTime(lastCheckValue, out var lastCheck)) return true;
        if (lastCheck > nowUtc) return true;
        return nowUtc - lastCheck >= AutomaticCheckInterval;
    }

    public static TimeSpan DelayUntilAutomaticCheck(string? lastCheckValue, DateTimeOffset nowUtc)
    {
        if (IsAutomaticCheckDue(lastCheckValue, nowUtc)) return TimeSpan.Zero;
        TryParseStoredCheckTime(lastCheckValue, out var lastCheck);
        var delay = lastCheck + AutomaticCheckInterval - nowUtc;
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }

    public static string FormatStoredCheckTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    static bool TryParseStoredCheckTime(string? value, out DateTimeOffset result) =>
        DateTimeOffset.TryParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out result);

    static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { }
    }
}
