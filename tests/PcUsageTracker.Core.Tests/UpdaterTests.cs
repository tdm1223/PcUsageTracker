using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using PcUsageTracker.Core.Updates;

namespace PcUsageTracker.Core.Tests;

public sealed class UpdaterTests : IDisposable
{
    readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(), $"pcut-updater-{Guid.NewGuid():N}");

    public UpdaterTests() => Directory.CreateDirectory(_temporaryDirectory);

    public void Dispose()
    {
        try { Directory.Delete(_temporaryDirectory, recursive: true); }
        catch { }
    }

    [Theory]
    [InlineData("v0.1.9", 0, 1, 9)]
    [InlineData("12.34.56", 12, 34, 56)]
    public void app_version_parses_and_formats_stable_versions(
        string text, int major, int minor, int patch)
    {
        AppVersion.TryParse(text, out var version).Should().BeTrue();
        version.Should().Be(new AppVersion(major, minor, patch));
        version.TagName.Should().Be($"v{major}.{minor}.{patch}");
        version.AssetName.Should().Be($"PcUsageTracker-v{major}.{minor}.{patch}-win-x64.exe");
    }

    [Theory]
    [InlineData("v1.2")]
    [InlineData("v1.2.3-beta")]
    [InlineData("V1.2.3")]
    [InlineData("v01.2.3")]
    [InlineData("")]
    public void app_version_rejects_non_release_syntax(string text) =>
        AppVersion.TryParse(text, out _).Should().BeFalse();

    [Fact]
    public void embedded_build_version_is_release_compatible()
    {
        var current = AppVersion.Current(typeof(UpdaterTests).Assembly);
        AppVersion.TryParse(current.ToString(), out var reparsed).Should().BeTrue();
        reparsed.Should().Be(current);
    }

    [Fact]
    public async Task github_client_selects_exact_stable_asset_and_digest()
    {
        var bytes = Encoding.UTF8.GetBytes("verified executable");
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        using var http = CreateHttpClient(_ => JsonResponse(ReleaseJson("v0.2.0", bytes.Length, digest)));
        var client = new GitHubReleaseClient(http);

        var release = await client.GetLatestStableAsync(CancellationToken.None);

        release.Version.Should().Be(new AppVersion(0, 2, 0));
        release.Asset.Name.Should().Be("PcUsageTracker-v0.2.0-win-x64.exe");
        release.Asset.Size.Should().Be(bytes.Length);
        release.Asset.Sha256Hex.Should().Be(digest);
        release.ReleasePage.Should().Be(new Uri("https://github.com/tdm1223/PcUsageTracker/releases/tag/v0.2.0"));
    }

    [Theory]
    [InlineData("v0.2.0-beta", false, false, "stable vMAJOR.MINOR.PATCH")]
    [InlineData("v0.2.0", true, false, "stable published")]
    [InlineData("v0.2.0", false, true, "stable published")]
    public async Task github_client_rejects_unstable_or_unpublished_release(
        string tag, bool draft, bool prerelease, string expected)
    {
        var bytes = Encoding.UTF8.GetBytes("payload");
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        using var http = CreateHttpClient(_ =>
            JsonResponse(ReleaseJson(tag, bytes.Length, digest, draft, prerelease)));
        var client = new GitHubReleaseClient(http);

        var action = () => client.GetLatestStableAsync(CancellationToken.None);

        await action.Should().ThrowAsync<UpdateException>().WithMessage($"*{expected}*");
    }

    [Fact]
    public async Task github_client_requires_exact_asset_and_sha256_digest()
    {
        const string json = """
            {
              "tag_name":"v0.2.0", "draft":false, "prerelease":false,
              "assets":[{
                "name":"PcUsageTracker.exe",
                "browser_download_url":"https://github.com/tdm1223/PcUsageTracker/releases/download/v0.2.0/PcUsageTracker.exe",
                "size":10, "digest":null
              }]
            }
            """;
        using var http = CreateHttpClient(_ => JsonResponse(json));
        var client = new GitHubReleaseClient(http);

        var action = () => client.GetLatestStableAsync(CancellationToken.None);

        await action.Should().ThrowAsync<UpdateException>().WithMessage("*exactly one*");
    }

    [Fact]
    public async Task github_client_rejects_exact_asset_when_digest_is_missing()
    {
        const string json = """
            {
              "tag_name":"v0.2.0", "draft":false, "prerelease":false,
              "assets":[{
                "name":"PcUsageTracker-v0.2.0-win-x64.exe",
                "browser_download_url":"https://github.com/tdm1223/PcUsageTracker/releases/download/v0.2.0/PcUsageTracker-v0.2.0-win-x64.exe",
                "size":10, "digest":null
              }]
            }
            """;
        using var http = CreateHttpClient(_ => JsonResponse(json));
        var client = new GitHubReleaseClient(http);

        var action = () => client.GetLatestStableAsync(CancellationToken.None);

        await action.Should().ThrowAsync<UpdateException>().WithMessage("*digest*");
    }

    [Fact]
    public async Task github_client_reports_rate_limit_reset()
    {
        using var http = CreateHttpClient(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.Add("X-RateLimit-Reset", "1893456000");
            return response;
        });
        var client = new GitHubReleaseClient(http);

        var action = () => client.GetLatestStableAsync(CancellationToken.None);

        await action.Should().ThrowAsync<UpdateException>()
            .WithMessage("*rate-limited*").WithMessage("*2030*");
    }

    [Fact]
    public async Task github_client_wraps_malformed_json_as_update_error()
    {
        using var http = CreateHttpClient(_ => JsonResponse("{not-json"));
        var client = new GitHubReleaseClient(http);

        var action = () => client.GetLatestStableAsync(CancellationToken.None);

        await action.Should().ThrowAsync<UpdateException>().WithMessage("*malformed*");
    }

    [Fact]
    public async Task download_enforces_exact_size_and_sha256_and_removes_bad_file()
    {
        var bytes = Encoding.UTF8.GetBytes("download bytes");
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        using var http = CreateHttpClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        });
        var client = new GitHubReleaseClient(http);
        var goodPath = Path.Combine(_temporaryDirectory, "good.download");
        var asset = new GitHubReleaseAsset(
            "PcUsageTracker-v0.2.0-win-x64.exe",
            new Uri("https://github.com/download/update.exe"),
            bytes.Length,
            digest);

        await client.DownloadVerifiedAsync(asset, goodPath, CancellationToken.None);
        File.ReadAllBytes(goodPath).Should().Equal(bytes);

        var badPath = Path.Combine(_temporaryDirectory, "bad.download");
        var badAsset = asset with { Sha256Hex = new string('0', 64) };
        var action = () => client.DownloadVerifiedAsync(badAsset, badPath, CancellationToken.None);
        await action.Should().ThrowAsync<UpdateException>().WithMessage("*digest*");
        File.Exists(badPath).Should().BeFalse();
    }

    [Fact]
    public async Task canceled_download_leaves_no_partial_staging_file()
    {
        var bytes = Encoding.UTF8.GetBytes("payload");
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        using var http = CreateHttpClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        });
        var client = new GitHubReleaseClient(http);
        var path = Path.Combine(_temporaryDirectory, "canceled.download");
        var asset = new GitHubReleaseAsset(
            "PcUsageTracker-v0.2.0-win-x64.exe",
            new Uri("https://github.com/download/update.exe"),
            bytes.Length,
            digest);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var action = () => client.DownloadVerifiedAsync(asset, path, cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task coordinator_compares_versions_and_stages_exact_filename()
    {
        var bytes = Encoding.UTF8.GetBytes("new version");
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var release = TestRelease(new AppVersion(0, 2, 0), bytes, digest);
        var fake = new FakeReleaseClient(release, bytes);
        var coordinator = new UpdateCoordinator(fake, new AppVersion(0, 1, 9), _temporaryDirectory);

        var check = await coordinator.CheckAsync(CancellationToken.None);
        var prepared = await coordinator.DownloadAsync(check.LatestRelease, CancellationToken.None);

        check.IsUpdateAvailable.Should().BeTrue();
        prepared.FilePath.Should().Be(Path.Combine(_temporaryDirectory, release.Asset.Name));
        File.ReadAllBytes(prepared.FilePath).Should().Equal(bytes);
    }

    [Fact]
    public void automatic_schedule_is_daily_and_handles_bad_or_future_values()
    {
        var now = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        var recent = UpdateCoordinator.FormatStoredCheckTime(now.AddHours(-23));
        var due = UpdateCoordinator.FormatStoredCheckTime(now.AddHours(-24));

        UpdateCoordinator.IsAutomaticCheckDue(recent, now).Should().BeFalse();
        UpdateCoordinator.DelayUntilAutomaticCheck(recent, now).Should().Be(TimeSpan.FromHours(1));
        UpdateCoordinator.IsAutomaticCheckDue(due, now).Should().BeTrue();
        UpdateCoordinator.IsAutomaticCheckDue("corrupt", now).Should().BeTrue();
        UpdateCoordinator.IsAutomaticCheckDue(
            UpdateCoordinator.FormatStoredCheckTime(now.AddMinutes(1)), now).Should().BeTrue();
    }

    [Fact]
    public void cleanup_removes_only_stale_updater_files()
    {
        var fake = new FakeReleaseClient(
            TestRelease(new AppVersion(0, 2, 0), [1], new string('0', 64)), [1]);
        var coordinator = new UpdateCoordinator(fake, new AppVersion(0, 1, 0), _temporaryDirectory);
        var stale = Path.Combine(_temporaryDirectory, "PcUsageTracker-v0.1.0-win-x64.exe");
        var recent = Path.Combine(_temporaryDirectory, "ready-new.ready");
        var unrelated = Path.Combine(_temporaryDirectory, "keep.txt");
        File.WriteAllText(stale, "old");
        File.WriteAllText(recent, "new");
        File.WriteAllText(unrelated, "keep");
        var now = DateTimeOffset.UtcNow;
        File.SetLastWriteTimeUtc(stale, now.AddDays(-8).UtcDateTime);

        coordinator.CleanupStaleStaging(now);

        File.Exists(stale).Should().BeFalse();
        File.Exists(recent).Should().BeTrue();
        File.Exists(unrelated).Should().BeTrue();
    }

    [Fact]
    public async Task applier_replaces_target_and_keeps_fixed_backup_after_ready_signal()
    {
        var staging = Path.Combine(_temporaryDirectory, "updates");
        Directory.CreateDirectory(staging);
        var source = Path.Combine(staging, "PcUsageTracker-v0.2.0-win-x64.exe");
        var targetDirectory = Path.Combine(_temporaryDirectory, "installed");
        Directory.CreateDirectory(targetDirectory);
        var target = Path.Combine(targetDirectory, "RenamedTracker.exe");
        var acceptance = Path.Combine(staging, "helper.accept");
        var marker = Path.Combine(staging, "ready.ready");
        File.WriteAllText(source, "new executable");
        File.WriteAllText(target, "old executable");
        var bytes = File.ReadAllBytes(source);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var processes = new FakeProcessController { Ready = true };
        var applier = new UpdateApplier(processes, new FixedAccessProbe(true));

        var result = await applier.ApplyAsync(
            new UpdateApplyRequest(1234, source, target, staging, acceptance, marker,
                "S-1-5-21-test", new AppVersion(0, 2, 0), bytes.Length, digest),
            CancellationToken.None);

        result.Should().Be(new UpdateApplyResult(true, false));
        File.ReadAllText(target).Should().Be("new executable");
        File.ReadAllText(Path.Combine(targetDirectory, UpdateApplier.BackupFileName))
            .Should().Be("old executable");
        processes.Starts.Should().ContainSingle(start => start.Path == target && !start.Elevated);
    }

    [Fact]
    public async Task applier_rolls_back_and_restarts_old_target_when_ready_marker_never_appears()
    {
        var staging = Path.Combine(_temporaryDirectory, "updates");
        Directory.CreateDirectory(staging);
        var source = Path.Combine(staging, "PcUsageTracker-v0.2.0-win-x64.exe");
        var targetDirectory = Path.Combine(_temporaryDirectory, "installed");
        Directory.CreateDirectory(targetDirectory);
        var target = Path.Combine(targetDirectory, "PcUsage.exe");
        var acceptance = Path.Combine(staging, "helper.accept");
        var marker = Path.Combine(staging, "ready.ready");
        File.WriteAllText(source, "new executable");
        File.WriteAllText(target, "old executable");
        var bytes = File.ReadAllBytes(source);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var processes = new FakeProcessController { Ready = false };
        var applier = new UpdateApplier(processes, new FixedAccessProbe(true));

        var result = await applier.ApplyAsync(
            new UpdateApplyRequest(1234, source, target, staging, acceptance, marker,
                "S-1-5-21-test", new AppVersion(0, 2, 0), bytes.Length, digest),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.RolledBack.Should().BeTrue();
        File.ReadAllText(target).Should().Be("old executable");
        processes.Stopped.Should().Contain(9001);
        processes.Starts.Should().HaveCount(2);
        processes.Starts[^1].Arguments.Should().BeEmpty();
    }

    [Fact]
    public async Task post_acceptance_payload_tampering_restarts_unchanged_target()
    {
        var fixture = CreateApplyFixture();
        File.AppendAllText(fixture.Source, "tampered");
        var processes = new FakeProcessController();
        var applier = new UpdateApplier(processes, new FixedAccessProbe(true));

        var result = await applier.ApplyAsync(fixture.Request, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.RolledBack.Should().BeTrue();
        processes.WaitForExitCalls.Should().Be(1);
        processes.Starts.Should().ContainSingle().Which.Path.Should().Be(fixture.Target);
        File.ReadAllText(fixture.Target).Should().Be("old executable");
    }

    [Fact]
    public async Task helper_launch_requests_elevation_only_when_target_cannot_be_replaced()
    {
        var staging = Path.Combine(_temporaryDirectory, "updates");
        Directory.CreateDirectory(staging);
        var source = Path.Combine(staging, "PcUsageTracker-v0.2.0-win-x64.exe");
        var target = Path.Combine(_temporaryDirectory, "installed.exe");
        var acceptance = Path.Combine(staging, "helper.accept");
        var marker = Path.Combine(staging, "ready.ready");
        File.WriteAllText(source, "payload");
        File.WriteAllText(target, "old");
        var bytes = File.ReadAllBytes(source);
        var prepared = new PreparedUpdate(
            new AppVersion(0, 2, 0), source, bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        var processes = new FakeProcessController { Acceptance = new UpdateAcceptanceResult(true) };
        var runtime = new FakeRuntime(target, "S-1-5-21-test");
        var applier = new UpdateApplier(processes, new FixedAccessProbe(false), runtime);

        var result = await applier.LaunchHelperAndWaitForAcceptanceAsync(
            prepared, target, Environment.ProcessId, staging, acceptance, marker, CancellationToken.None);

        result.Started.Should().BeTrue();
        result.Accepted.Should().BeTrue();
        result.Elevated.Should().BeTrue();
        processes.Starts.Should().ContainSingle().Which.Elevated.Should().BeTrue();
    }

    [Fact]
    public void helper_arguments_require_direct_staging_children_and_external_target()
    {
        var staging = Path.Combine(_temporaryDirectory, "updates");
        Directory.CreateDirectory(staging);
        var source = Path.Combine(staging, "PcUsageTracker-v0.2.0-win-x64.exe");
        var target = Path.Combine(_temporaryDirectory, "PcUsageTracker.exe");
        var acceptance = Path.Combine(staging, "helper.accept");
        var ready = Path.Combine(staging, "helper.ready");
        File.WriteAllText(source, "new");
        File.WriteAllText(target, "old");
        var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))).ToLowerInvariant();
        string[] ValidArguments(string requestedTarget) =>
        [
            UpdateApplier.ApplyFlag,
            "--old-pid", "1234",
            "--target", requestedTarget,
            "--staging-root", staging,
            "--accept-marker", acceptance,
            "--ready-marker", ready,
            "--user-sid", "S-1-5-21-test",
            "--version", "0.2.0",
            "--size", new FileInfo(source).Length.ToString(),
            "--sha256", digest,
            "--protocol", "1",
        ];

        UpdateApplier.TryParseApplyArguments(
            ValidArguments(target), source, out var request, out var error).Should().BeTrue();
        request.Should().NotBeNull();
        error.Should().BeNull();
        request!.StagingRoot.Should().Be(staging);

        var targetInStaging = Path.Combine(staging, "target.exe");
        File.WriteAllText(targetInStaging, "old");
        UpdateApplier.TryParseApplyArguments(
            ValidArguments(targetInStaging), source, out request, out error).Should().BeTrue();
        request.Should().BeNull();
        error.Should().Contain("outside");

        var nested = Path.Combine(staging, "nested");
        Directory.CreateDirectory(nested);
        var nestedSource = Path.Combine(nested, Path.GetFileName(source));
        File.Copy(source, nestedSource);
        UpdateApplier.TryParseApplyArguments(
            ValidArguments(target), nestedSource, out request, out error).Should().BeTrue();
        request.Should().BeNull();
        error.Should().Contain("direct children");
    }

    [Fact]
    public void helper_acceptance_binds_old_pid_to_target_and_same_windows_user()
    {
        var fixture = CreateApplyFixture();
        var matching = new UpdateApplier(
            new FakeProcessController(), new FixedAccessProbe(true),
            new FakeRuntime(fixture.Target, fixture.Sid));

        matching.ValidateAndSignalAcceptance(fixture.Request).Accepted.Should().BeTrue();
        File.ReadAllText(fixture.Acceptance).Should().StartWith("accepted");

        var wrongProcess = matching = new UpdateApplier(
            new FakeProcessController(), new FixedAccessProbe(true),
            new FakeRuntime(Path.Combine(_temporaryDirectory, "other.exe"), fixture.Sid));
        wrongProcess.ValidateAndSignalAcceptance(fixture.Request).Should().Match<UpdateAcceptanceResult>(
            result => !result.Accepted && result.Error!.Contains("does not match"));

        var otherUser = new UpdateApplier(
            new FakeProcessController(), new FixedAccessProbe(true),
            new FakeRuntime(fixture.Target, "S-1-5-21-other"));
        otherUser.ValidateAndSignalAcceptance(fixture.Request).Should().Match<UpdateAcceptanceResult>(
            result => !result.Accepted && result.Error!.Contains("administrator account"));
        File.ReadAllText(fixture.Acceptance).Should().Contain("install the release manually");
    }

    [Fact]
    public async Task parent_stays_running_when_helper_times_out_or_rejects_acceptance()
    {
        var fixture = CreateApplyFixture();
        var prepared = new PreparedUpdate(
            new AppVersion(0, 2, 0), fixture.Source,
            fixture.Request.ExpectedSize, fixture.Request.ExpectedSha256Hex);

        foreach (var acceptance in new[]
                 {
                     new UpdateAcceptanceResult(false, "timed out"),
                     new UpdateAcceptanceResult(false, "different account; install manually"),
                 })
        {
            var processes = new FakeProcessController { Acceptance = acceptance };
            var applier = new UpdateApplier(
                processes, new FixedAccessProbe(true), new FakeRuntime(fixture.Target, fixture.Sid));

            var result = await applier.LaunchHelperAndWaitForAcceptanceAsync(
                prepared, fixture.Target, Environment.ProcessId, fixture.Staging,
                fixture.Acceptance, fixture.Ready, CancellationToken.None);

            result.Started.Should().BeTrue();
            result.Accepted.Should().BeFalse();
            result.Error.Should().Contain(acceptance.Error);
            result.Error.Should().Contain("Install the release manually");
            processes.Stopped.Should().Contain(9001);
            processes.WaitForExitCalls.Should().Be(0,
                "the parent does not exit or ask the helper to replace anything before acceptance");
            File.ReadAllText(fixture.Target).Should().Be("old executable");
        }
    }

    [Fact]
    public async Task canceled_uac_does_not_accept_update_or_touch_current_target()
    {
        var fixture = CreateApplyFixture();
        var prepared = new PreparedUpdate(
            new AppVersion(0, 2, 0), fixture.Source,
            fixture.Request.ExpectedSize, fixture.Request.ExpectedSha256Hex);
        var processes = new FakeProcessController
        {
            StartException = new System.ComponentModel.Win32Exception(1223),
        };
        var applier = new UpdateApplier(
            processes, new FixedAccessProbe(false), new FakeRuntime(fixture.Target, fixture.Sid));

        var result = await applier.LaunchHelperAndWaitForAcceptanceAsync(
            prepared, fixture.Target, Environment.ProcessId, fixture.Staging,
            fixture.Acceptance, fixture.Ready, CancellationToken.None);

        result.Started.Should().BeFalse();
        result.Accepted.Should().BeFalse();
        result.Elevated.Should().BeTrue();
        result.Error.Should().Contain("canceled");
        processes.WaitForExitCalls.Should().Be(0);
        File.ReadAllText(fixture.Target).Should().Be("old executable");
    }

    [Fact]
    public async Task helper_requires_parent_to_acknowledge_acceptance_before_continuing()
    {
        var marker = Path.Combine(_temporaryDirectory, "acceptance.accept");
        File.WriteAllText(marker, "accepted");

        (await UpdateApplier.WaitForAcceptanceAcknowledgementAsync(
            marker, TimeSpan.Zero, CancellationToken.None)).Should().BeFalse();

        File.Delete(marker);
        (await UpdateApplier.WaitForAcceptanceAcknowledgementAsync(
            marker, TimeSpan.Zero, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public void self_update_target_rejects_shared_host_and_pid_path_mismatch()
    {
        var target = Path.Combine(_temporaryDirectory, "dotnet.exe");
        File.WriteAllText(target, "host");
        var sharedHost = new UpdateApplier(
            new FakeProcessController(), new FixedAccessProbe(true),
            new FakeRuntime(target, "S-1", suitable: false,
                suitabilityError: "shared dotnet host is unsupported"));
        sharedHost.ValidateCurrentTarget(target, 1234).Should().Match<UpdateTargetValidation>(
            result => !result.IsValid && result.Error!.Contains("dotnet"));

        var mismatched = new UpdateApplier(
            new FakeProcessController(), new FixedAccessProbe(true),
            new FakeRuntime(Path.Combine(_temporaryDirectory, "different.exe"), "S-1"));
        mismatched.ValidateCurrentTarget(target, 1234).Should().Match<UpdateTargetValidation>(
            result => !result.IsValid && result.Error!.Contains("does not match"));
    }

    [Fact]
    public async Task github_client_rejects_oversized_asset_and_untrusted_final_redirect()
    {
        const string oversizedJson = """
            {"tag_name":"v0.2.0","draft":false,"prerelease":false,"assets":[{
            "name":"PcUsageTracker-v0.2.0-win-x64.exe",
            "browser_download_url":"https://github.com/update.exe",
            "size":536870913,
            "digest":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]}
            """;
        using var oversizedHttp = CreateHttpClient(_ => JsonResponse(oversizedJson));
        var oversized = () => new GitHubReleaseClient(oversizedHttp)
            .GetLatestStableAsync(CancellationToken.None);
        await oversized.Should().ThrowAsync<UpdateException>().WithMessage("*size*");

        var bytes = Encoding.UTF8.GetBytes("payload");
        var actualDigest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        using var redirectedHttp = CreateHttpClient(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://downloads.example.invalid/update.exe"),
            Content = new ByteArrayContent(bytes),
        });
        var asset = new GitHubReleaseAsset(
            "PcUsageTracker-v0.2.0-win-x64.exe", new Uri("https://github.com/update.exe"),
            bytes.Length, actualDigest);
        var destination = Path.Combine(_temporaryDirectory, "redirect.download");
        var redirected = () => new GitHubReleaseClient(redirectedHttp)
            .DownloadVerifiedAsync(asset, destination, CancellationToken.None);
        await redirected.Should().ThrowAsync<UpdateException>().WithMessage("*redirected*");
        File.Exists(destination).Should().BeFalse();
    }

    (string Staging, string Source, string Target, string Acceptance, string Ready,
        string Sid, UpdateApplyRequest Request) CreateApplyFixture()
    {
        var unique = Path.Combine(_temporaryDirectory, Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(unique, "updates");
        var installed = Path.Combine(unique, "installed");
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(installed);
        var source = Path.Combine(staging, "PcUsageTracker-v0.2.0-win-x64.exe");
        var target = Path.Combine(installed, "PcUsageTracker.exe");
        var acceptance = Path.Combine(staging, "helper.accept");
        var ready = Path.Combine(staging, "helper.ready");
        const string sid = "S-1-5-21-test";
        File.WriteAllText(source, "new executable");
        File.WriteAllText(target, "old executable");
        var bytes = File.ReadAllBytes(source);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var request = new UpdateApplyRequest(
            1234, source, target, staging, acceptance, ready, sid,
            new AppVersion(0, 2, 0), bytes.Length, digest);
        return (staging, source, target, acceptance, ready, sid, request);
    }

    static GitHubRelease TestRelease(AppVersion version, byte[] bytes, string digest) => new(
        version,
        version.TagName,
        new Uri($"https://github.com/tdm1223/PcUsageTracker/releases/tag/{version.TagName}"),
        new GitHubReleaseAsset(
            version.AssetName,
            new Uri($"https://github.com/tdm1223/PcUsageTracker/releases/download/{version.TagName}/{version.AssetName}"),
            bytes.Length,
            digest));

    static string ReleaseJson(
        string tag,
        int size,
        string digest,
        bool draft = false,
        bool prerelease = false)
    {
        var version = AppVersion.TryParse(tag, out var parsed) ? parsed : new AppVersion(0, 2, 0);
        return $$"""
            {
              "tag_name":"{{tag}}",
              "draft":{{draft.ToString().ToLowerInvariant()}},
              "prerelease":{{prerelease.ToString().ToLowerInvariant()}},
              "html_url":"https://github.com/tdm1223/PcUsageTracker/releases/tag/{{tag}}",
              "assets":[{
                "name":"{{version.AssetName}}",
                "browser_download_url":"https://github.com/tdm1223/PcUsageTracker/releases/download/{{tag}}/{{version.AssetName}}",
                "size":{{size}},
                "digest":"sha256:{{digest}}"
              }]
            }
            """;
    }

    static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    static HttpClient CreateHttpClient(Func<HttpRequestMessage, HttpResponseMessage> response) =>
        new(new DelegateHandler(response)) { Timeout = TimeSpan.FromSeconds(5) };

    sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var result = response(request);
            result.RequestMessage ??= request;
            return Task.FromResult(result);
        }
    }

    sealed class FakeReleaseClient(GitHubRelease release, byte[] bytes) : IUpdateReleaseClient
    {
        public Task<GitHubRelease> GetLatestStableAsync(CancellationToken cancellationToken) =>
            Task.FromResult(release);

        public Task DownloadVerifiedAsync(
            GitHubReleaseAsset asset,
            string destinationPath,
            CancellationToken cancellationToken)
        {
            File.WriteAllBytes(destinationPath, bytes);
            return Task.CompletedTask;
        }
    }

    sealed class FakeProcessController : IUpdateProcessController
    {
        public bool Ready { get; init; }
        public UpdateAcceptanceResult Acceptance { get; init; } = new(true);
        public Exception? StartException { get; init; }
        public List<StartCall> Starts { get; } = [];
        public List<int> Stopped { get; } = [];
        public int WaitForExitCalls { get; private set; }

        public Task<bool> WaitForExitAsync(
            int processId, TimeSpan timeout, CancellationToken cancellationToken)
        {
            WaitForExitCalls++;
            return Task.FromResult(true);
        }

        public int Start(string executablePath, IReadOnlyList<string> arguments, bool elevated)
        {
            if (StartException is not null) throw StartException;
            Starts.Add(new StartCall(executablePath, arguments.ToArray(), elevated));
            return 9001;
        }

        public Task<UpdateAcceptanceResult> WaitForAcceptanceMarkerAsync(
            string markerPath,
            int expectedProcessId,
            TimeSpan timeout,
            CancellationToken cancellationToken) => Task.FromResult(Acceptance);

        public Task<bool> WaitForReadyMarkerAsync(
            string markerPath,
            int expectedProcessId,
            TimeSpan timeout,
            CancellationToken cancellationToken) => Task.FromResult(Ready);

        public Task StopAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Stopped.Add(processId);
            return Task.CompletedTask;
        }
    }

    sealed record StartCall(string Path, IReadOnlyList<string> Arguments, bool Elevated);

    sealed class FixedAccessProbe(bool canReplace) : IUpdateTargetAccessProbe
    {
        public bool CanReplace(string targetPath) => canReplace;
    }

    sealed class FakeRuntime(
        string processPath,
        string? sid,
        bool suitable = true,
        string? suitabilityError = null) : IUpdateRuntimeInfo
    {
        public string? CurrentUserSid => sid;
        public AppVersion CurrentApplicationVersion => new(0, 2, 0);
        public string? GetProcessImagePath(int processId) => processPath;
        public string GetFinalPath(string path) => Path.GetFullPath(path);

        public bool IsSuitableApplicationExecutable(string path, out string? error)
        {
            error = suitabilityError;
            return suitable;
        }
    }
}
