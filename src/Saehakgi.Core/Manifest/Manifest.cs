namespace Saehakgi.Core.Manifest;

/// <summary>Kinds of things saehakgi can migrate.</summary>
public enum MigrationItemType
{
    Folder,
    Bookmarks,
    MouseSettings,
    Certificate,
}

/// <summary>One migratable unit recorded in a bundle's manifest.</summary>
public sealed class ManifestItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public MigrationItemType Type { get; set; }
    public string DisplayName { get; set; } = "";

    /// <summary>Free-form per-module metadata (e.g. original path, browser name).</summary>
    public Dictionary<string, string> Meta { get; set; } = new();

    /// <summary>Relative path inside the bundle payload for file-based items.</summary>
    public string? PayloadPath { get; set; }

    /// <summary>Inline JSON blob for small settings items stored in the manifest itself.</summary>
    public string? InlineJson { get; set; }
}

/// <summary>The manifest embedded in every exported bundle.</summary>
public sealed class ExportManifest
{
    public string FormatVersion { get; set; } = "0.1";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string SourceMachine { get; set; } = Environment.MachineName;
    public string SourceUser { get; set; } = Environment.UserName;
    public List<ManifestItem> Items { get; set; } = new();
}
