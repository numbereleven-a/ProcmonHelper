using System.Globalization;
using System.Text.RegularExpressions;

namespace ProcmonHelper.Core;

public sealed record FileNameContext(string AppName, string ProfileName, Guid SessionId, int? Pid, DateTimeOffset Timestamp, int? Segment = null, string? Format = null);

public static partial class FileNameTemplate
{
    private static readonly HashSet<string> SupportedTokens = new(StringComparer.OrdinalIgnoreCase)
    { "AppName", "ComputerName", "UserName", "Date", "Time", "DateTime", "SessionId", "ProfileName", "Pid", "Format", "Segment" };
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    public static string Expand(string template, FileNameContext context)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AppName"] = context.AppName,
            ["ComputerName"] = Environment.MachineName,
            ["UserName"] = Environment.UserName,
            ["Date"] = context.Timestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["Time"] = context.Timestamp.ToString("HH-mm-ss", CultureInfo.InvariantCulture),
            ["DateTime"] = context.Timestamp.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture),
            ["SessionId"] = context.SessionId.ToString("N"),
            ["ProfileName"] = context.ProfileName,
            ["Pid"] = context.Pid?.ToString(CultureInfo.InvariantCulture) ?? "none",
            ["Format"] = context.Format ?? string.Empty,
            ["Segment"] = context.Segment?.ToString("000", CultureInfo.InvariantCulture) ?? string.Empty
        };
        var unknown = FindUnknownTokens(template);
        if (unknown.Count > 0)
            throw new ArgumentException($"Unknown file-name template token: {{{unknown[0]}}}.", nameof(template));
        var expanded = TokenRegex().Replace(template, match => values[match.Groups[1].Value]);
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string(expanded.Select(ch => invalid.Contains(ch) || ch is '/' or '\\' ? '_' : ch).ToArray());
        while (safe.Contains("..", StringComparison.Ordinal)) safe = safe.Replace("..", "_", StringComparison.Ordinal);
        safe = MultiUnderscoreRegex().Replace(safe, "_");
        if (safe.Length > 180) safe = safe[..180];
        safe = safe.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(safe)) safe = "capture";
        var deviceName = safe.Split('.')[0].TrimEnd(' ');
        if (Reserved.Contains(deviceName)) safe = "_" + safe;
        return safe.Length <= 180 ? safe : safe[..180].Trim().TrimEnd('.');
    }

    public static IReadOnlyList<string> FindUnknownTokens(string template)
    {
        var result = TokenRegex().Matches(template).Select(x => x.Groups[1].Value)
            .Where(x => !SupportedTokens.Contains(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (template.Count(x => x == '{') != template.Count(x => x == '}') || BraceRegex().Replace(template, string.Empty).Contains('{') || BraceRegex().Replace(template, string.Empty).Contains('}'))
            result.Add("invalid braces");
        return result;
    }

    public static string GetUniquePath(string directory, string name, string extension)
    {
        var candidate = Path.Combine(directory, name + extension);
        for (var index = 1; File.Exists(candidate); index++)
            candidate = Path.Combine(directory, $"{name}_{index:000}{extension}");
        return candidate;
    }

    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex TokenRegex();
    [GeneratedRegex(@"\{[^{}]+\}")]
    private static partial Regex BraceRegex();
    [GeneratedRegex("_+")]
    private static partial Regex MultiUnderscoreRegex();
}
