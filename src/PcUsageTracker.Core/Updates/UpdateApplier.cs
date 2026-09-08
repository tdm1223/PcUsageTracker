using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace PcUsageTracker.Core.Updates;

public sealed record UpdateApplyRequest(
    int OldProcessId,
    string SourcePath,
    string TargetPath,
    string StagingRoot,
    string AcceptanceMarkerPath,
    string ReadyMarkerPath,
    string OriginalUserSid,
    AppVersion ExpectedVersion,
    long ExpectedSize,
    string ExpectedSha256Hex);

public sealed record UpdateApplyResult(bool Succeeded, bool RolledBack, string? Error = null);
public sealed record UpdateHelperLaunchResult(bool Started, bool Accepted, bool Elevated, string? Error = null);
public sealed record UpdateAcceptanceResult(bool Accepted, string? Error = null);
public sealed record UpdateTargetValidation(bool IsValid, string? Error = null);

public interface IUpdateProcessController
{
    Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken);
    int Start(string executablePath, IReadOnlyList<string> arguments, bool elevated);
    Task<UpdateAcceptanceResult> WaitForAcceptanceMarkerAsync(
        string markerPath, int expectedProcessId, TimeSpan timeout, CancellationToken cancellationToken);
    Task<bool> WaitForReadyMarkerAsync(
        string markerPath, int expectedProcessId, TimeSpan timeout, CancellationToken cancellationToken);
    Task StopAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken);
}

public interface IUpdateTargetAccessProbe
{
    bool CanReplace(string targetPath);
}

public interface IUpdateRuntimeInfo
{
    string? CurrentUserSid { get; }
    AppVersion CurrentApplicationVersion { get; }
    string? GetProcessImagePath(int processId);
    string GetFinalPath(string path);
    bool IsSuitableApplicationExecutable(string path, out string? error);
}

public sealed class SystemUpdateTargetAccessProbe : IUpdateTargetAccessProbe
{
    const uint DeleteAccess = 0x00010000;
    const uint ShareRead = 0x00000001;
    const uint ShareWrite = 0x00000002;
    const uint ShareDelete = 0x00000004;
    const uint OpenExisting = 3;

    public bool CanReplace(string targetPath)
    {
        try
        {
            var target = Path.GetFullPath(targetPath);
            var directory = Path.GetDirectoryName(target) ?? throw new IOException("Invalid target directory.");
            var probe = Path.Combine(directory, $".pcut-write-{Guid.NewGuid():N}.tmp");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       1, FileOptions.DeleteOnClose)) { }
            if (File.Exists(probe)) File.Delete(probe);

            if (!OperatingSystem.IsWindows())
            {
                using var stream = new FileStream(target, FileMode.Open, FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete);
                return true;
            }

            using var handle = CreateFileW(
                target, DeleteAccess, ShareRead | ShareWrite | ShareDelete, IntPtr.Zero,
                OpenExisting, 0, IntPtr.Zero);
            if (!handle.IsInvalid) return true;
            // The currently mapped executable may reject a DELETE handle until its process exits.
            // Elevation cannot fix a sharing/lock violation; the helper waits for that process instead.
            return Marshal.GetLastWin32Error() is 32 or 33;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
}

public sealed class SystemUpdateRuntimeInfo : IUpdateRuntimeInfo
{
    public AppVersion CurrentApplicationVersion => AppVersion.Current();

