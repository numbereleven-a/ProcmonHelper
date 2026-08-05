using ProcmonHelper.Contracts;

namespace ProcmonHelper.Core;

public sealed class ProfileValidator
{
    public IReadOnlyList<ValidationIssue> Validate(CaptureProfile profile)
    {
        var issues = new List<ValidationIssue>();
        var stop = profile.Stop;
        var processes = profile.Processes;
        if (stop is null)
        {
            issues.Add(new(nameof(profile.Stop), "Stop settings are missing from the profile."));
            return issues;
        }
        if (processes is null)
        {
            issues.Add(new(nameof(profile.Processes), "Process settings are missing from the profile."));
            return issues;
        }
        if (!File.Exists(profile.ProcmonPath))
            issues.Add(new(nameof(profile.ProcmonPath), "Process Monitor executable does not exist."));
        else if (!string.Equals(Path.GetFileName(profile.ProcmonPath), "Procmon64.exe", StringComparison.OrdinalIgnoreCase))
            issues.Add(new(nameof(profile.ProcmonPath), "Select the x64 executable named Procmon64.exe."));

        if (!File.Exists(profile.TargetPath))
            issues.Add(new(nameof(profile.TargetPath), "Target executable does not exist."));
        else if (!string.Equals(Path.GetExtension(profile.TargetPath), ".exe", StringComparison.OrdinalIgnoreCase) || !IsPortableExecutable(profile.TargetPath))
            issues.Add(new(nameof(profile.TargetPath), "Target must be a Windows PE executable (.exe), not a script, shortcut, or document."));
        if (!string.IsNullOrWhiteSpace(profile.WorkingDirectory) && !Directory.Exists(profile.WorkingDirectory))
            issues.Add(new(nameof(profile.WorkingDirectory), "Working directory does not exist."));
        if (profile.FilterMode == FilterMode.PmcConfiguration && !File.Exists(profile.PmcPath))
            issues.Add(new(nameof(profile.PmcPath), "The selected PMC configuration does not exist."));
        if (stop.MaximumDuration is { } duration && duration <= TimeSpan.Zero)
            issues.Add(new("Stop.MaximumDuration", "Maximum duration must be greater than zero."));
        if (stop.TargetExitDelay < TimeSpan.Zero)
            issues.Add(new("Stop.TargetExitDelay", "Target-exit delay cannot be negative."));
        if (stop.MaximumPmlBytes is { } maximumBytes && maximumBytes < 1024 * 1024)
            issues.Add(new("Stop.MaximumPmlBytes", "PML size limit must be at least 1 MB."));
        if (stop.MinimumFreeBytes < 64L * 1024 * 1024)
            issues.Add(new("Stop.MinimumFreeBytes", "Free-space reserve must be at least 64 MB."));
        if (!stop.StopAfterTargetExit && stop.MaximumDuration is null && stop.MaximumPmlBytes is null)
            issues.Add(new("Stop", "Only manual stop is enabled; the session has no automatic safety limit.", true));
        if (string.IsNullOrWhiteSpace(profile.LocalDirectory))
            issues.Add(new(nameof(profile.LocalDirectory), "Select a local capture directory."));
        if (string.IsNullOrWhiteSpace(profile.FileNameTemplate))
            issues.Add(new(nameof(profile.FileNameTemplate), "File name template cannot be empty."));

        var duplicate = processes.Where(x => x is not null && x.Enabled)
            .GroupBy(x => NormalizeProcessName(x.Name), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(x => x.Key.Length > 0 && x.Count() > 1);
        if (duplicate is not null)
            issues.Add(new(nameof(profile.Processes), $"Duplicate process name: {duplicate.Key}."));
        foreach (var process in processes.Where(x => x is not null && x.Enabled))
            if (!IsValidProcessName(process.Name))
                issues.Add(new(nameof(profile.Processes), $"Invalid executable name: {process.Name}."));
        return issues;
    }

    public static string NormalizeProcessName(string name)
    {
        var value = Path.GetFileName(name.Trim());
        return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value : value + ".exe";
    }

    public static bool IsValidProcessName(string name)
    {
        var original = name.Trim();
        if (!string.Equals(original, Path.GetFileName(original), StringComparison.Ordinal)) return false;
        var normalized = NormalizeProcessName(name);
        return normalized.Length > 4 && normalized.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
               string.Equals(normalized, Path.GetFileName(normalized), StringComparison.Ordinal);
    }

    public static bool IsPortableExecutable(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D) return false;
            stream.Position = 0x3C;
            var peOffset = reader.ReadInt32();
            if (peOffset < 64 || peOffset > stream.Length - 4) return false;
            stream.Position = peOffset;
            return reader.ReadUInt32() == 0x00004550;
        }
        catch { return false; }
    }
}
