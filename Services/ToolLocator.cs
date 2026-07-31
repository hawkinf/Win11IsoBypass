namespace Win11IsoBypass.Services;

public static class ToolLocator
{
    private const string EmbeddedResourceName = "Win11IsoBypass.oscdimg.exe";

    public static string? FindOscdimg()
    {
        return ExtractEmbeddedOscdimg();
    }

    private static string? ExtractEmbeddedOscdimg()
    {
        try
        {
            using var resource = typeof(ToolLocator).Assembly.GetManifestResourceStream(EmbeddedResourceName);
            if (resource is null) return null;

            var directory = Path.Combine(Path.GetTempPath(), "WindowsIsoCustomizer", "tools");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "oscdimg-embedded.exe");

            if (!File.Exists(path) || new FileInfo(path).Length != resource.Length)
            {
                resource.Position = 0;
                using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                resource.CopyTo(file);
            }

            File.SetAttributes(path, FileAttributes.Normal);
            return path;
        }
        catch
        {
            return null;
        }
    }
}
