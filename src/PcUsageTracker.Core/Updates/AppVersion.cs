using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace PcUsageTracker.Core.Updates;

/// <summary>A stable three-component application version used by release tags and asset names.</summary>
public readonly record struct AppVersion(int Major, int Minor, int Patch) : IComparable<AppVersion>
{
    static readonly Regex StablePattern = new(
        @"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static AppVersion Current(Assembly? assembly = null)
    {
        var version = (assembly ?? Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly())
            .GetName().Version;
        return version is null
            ? default
            : new AppVersion(
                Math.Max(0, version.Major),
                Math.Max(0, version.Minor),
                Math.Max(0, version.Build));
    }

    public static bool TryParse(string? value, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var match = StablePattern.Match(value.Trim());
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
            return false;

        version = new AppVersion(major, minor, patch);
        return true;
    }

    public static AppVersion Parse(string value) =>
        TryParse(value, out var version)
            ? version
            : throw new FormatException($"'{value}' is not a stable vMAJOR.MINOR.PATCH version.");

    public static bool TryParseAssetName(string? fileName, out AppVersion version)
    {
        version = default;
        const string prefix = "PcUsageTracker-v";
        const string suffix = "-win-x64.exe";
        if (fileName is null ||
            !fileName.StartsWith(prefix, StringComparison.Ordinal) ||
            !fileName.EndsWith(suffix, StringComparison.Ordinal)) return false;
        var versionText = fileName[prefix.Length..^suffix.Length];
        return TryParse(versionText, out version) &&
               string.Equals(fileName, version.AssetName, StringComparison.Ordinal);
    }

    public int CompareTo(AppVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0) return major;
        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
    public string TagName => $"v{this}";
    public string AssetName => $"PcUsageTracker-{TagName}-win-x64.exe";

    public static bool operator <(AppVersion left, AppVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(AppVersion left, AppVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(AppVersion left, AppVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(AppVersion left, AppVersion right) => left.CompareTo(right) >= 0;
}
