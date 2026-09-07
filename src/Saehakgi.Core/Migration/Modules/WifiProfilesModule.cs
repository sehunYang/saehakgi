using System.Xml.Linq;
using Saehakgi.Core.Manifest;
using Saehakgi.Core.Util;

namespace Saehakgi.Core.Migration.Modules;

/// <summary>
/// Migrates saved Wi-Fi profiles via <c>netsh wlan export/add</c>. Exported XML
/// includes the network key in clear text, so it only ever lives inside the
/// AES-256-GCM bundle. On import, profiles that already exist on the target are
/// left untouched; only newly added profiles are recorded, so reset deletes just
/// those and never removes a network the machine already knew.
/// </summary>
public sealed class WifiProfilesModule : IMigrationModule
{
    public MigrationItemType Type => MigrationItemType.WifiProfiles;
    public string DisplayName => "Wi-Fi 프로필";

    public IReadOnlyList<ManifestItem> Collect(MigrationRequest request, string payloadRoot)
    {
        var dir = Path.Combine(payloadRoot, "wifi");
        Directory.CreateDirectory(dir);

        var res = ProcessRunner.Run("netsh", $"wlan export profile key=clear folder=\"{dir}\"", 60_000);
        var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.xml") : Array.Empty<string>();
        if (!res.Started || files.Length == 0) return Array.Empty<ManifestItem>();

        return new[]
        {
            new ManifestItem
            {
                Type = Type,
                DisplayName = $"{DisplayName} — {files.Length}개",
                PayloadPath = "wifi",
            },
        };
    }

    public void Apply(ManifestItem item, string payloadRoot, AppliedManifest applied, MigrationRequest request)
    {
        if (item.PayloadPath is null) return;
        var source = Path.Combine(payloadRoot, item.PayloadPath);
        if (!Directory.Exists(source)) return;

        var existing = ExistingProfileNames();

        foreach (var xml in Directory.GetFiles(source, "*.xml"))
        {
            var name = ReadProfileName(xml);
            if (name is null || existing.Contains(name)) continue; // leave pre-existing networks alone

            var add = ProcessRunner.Run("netsh", $"wlan add profile filename=\"{xml}\" user=all", 30_000);
            if (!add.Ok) continue;

            applied.Actions.Add(new AppliedAction
            {
                Kind = AppliedActionKind.RunProcessOnReset,
                Target = $"Wi-Fi: {name}",
                ResetExe = "netsh",
                ResetArgs = $"wlan delete profile name=\"{name}\"",
            });
        }
    }

    private static HashSet<string> ExistingProfileNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var res = ProcessRunner.Run("netsh", "wlan show profiles", 30_000);
        if (!res.Started) return names;

        // Lines look like "    All User Profile     : <name>" (localized label, stable format).
        foreach (var line in res.StdOut.Split('\n'))
        {
            var idx = line.IndexOf(':');
            if (idx < 0) continue;
            var candidate = line[(idx + 1)..].Trim();
            if (candidate.Length > 0) names.Add(candidate);
        }
        return names;
    }

    private static string? ReadProfileName(string xmlPath)
    {
        try
        {
            var doc = XDocument.Load(xmlPath);
            // First <name> in document order is WLANProfile/name (the profile name).
            return doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "name")?.Value?.Trim();
        }
        catch
        {
            return null;
        }
    }
}
