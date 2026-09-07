using System.Text.Json;
using Saehakgi.Core.Manifest;

namespace Saehakgi.Core.Migration.Modules;

/// <summary>
/// Migrates the user's environment variables (HKCU\Environment) — PATH, JAVA_HOME,
/// and any custom variables. Values keep their kind (REG_EXPAND_SZ for PATH), so
/// they round-trip exactly, and the change is broadcast live.
/// </summary>
public sealed class EnvironmentVariablesModule : IMigrationModule
{
    private const string SubKey = "Environment";

    public MigrationItemType Type => MigrationItemType.EnvironmentVariables;
    public string DisplayName => "환경변수 (사용자)";

    public IReadOnlyList<ManifestItem> Collect(MigrationRequest request, string payloadRoot)
    {
        var entries = RegistryValues.ReadAll(SubKey);
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
        if (request.ApplyLiveSettings) RegistryValues.Broadcast("Environment");
    }
}
