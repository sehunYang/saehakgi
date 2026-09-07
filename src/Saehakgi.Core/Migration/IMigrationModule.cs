using Saehakgi.Core.Manifest;

namespace Saehakgi.Core.Migration;

/// <summary>Options carried through an export or import run.</summary>
public sealed class MigrationRequest
{
    /// <summary>Export: folders the user chose to migrate (recursively).</summary>
    public List<string> FolderPaths { get; set; } = new();

    /// <summary>Import: if a target already exists, back it up and overwrite (vs. skip).</summary>
    public bool OverwriteExisting { get; set; } = true;

    /// <summary>Import: actually apply live OS settings (mouse), not just registry writes.</summary>
    public bool ApplyLiveSettings { get; set; } = true;
}

/// <summary>
/// A migration module knows how to collect one kind of thing into a bundle
/// payload and apply it on the target. Reset is centralized in the engine and
/// driven by the <see cref="AppliedManifest"/>, so modules only record actions.
/// </summary>
public interface IMigrationModule
{
    MigrationItemType Type { get; }
    string DisplayName { get; }

    /// <summary>
    /// Collect data into <paramref name="payloadRoot"/> (the bundle staging dir) and
    /// return the manifest items describing it. Returns empty if nothing is present.
    /// </summary>
    IReadOnlyList<ManifestItem> Collect(MigrationRequest request, string payloadRoot);

    /// <summary>
    /// Apply one item on the target, appending reversible steps to <paramref name="applied"/>.
    /// </summary>
    void Apply(ManifestItem item, string payloadRoot, AppliedManifest applied, MigrationRequest request);
}
