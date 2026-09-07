using System.Text.Json;
using Saehakgi.Core.Manifest;

namespace Saehakgi.Core.Migration.Modules;

/// <summary>
/// Migrates a curated set of popular HKCU personalization toggles: show file
/// extensions, light/dark app+system theme, and the Windows 11 taskbar alignment.
/// All are small registry values, so they migrate and reset cleanly.
/// </summary>
public sealed class PersonalizationModule : IMigrationModule
{
    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static readonly (string SubKey, string Name)[] Targets =
    {
        (Advanced, "HideFileExt"),
        (Advanced, "ShowTaskViewButton"),
        (Advanced, "TaskbarAl"),        // Windows 11: 0 = left, 1 = center
        (Personalize, "AppsUseLightTheme"),
        (Personalize, "SystemUsesLightTheme"),
    };

    public MigrationItemType Type => MigrationItemType.Personalization;
    public string DisplayName => "개인화 (탐색기·테마·작업표시줄)";

    public IReadOnlyList<ManifestItem> Collect(MigrationRequest request, string payloadRoot)
    {
        var entries = new List<RegEntry>();
        foreach (var group in Targets.GroupBy(t => t.SubKey))
        {
            entries.AddRange(RegistryValues.Read(group.Key, group.Select(t => t.Name)));
        }
        if (entries.Count == 0) return Array.Empty<ManifestItem>();

        return new[]
        {
            new ManifestItem
            {
                Type = Type,
                DisplayName = $"{DisplayName} — {entries.Count}개",
                InlineJson = JsonSerializer.Serialize(entries),
            },
        };
    }

    public void Apply(ManifestItem item, string payloadRoot, AppliedManifest applied, MigrationRequest request)
    {
        if (item.InlineJson is null) return;
        var entries = JsonSerializer.Deserialize<List<RegEntry>>(item.InlineJson);
        if (entries is null || entries.Count == 0) return;

        RegistryValues.Apply(entries, applied);
        if (request.ApplyLiveSettings) RegistryValues.Broadcast("ImmersiveColorSet");
    }
}
