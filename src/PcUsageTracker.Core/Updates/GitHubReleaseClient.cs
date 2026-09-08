using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace PcUsageTracker.Core.Updates;

public sealed record GitHubReleaseAsset(
    string Name,
    Uri DownloadUri,
    long Size,
    string Sha256Hex);

public sealed record GitHubRelease(
    AppVersion Version,
    string TagName,
    Uri? ReleasePage,
    GitHubReleaseAsset Asset);

public interface IUpdateReleaseClient
{
    Task<GitHubRelease> GetLatestStableAsync(CancellationToken cancellationToken);
    Task DownloadVerifiedAsync(
        GitHubReleaseAsset asset,
        string destinationPath,
        CancellationToken cancellationToken);
}

public sealed class UpdateException : Exception
{
    public UpdateException(string message) : base(message) { }
    public UpdateException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Reads the public GitHub latest-release endpoint and verifies release asset bytes.</summary>
public sealed class GitHubReleaseClient : IUpdateReleaseClient
{
    public const long MaximumAssetBytes = 512L * 1024 * 1024;
    const int MaximumMetadataBytes = 2 * 1024 * 1024;
    static readonly Uri LatestReleaseUri =
        new("https://api.github.com/repos/tdm1223/PcUsageTracker/releases/latest");

    readonly HttpClient _httpClient;

    public GitHubReleaseClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("PcUsageTracker-Updater/1.0");
        if (!_httpClient.DefaultRequestHeaders.Accept.Any())
            _httpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (!_httpClient.DefaultRequestHeaders.Contains("X-GitHub-Api-Version"))
            _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<GitHubRelease> GetLatestStableAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUri);
        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw CreateHttpError("check for updates", response);
        var metadataUri = response.RequestMessage?.RequestUri;
        if (metadataUri is null || metadataUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(metadataUri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase))
            throw new UpdateException("The release check was redirected outside the GitHub API.");

