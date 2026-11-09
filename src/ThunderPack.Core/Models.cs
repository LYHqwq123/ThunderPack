namespace ThunderPack.Core;

public sealed class ModPackage
{
    public string Namespace { get; set; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Description { get; init; } = "";
    public string WebsiteUrl { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public string ArchiveRoot { get; init; } = "";
    public string[] Dependencies { get; init; } = [];
    public long FileSize { get; init; }
    public byte[]? IconBytes { get; init; }
    public bool Enabled { get; set; } = true;
    public bool HasNamespace => !string.IsNullOrWhiteSpace(Namespace);
    public string Id => HasNamespace ? $"{Namespace}-{Name}" : Name;
    public string DependencyString => $"{Id}-{Version}";
    public bool IsBepInExLoader => Name.Equals("BepInExPack", StringComparison.OrdinalIgnoreCase)
        || Name.StartsWith("BepInExPack_", StringComparison.OrdinalIgnoreCase)
        || Name.Equals("BepInEx", StringComparison.OrdinalIgnoreCase);
    public bool IsLoader => IsBepInExLoader
        || Name.Equals("MelonLoader", StringComparison.OrdinalIgnoreCase)
        || Name.StartsWith("MelonLoader_", StringComparison.OrdinalIgnoreCase)
        || Name.Equals("ReturnOfModding", StringComparison.OrdinalIgnoreCase)
        || Name.Equals("GDWeave", StringComparison.OrdinalIgnoreCase);
}

public sealed class DependencyIssue
{
    public string Dependency { get; init; } = "";
    public string RequiredBy { get; init; } = "";
    public string Message { get; init; } = "";
    public string Kind { get; init; } = "missing";
    public bool CanDownload { get; init; }
    public string? DownloadUrl { get; init; }
}

public sealed class PackOptions
{
    public string Name { get; init; } = "My_Modpack";
    public string Version { get; init; } = "1.0.0";
    public string Description { get; init; } = "我的 Thunderstore Mod 整合包";
    public string WebsiteUrl { get; init; } = "";
    public string Game { get; init; } = "generic";
    public string InstallMode { get; init; } = "archive";
    public byte[]? IconBytes { get; init; }
}

public sealed class ImportResult
{
    public List<ModPackage> Packages { get; init; } = [];
    public List<string> Errors { get; init; } = [];
    public string? Game { get; set; }
    public string? InstallMode { get; set; }
}

public sealed record DependencyReference(string Namespace, string Name, string Version)
{
    public string Id => $"{Namespace}-{Name}";
    public string FullName => $"{Id}-{Version}";
    public string DownloadUrl => $"https://thunderstore.io/package/download/{Uri.EscapeDataString(Namespace)}/{Uri.EscapeDataString(Name)}/{Version}/";
}
