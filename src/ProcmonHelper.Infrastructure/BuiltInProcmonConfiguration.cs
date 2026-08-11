namespace ProcmonHelper.Infrastructure;

public static class BuiltInProcmonConfiguration
{
    private const string ResourceName = "ProcmonHelper.Infrastructure.Assets.ExcludeProcmon.pmc";
    private const string FileName = "ExcludeProcmon.pmc";

    public static string WriteToDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        using var resource = typeof(BuiltInProcmonConfiguration).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The built-in Process Monitor configuration is missing.");
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        resource.CopyTo(output);
        return path;
    }
}
