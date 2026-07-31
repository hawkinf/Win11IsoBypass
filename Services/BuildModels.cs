namespace Win11IsoBypass.Services;

public sealed record BypassOptions(bool Tpm, bool SecureBoot, bool Ram, bool Cpu, bool Storage)
{
    public IReadOnlyList<string> RegistryValues
    {
        get
        {
            var values = new List<string>();
            if (Tpm) values.Add("BypassTPMCheck");
            if (SecureBoot) values.Add("BypassSecureBootCheck");
            if (Ram) values.Add("BypassRAMCheck");
            if (Cpu) values.Add("BypassCPUCheck");
            if (Storage) values.Add("BypassStorageCheck");
            return values;
        }
    }
}

public enum UnattendPlacement
{
    Root,
    Sources,
    Panther,
    RootAndPanther,
    All
}

public sealed record BuildProgress(int Percent, string Status, string? Detail = null);

public sealed class InstallImageInfo
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;
    public required string Format { get; init; }
    public bool IsSelected { get; set; } = true;

    public string DisplayName => $"Índice {Index} — {Name}";
    public string DisplayDetails => string.IsNullOrWhiteSpace(Description) ? Format : $"{Format} · {Description}";
}