    public string? CurrentUserSid
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return null;
            try { return WindowsIdentity.GetCurrent().User?.Value; }
            catch (Exception ex) when (ex is PlatformNotSupportedException or UnauthorizedAccessException) { return null; }
        }
    }

    public string? GetProcessImagePath(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.MainModule?.FileName is { } path ? GetFinalPath(path) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    public string GetFinalPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        try { return File.ResolveLinkTarget(fullPath, returnFinalTarget: true)?.FullName ?? fullPath; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return fullPath;
        }
    }

    public bool IsSuitableApplicationExecutable(string path, out string? error)
    {
        error = null;
        try
        {
            var fullPath = GetFinalPath(path);
            if (!File.Exists(fullPath) || !string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("The running path is not a Windows application executable.");

            var version = FileVersionInfo.GetVersionInfo(fullPath);
            var originalName = version.OriginalFilename ?? string.Empty;
            var productName = version.ProductName ?? string.Empty;
            var fileName = Path.GetFileName(fullPath);
            if (string.Equals(fileName, "dotnet.exe", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(originalName, "dotnet.exe", StringComparison.OrdinalIgnoreCase) ||
                productName.Contains("Microsoft .NET Host", StringComparison.OrdinalIgnoreCase))
                throw new UpdateException(
                    "Self-update is unavailable when the app is running under the shared dotnet host. " +
                    "Install a published PcUsageTracker executable manually.");

            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
            if (pe.PEHeaders.PEHeader is null ||
                !pe.PEHeaders.CoffHeader.Characteristics.HasFlag(Characteristics.ExecutableImage) ||
                pe.PEHeaders.CoffHeader.Characteristics.HasFlag(Characteristics.Dll))
                throw new UpdateException("The running path is not a replaceable Windows application executable.");

            var identity = string.Join(" ", version.FileDescription, version.ProductName,
                version.InternalName, version.OriginalFilename);
            if (!identity.Contains("PcUsageTracker", StringComparison.OrdinalIgnoreCase))
                throw new UpdateException(
                    "The running executable could not be identified as a PcUsageTracker apphost. " +
                    "Install the release executable manually.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or UpdateException)
        {
            error = ex.Message;
            return false;
        }
    }
}

public sealed class SystemUpdateProcessController : IUpdateProcessController
{
    public async Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        }
        catch (ArgumentException) { return true; }
    }

    public int Start(string executablePath, IReadOnlyList<string> arguments, bool elevated)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory,
        };
        if (elevated) startInfo.Verb = "runas";
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)
                            ?? throw new UpdateException("Windows did not start the update helper.");
        return process.Id;
    }

    public async Task<UpdateAcceptanceResult> WaitForAcceptanceMarkerAsync(
        string markerPath, int expectedProcessId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryReadAcceptance(markerPath, expectedProcessId, out var result)) return result;
            if (!IsRunning(expectedProcessId))
                return new UpdateAcceptanceResult(false, "The update helper exited before accepting the update.");
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        return TryReadAcceptance(markerPath, expectedProcessId, out var final)
            ? final
            : new UpdateAcceptanceResult(false, "The update helper did not accept the update in time.");
    }

    public async Task<bool> WaitForReadyMarkerAsync(
        string markerPath, int expectedProcessId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryReadReadyProcessId(markerPath, out var readyPid) && readyPid == expectedProcessId) return true;
            if (!IsRunning(expectedProcessId)) return false;
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        return TryReadReadyProcessId(markerPath, out var finalPid) && finalPid == expectedProcessId;
    }

    public async Task StopAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited) return;
            process.Kill(entireProcessTree: true);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try { await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }

    static bool IsRunning(int processId)
    {
        try { using var process = Process.GetProcessById(processId); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    static bool TryReadReadyProcessId(string markerPath, out int processId)
    {
        processId = 0;
        try
        {
            return int.TryParse(File.ReadAllText(markerPath).Trim(), NumberStyles.None,
                CultureInfo.InvariantCulture, out processId) && processId > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    static bool TryReadAcceptance(string markerPath, int expectedPid, out UpdateAcceptanceResult result)
    {
        result = new UpdateAcceptanceResult(false);
        try
        {
            var lines = File.ReadAllLines(markerPath);
            if (lines.Length < 2 ||
                !int.TryParse(lines[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ||
                pid != expectedPid) return false;
            result = string.Equals(lines[0], "accepted", StringComparison.Ordinal)
                ? new UpdateAcceptanceResult(true)
                : new UpdateAcceptanceResult(false,
                    lines.Length >= 3 ? string.Join(Environment.NewLine, lines.Skip(2)) : "The update helper rejected the update.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}

/// <summary>Validates, handshakes, and atomically applies a verified self-update.</summary>
public sealed class UpdateApplier
{
    public const string ApplyFlag = "--apply-update";
    public const string ReadyFlag = "--update-ready";
    public const string BackupFileName = "PcUsageTracker.App.previous.exe";
    public static readonly TimeSpan AcceptanceTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan AcceptanceAcknowledgementTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan OldProcessExitTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(45);

    readonly IUpdateProcessController _processes;
    readonly IUpdateTargetAccessProbe _accessProbe;
    readonly IUpdateRuntimeInfo _runtime;

    public UpdateApplier(
        IUpdateProcessController? processes = null,
        IUpdateTargetAccessProbe? accessProbe = null,
        IUpdateRuntimeInfo? runtime = null)
    {
        _processes = processes ?? new SystemUpdateProcessController();
        _accessProbe = accessProbe ?? new SystemUpdateTargetAccessProbe();
        _runtime = runtime ?? new SystemUpdateRuntimeInfo();
    }

    public static bool IsApplyInvocation(IReadOnlyList<string> arguments) =>
        arguments.Count > 0 && string.Equals(arguments[0], ApplyFlag, StringComparison.OrdinalIgnoreCase);

    public UpdateTargetValidation ValidateCurrentTarget(string targetPath, int processId)
    {
        try
        {
            if (processId <= 0) throw new UpdateException("The running process ID is invalid.");
            var target = _runtime.GetFinalPath(targetPath);
            var processPath = _runtime.GetProcessImagePath(processId);
            if (processPath is null ||
                !string.Equals(_runtime.GetFinalPath(processPath), target, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("The running process image does not match the update target.");
            if (!_runtime.IsSuitableApplicationExecutable(target, out var suitabilityError))
                throw new UpdateException(suitabilityError ?? "The running executable is not suitable for self-update.");
            if (string.Equals(Path.GetFileName(target), BackupFileName, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("The running executable uses the reserved update backup filename.");
            return new UpdateTargetValidation(true);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UpdateException)
        {
            return new UpdateTargetValidation(false, ex.Message);
        }
    }

    public async Task<UpdateHelperLaunchResult> LaunchHelperAndWaitForAcceptanceAsync(
        PreparedUpdate update,
        string targetPath,
        int oldProcessId,
        string stagingRoot,
        string acceptanceMarkerPath,
        string readyMarkerPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        var elevated = false;
        var helperPid = 0;
        try
        {
            var source = _runtime.GetFinalPath(update.FilePath);
            var target = _runtime.GetFinalPath(targetPath);
            var root = _runtime.GetFinalPath(stagingRoot);
            var acceptance = Path.GetFullPath(acceptanceMarkerPath);
            var ready = Path.GetFullPath(readyMarkerPath);
            var targetValidation = ValidateCurrentTarget(target, oldProcessId);
            if (!targetValidation.IsValid) throw new UpdateException(targetValidation.Error!);
            ValidateStagingPaths(source, target, root, acceptance, ready);
            if (oldProcessId != Environment.ProcessId)
                throw new UpdateException("The update target is not the current process.");
            if (!AppVersion.TryParseAssetName(Path.GetFileName(source), out var sourceVersion) ||
                sourceVersion != update.Version)
                throw new UpdateException("The staged update filename does not match its release version.");
            VerifyPayload(source, update.Size, update.Sha256Hex);
            var sid = _runtime.CurrentUserSid;
            if (string.IsNullOrWhiteSpace(sid))
                throw new UpdateException("The current Windows user identity could not be verified.");

            TryDelete(acceptance);
            TryDelete(ready);
            elevated = !_accessProbe.CanReplace(target);
            var arguments = BuildApplyArguments(
                oldProcessId, target, root, acceptance, ready, sid,
                update.Version, update.Size, update.Sha256Hex);
            helperPid = _processes.Start(source, arguments, elevated);
            var accepted = await _processes.WaitForAcceptanceMarkerAsync(
                acceptance, helperPid, AcceptanceTimeout, cancellationToken).ConfigureAwait(false);
            TryDelete(acceptance);
            if (!accepted.Accepted)
            {
                await _processes.StopAsync(helperPid, TimeSpan.FromSeconds(2), CancellationToken.None)
                    .ConfigureAwait(false);
                return new UpdateHelperLaunchResult(true, false, elevated,
                    $"{accepted.Error ?? "The update helper rejected the update."} " +
                    "Install the release manually; a different account used for UAC cannot apply it automatically.");
            }
            return new UpdateHelperLaunchResult(true, true, elevated);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new UpdateHelperLaunchResult(false, false, true,
                "Administrator approval was canceled. The current version is still running.");
        }
        catch (Exception ex)
        {
            if (helperPid > 0)
                await _processes.StopAsync(helperPid, TimeSpan.FromSeconds(2), CancellationToken.None)
                    .ConfigureAwait(false);
            return new UpdateHelperLaunchResult(helperPid > 0, false, elevated, ex.Message);
        }
    }

    public UpdateAcceptanceResult ValidateAndSignalAcceptance(UpdateApplyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            ValidateRequest(request);
            if (!string.Equals(_runtime.CurrentUserSid, request.OriginalUserSid, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException(
                    "The administrator account differs from the account running PcUsageTracker. " +
                    "The automatic update was canceled; install the release manually from the original account.");
            var processPath = _runtime.GetProcessImagePath(request.OldProcessId);
            if (processPath is null ||
                !string.Equals(_runtime.GetFinalPath(processPath), _runtime.GetFinalPath(request.TargetPath),
                    StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("The old process image does not match the requested update target.");
            if (!_runtime.IsSuitableApplicationExecutable(request.TargetPath, out var suitabilityError))
                throw new UpdateException(suitabilityError ?? "The update target is not a PcUsageTracker apphost.");
            if (!_accessProbe.CanReplace(request.TargetPath))
                throw new UpdateException(
                    "The update helper cannot safely replace this executable. Install the release manually.");
            if (_runtime.CurrentApplicationVersion != request.ExpectedVersion)
                throw new UpdateException(
                    "The update helper's embedded version does not match the requested release version.");
            VerifyPayload(request.SourcePath, request.ExpectedSize, request.ExpectedSha256Hex);
            SignalAcceptance(request.AcceptanceMarkerPath, true, Environment.ProcessId, null);
            return new UpdateAcceptanceResult(true);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or UpdateException)
        {
            TrySignalRejection(request, ex.Message);
            return new UpdateAcceptanceResult(false, ex.Message);
        }
    }

    public static async Task<bool> WaitForAcceptanceAcknowledgementAsync(
        string markerPath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(markerPath)) return true;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        return !File.Exists(markerPath);
    }

    public async Task<UpdateApplyResult> ApplyAsync(UpdateApplyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? adjacentTemporary = null;
        var replaced = false;
        var replacementStartedPid = 0;
        var oldProcessExited = false;
        try
        {
            oldProcessExited = await _processes.WaitForExitAsync(
                request.OldProcessId, OldProcessExitTimeout, cancellationToken).ConfigureAwait(false);
            if (!oldProcessExited)
                return new UpdateApplyResult(false, false,
                    "The running application did not exit; it was left unchanged.");

            // All fields were accepted before the parent exited. Revalidate only after that exit so
            // any post-acceptance tampering enters the restart/rollback path instead of stranding it.
            ValidateRequest(request);
            VerifyPayload(request.SourcePath, request.ExpectedSize, request.ExpectedSha256Hex);
            TryDelete(request.ReadyMarkerPath);
            var targetDirectory = Path.GetDirectoryName(request.TargetPath)!;
            adjacentTemporary = Path.Combine(
                targetDirectory, $".{Path.GetFileName(request.TargetPath)}.{Guid.NewGuid():N}.update.tmp");
            var backupPath = Path.Combine(targetDirectory, BackupFileName);
            File.Copy(request.SourcePath, adjacentTemporary, overwrite: false);
            VerifyPayload(adjacentTemporary, request.ExpectedSize, request.ExpectedSha256Hex);
            TryDelete(backupPath);
            File.Replace(adjacentTemporary, request.TargetPath, backupPath, ignoreMetadataErrors: true);
            adjacentTemporary = null;
            replaced = true;

            replacementStartedPid = _processes.Start(
                request.TargetPath, [ReadyFlag, request.ReadyMarkerPath], elevated: false);
            if (!await _processes.WaitForReadyMarkerAsync(
                    request.ReadyMarkerPath, replacementStartedPid, ReadyTimeout, cancellationToken)
                    .ConfigureAwait(false))
                throw new UpdateException("The updated application did not report a ready state.");

            TryDelete(request.ReadyMarkerPath);
            return new UpdateApplyResult(true, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!oldProcessExited)
                return new UpdateApplyResult(false, false, ex.Message);
            var rollback = await TryRollbackAndRestartAsync(
                request, replaced, replacementStartedPid, CancellationToken.None).ConfigureAwait(false);
            return new UpdateApplyResult(false, rollback, ex.Message);
        }
        finally
        {
            if (adjacentTemporary is not null) TryDelete(adjacentTemporary);
        }
    }

    public static bool TryParseApplyArguments(
        IReadOnlyList<string> arguments, string sourcePath,
        out UpdateApplyRequest? request, out string? error)
    {
        request = null;
        error = null;
        if (!IsApplyInvocation(arguments)) return false;
        try
        {
            if (arguments.Count != 21 ||
                !Label(arguments, 1, "--old-pid") || !Label(arguments, 3, "--target") ||
                !Label(arguments, 5, "--staging-root") || !Label(arguments, 7, "--accept-marker") ||
                !Label(arguments, 9, "--ready-marker") || !Label(arguments, 11, "--user-sid") ||
                !Label(arguments, 13, "--version") || !Label(arguments, 15, "--size") ||
                !Label(arguments, 17, "--sha256") || !Label(arguments, 19, "--protocol") ||
                arguments[20] != "1" || !AppVersion.TryParse(arguments[14], out var version) ||
                !int.TryParse(arguments[2], NumberStyles.None, CultureInfo.InvariantCulture, out var oldPid) ||
                !long.TryParse(arguments[16], NumberStyles.None, CultureInfo.InvariantCulture, out var size))
                throw new UpdateException("The update helper arguments are malformed.");

            var parsed = new UpdateApplyRequest(
                oldPid, Path.GetFullPath(sourcePath), Path.GetFullPath(arguments[4]),
                Path.GetFullPath(arguments[6]), Path.GetFullPath(arguments[8]),
                Path.GetFullPath(arguments[10]), arguments[12], version, size, arguments[18].ToLowerInvariant());
            ValidateRequest(parsed);
            request = parsed;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UpdateException)
        {
            error = ex.Message;
            return true;
        }
    }

    public static bool TryGetReadyMarker(IReadOnlyList<string> arguments, string stagingRoot, out string? markerPath)
    {
        markerPath = null;
        if (arguments.Count != 2 || !string.Equals(arguments[0], ReadyFlag, StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            var marker = Path.GetFullPath(arguments[1]);
            if (!marker.EndsWith(".ready", StringComparison.OrdinalIgnoreCase) ||
                !IsDirectChild(Path.GetFullPath(stagingRoot), marker)) return false;
            markerPath = marker;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException) { return false; }
    }

    public static void SignalReady(string markerPath, int processId) =>
        WriteMarker(markerPath, processId.ToString(CultureInfo.InvariantCulture));

    static IReadOnlyList<string> BuildApplyArguments(
        int oldProcessId, string targetPath, string stagingRoot, string acceptanceMarkerPath,
        string readyMarkerPath, string userSid, AppVersion version, long size, string digest) =>
        [
            ApplyFlag,
            "--old-pid", oldProcessId.ToString(CultureInfo.InvariantCulture),
            "--target", targetPath,
            "--staging-root", stagingRoot,
            "--accept-marker", acceptanceMarkerPath,
            "--ready-marker", readyMarkerPath,
            "--user-sid", userSid,
            "--version", version.ToString(),
            "--size", size.ToString(CultureInfo.InvariantCulture),
            "--sha256", digest,
            "--protocol", "1",
        ];

    async Task<bool> TryRollbackAndRestartAsync(
        UpdateApplyRequest request, bool replaced, int replacementProcessId, CancellationToken cancellationToken)
    {
        try
        {
            if (replacementProcessId > 0)
                await _processes.StopAsync(
                    replacementProcessId, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            if (replaced)
            {
                var backupPath = Path.Combine(Path.GetDirectoryName(request.TargetPath)!, BackupFileName);
                if (!File.Exists(backupPath)) return false;
                File.Replace(backupPath, request.TargetPath, destinationBackupFileName: null,
                    ignoreMetadataErrors: true);
            }
            _processes.Start(request.TargetPath, Array.Empty<string>(), elevated: false);
            return true;
        }
        catch { return false; }
    }

    static void ValidateRequest(UpdateApplyRequest request)
    {
        if (request.OldProcessId <= 0 || request.OldProcessId == Environment.ProcessId)
            throw new UpdateException("The old application process ID is invalid.");
        if (!Path.IsPathFullyQualified(request.SourcePath) || !Path.IsPathFullyQualified(request.TargetPath) ||
            !Path.IsPathFullyQualified(request.StagingRoot) ||
            !Path.IsPathFullyQualified(request.AcceptanceMarkerPath) ||
            !Path.IsPathFullyQualified(request.ReadyMarkerPath))
            throw new UpdateException("The update paths must be fully qualified.");
        ValidateStagingPaths(request.SourcePath, request.TargetPath, request.StagingRoot,
            request.AcceptanceMarkerPath, request.ReadyMarkerPath);
        if (!AppVersion.TryParseAssetName(Path.GetFileName(request.SourcePath), out var sourceVersion) ||
            sourceVersion != request.ExpectedVersion)
            throw new UpdateException("The update helper filename does not match the requested release version.");
        if (!File.Exists(request.SourcePath) || !File.Exists(request.TargetPath))
            throw new UpdateException("The update payload or target executable no longer exists.");
        if (!request.TargetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileName(request.TargetPath), BackupFileName, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException("The update target executable is invalid.");
        if (string.IsNullOrWhiteSpace(request.OriginalUserSid) || request.OriginalUserSid.Length > 184)
            throw new UpdateException("The original Windows user identity is invalid.");
        if (request.ExpectedSize <= 0 || request.ExpectedSize > GitHubReleaseClient.MaximumAssetBytes ||
            request.ExpectedSha256Hex.Length != 64 || request.ExpectedSha256Hex.Any(c => !Uri.IsHexDigit(c)))
            throw new UpdateException("The update verification values are invalid.");
    }

    static void ValidateStagingPaths(
        string source, string target, string root, string acceptance, string ready)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        source = Path.GetFullPath(source);
        target = Path.GetFullPath(target);
        acceptance = Path.GetFullPath(acceptance);
        ready = Path.GetFullPath(ready);
        if (!Directory.Exists(root) || !IsDirectChild(root, source) || !IsDirectChild(root, acceptance) ||
            !IsDirectChild(root, ready) || IsPathInside(root, target) ||
            string.Equals(source, target, StringComparison.OrdinalIgnoreCase) ||
            !acceptance.EndsWith(".accept", StringComparison.OrdinalIgnoreCase) ||
            !ready.EndsWith(".ready", StringComparison.OrdinalIgnoreCase))
            throw new UpdateException(
                "The helper payload and markers must be direct children of the staging folder, and the target must be outside it.");
    }

    static void VerifyPayload(string path, long expectedSize, string expectedSha256Hex)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != expectedSize)
            throw new UpdateException("The staged update size changed before installation.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        var actual = SHA256.HashData(stream);
        var expected = Convert.FromHexString(expectedSha256Hex);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            throw new UpdateException("The staged update digest changed before installation.");
    }

    static bool Label(IReadOnlyList<string> arguments, int index, string value) =>
        string.Equals(arguments[index], value, StringComparison.OrdinalIgnoreCase);

    static bool IsDirectChild(string root, string candidate) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(candidate)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase);

    static bool IsPathInside(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return relative.Length > 0 && relative != "." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !string.Equals(relative, "..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    static void SignalAcceptance(string markerPath, bool accepted, int processId, string? error) =>
        WriteMarker(markerPath,
            $"{(accepted ? "accepted" : "rejected")}{Environment.NewLine}" +
            $"{processId.ToString(CultureInfo.InvariantCulture)}{Environment.NewLine}{error ?? string.Empty}");

    static void TrySignalRejection(UpdateApplyRequest request, string error)
    {
        try
        {
            ValidateStagingPaths(request.SourcePath, request.TargetPath, request.StagingRoot,
                request.AcceptanceMarkerPath, request.ReadyMarkerPath);
            SignalAcceptance(request.AcceptanceMarkerPath, false, Environment.ProcessId, error);
        }
        catch { }
    }

    static void WriteMarker(string markerPath, string contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markerPath);
        var fullPath = Path.GetFullPath(markerPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, contents);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally { TryDelete(temporary); }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { }
    }
}
