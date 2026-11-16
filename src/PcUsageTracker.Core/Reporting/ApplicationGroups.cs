using System.Text.Json;
using Microsoft.Data.Sqlite;
using PcUsageTracker.Core.Sampling;
using PcUsageTracker.Core.Storage;

namespace PcUsageTracker.Core.Reporting;

public sealed record ApplicationGroup(string Name, IReadOnlyList<string> Processes);
public sealed record GroupedUsage(string Name, bool IsGroup, string? Category, long Seconds, IReadOnlyList<string> Processes);

/// <summary>Groups are presentation settings; sessions and process identities stay unchanged.</summary>
public static class ApplicationGroups
{
    public const string SettingsKey = "application_groups_v1";

    public static IReadOnlyList<ApplicationGroup> Load(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", SettingsKey);
        var value = command.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<ApplicationGroup>();
        try
        {
            return Validate(JsonSerializer.Deserialize<List<ApplicationGroup>>(value)
                ?? throw new InvalidDataException("Invalid application group settings."));
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Invalid application group settings.", exception);
        }
    }

    public static void Save(SqliteStore store, IEnumerable<ApplicationGroup> groups)
    {
        var valid = Validate(groups);
        store.SetSetting(SettingsKey, JsonSerializer.Serialize(valid));
    }

    static ApplicationGroup[] Validate(IEnumerable<ApplicationGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ApplicationGroup>();
        foreach (var group in groups)
        {
            if (group is null || string.IsNullOrWhiteSpace(group.Name) || group.Name.Trim().Length > 100 ||
                group.Processes is null) throw new ArgumentException("A group needs a name and processes.");
            var name = group.Name.Trim();
            if (!names.Add(name)) throw new ArgumentException($"Group name '{name}' is already in use.");
            var processes = group.Processes.Select(value => value?.Trim() ?? string.Empty).ToArray();
            if (processes.Length == 0 || processes.Any(value => value.Length == 0 || value.Length > 260 ||
                    string.Equals(value, IdleSentinel.Name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("A group needs at least one valid application.");
            foreach (var process in processes)
                if (!used.Add(process)) throw new ArgumentException($"Application '{process}' is already assigned to a group.");
            result.Add(new ApplicationGroup(name, processes));
            if (result.Count > 1000 || used.Count > 10000) throw new ArgumentException("Too many groups or applications.");
        }
        return result.ToArray();
    }

    public static IReadOnlyList<GroupedUsage> Summarize(IReadOnlyList<ReportEntry> entries, IReadOnlyList<ApplicationGroup> groups)
    {
        var membership = groups.SelectMany(group => group.Processes.Select(process => (process, group)))
            .ToDictionary(item => item.process, item => item.group, StringComparer.OrdinalIgnoreCase);
        var grouped = new Dictionary<string, List<ReportEntry>>(StringComparer.OrdinalIgnoreCase);
        var result = new List<GroupedUsage>();
        foreach (var entry in entries)
        {
            if (!membership.TryGetValue(entry.ProcessName, out var group))
            {
                result.Add(new GroupedUsage(entry.DisplayName, false, entry.CategoryName, entry.TotalSeconds, [entry.ProcessName]));
                continue;
            }
            if (!grouped.TryGetValue(group.Name, out var members)) grouped.Add(group.Name, members = []);
            members.Add(entry);
        }
        foreach (var (name, members) in grouped)
        {
            var categories = members.Select(member => member.CategoryName ?? "Other").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            result.Add(new GroupedUsage(name, true, categories.Length == 1 ? categories[0] : null,
                members.Sum(member => (long)member.TotalSeconds), members.Select(member => member.ProcessName).ToArray()));
        }
        return result.OrderByDescending(item => item.Seconds).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
}