        byte[] payload;
        try
        {
            payload = await ReadLimitedAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                MaximumMetadataBytes,
                cancellationToken).ConfigureAwait(false);
        }
        catch (UpdateException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new UpdateException("The GitHub release response could not be read.", ex);
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetRequiredBoolean(root, "draft", out var draft) ||
                !TryGetRequiredBoolean(root, "prerelease", out var prerelease) ||
                draft || prerelease)
                throw new UpdateException("GitHub did not return a stable published release.");

            var tagName = RequiredString(root, "tag_name");
            if (!AppVersion.TryParse(tagName, out var version) || tagName != version.TagName)
                throw new UpdateException("The latest release tag is not a stable vMAJOR.MINOR.PATCH tag.");

            var expectedAssetName = version.AssetName;
            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                throw new UpdateException("The GitHub release has no asset list.");

            var matches = assets.EnumerateArray()
                .Where(asset => asset.ValueKind == JsonValueKind.Object &&
                                asset.TryGetProperty("name", out var name) &&
                                name.ValueKind == JsonValueKind.String &&
                                string.Equals(name.GetString(), expectedAssetName, StringComparison.Ordinal))
                .ToArray();
            if (matches.Length != 1)
                throw new UpdateException(
                    $"The release must contain exactly one '{expectedAssetName}' asset.");

            var selected = matches[0];
            var downloadText = RequiredString(selected, "browser_download_url");
            if (!Uri.TryCreate(downloadText, UriKind.Absolute, out var downloadUri) ||
                downloadUri.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(downloadUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("The release asset download URL is not a trusted GitHub HTTPS URL.");

            if (!selected.TryGetProperty("size", out var sizeElement) ||
                !sizeElement.TryGetInt64(out var size) || size <= 0 || size > MaximumAssetBytes)
                throw new UpdateException("The release asset size is missing or outside the allowed limit.");

            var digest = RequiredString(selected, "digest");
            if (!TryParseSha256Digest(digest, out var sha256))
                throw new UpdateException("The release asset does not provide a valid SHA-256 digest.");

            Uri? releasePage = null;
            if (root.TryGetProperty("html_url", out var pageElement) &&
                pageElement.ValueKind == JsonValueKind.String &&
                Uri.TryCreate(pageElement.GetString(), UriKind.Absolute, out var parsedPage) &&
                parsedPage.Scheme == Uri.UriSchemeHttps &&
                string.Equals(parsedPage.Host, "github.com", StringComparison.OrdinalIgnoreCase))
                releasePage = parsedPage;

            return new GitHubRelease(
                version,
                tagName,
                releasePage,
                new GitHubReleaseAsset(expectedAssetName, downloadUri, size, sha256));
        }
        catch (UpdateException) { throw; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new UpdateException("GitHub returned malformed release metadata.", ex);
        }
    }

    public async Task DownloadVerifiedAsync(
        GitHubReleaseAsset asset,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ValidateAsset(asset);

        var fullDestination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
        if (File.Exists(fullDestination)) File.Delete(fullDestination);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUri);
            using var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw CreateHttpError("download the update", response);
            var finalUri = response.RequestMessage?.RequestUri;
            if (finalUri is null || finalUri.Scheme != Uri.UriSchemeHttps || !IsTrustedDownloadHost(finalUri.Host))
                throw new UpdateException("The update download was redirected outside trusted GitHub HTTPS hosts.");
            if (response.Content.Headers.ContentLength is { } contentLength && contentLength != asset.Size)
                throw new UpdateException(
                    $"The update size did not match GitHub metadata (expected {asset.Size}, got {contentLength}).");

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(
                fullDestination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            long written = 0;
            try
            {
                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0) break;
                    written += read;
                    if (written > asset.Size || written > MaximumAssetBytes)
                        throw new UpdateException("The update download exceeded its declared size.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);

            if (written != asset.Size)
                throw new UpdateException(
                    $"The update download was incomplete (expected {asset.Size}, got {written}).");
            var expectedHash = Convert.FromHexString(asset.Sha256Hex);
            var actualHash = hash.GetHashAndReset();
            if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
                throw new UpdateException("The update SHA-256 digest did not match GitHub metadata.");
        }
        catch
        {
            TryDelete(fullDestination);
            throw;
        }
    }

    static void ValidateAsset(GitHubReleaseAsset asset)
    {
        if (asset.DownloadUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(asset.DownloadUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            throw new UpdateException("The update asset URL is not a trusted GitHub HTTPS URL.");
        if (asset.Size <= 0 || asset.Size > MaximumAssetBytes)
            throw new UpdateException("The update asset size is outside the allowed limit.");
        if (!TryParseSha256Digest("sha256:" + asset.Sha256Hex, out _))
            throw new UpdateException("The expected update digest is invalid.");
    }

    static bool IsTrustedDownloadHost(string host) =>
        string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);

    static bool TryParseSha256Digest(string? digest, out string hex)
    {
        hex = string.Empty;
        const string prefix = "sha256:";
        if (digest is null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var candidate = digest[prefix.Length..];
        if (candidate.Length != 64 || candidate.Any(c => !Uri.IsHexDigit(c))) return false;
        hex = candidate.ToLowerInvariant();
        return true;
    }

    static string RequiredString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new UpdateException($"The GitHub release is missing '{property}'.");
        return value.GetString()!;
    }

    static bool TryGetRequiredBoolean(JsonElement element, string property, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(property, out var item) ||
            item.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = item.GetBoolean();
        return true;
    }

    static UpdateException CreateHttpError(string action, HttpResponseMessage response)
    {
        var status = $"{(int)response.StatusCode} {response.ReasonPhrase}".Trim();
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            var resetText = response.Headers.TryGetValues("X-RateLimit-Reset", out var values)
                ? values.FirstOrDefault()
                : null;
            if (long.TryParse(resetText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var resetUnix))
            {
                try
                {
                    return new UpdateException(
                        $"GitHub rate-limited the update request ({status}). Try again after " +
                        $"{DateTimeOffset.FromUnixTimeSeconds(resetUnix):u}.");
                }
                catch (ArgumentOutOfRangeException) { }
            }
            return new UpdateException($"GitHub rate-limited the update request ({status}). Try again later.");
        }
        return new UpdateException($"GitHub could not {action} ({status}).");
    }

    static async Task<byte[]> ReadLimitedAsync(Stream input, int maximumBytes, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0) break;
                if (output.Length + read > maximumBytes)
                    throw new UpdateException("The GitHub release response was unexpectedly large.");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { }
    }
}
