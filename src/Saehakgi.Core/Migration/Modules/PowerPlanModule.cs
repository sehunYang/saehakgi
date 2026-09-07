using System.Text.RegularExpressions;
using Saehakgi.Core.Manifest;
using Saehakgi.Core.Util;

namespace Saehakgi.Core.Migration.Modules;

/// <summary>
/// Migrates the active power scheme via <c>powercfg /export</c> → <c>/import</c>
/// (covers custom OEM plans, not just the built-ins). On import a fresh scheme
/// GUID is created and made active; reset restores the previously active scheme
/// and deletes the one we imported.
/// </summary>
public sealed partial class PowerPlanModule : IMigrationModule
{
    public MigrationItemType Type => MigrationItemType.PowerPlan;
    public string DisplayName => "전원 계획";

    public IReadOnlyList<ManifestItem> Collect(MigrationRequest request, string payloadRoot)
    {
        var active = ProcessRunner.Run("powercfg", "/getactivescheme", 20_000);
        if (!active.Ok) return Array.Empty<ManifestItem>();

        var guid = FirstGuid(active.StdOut);
        if (guid is null) return Array.Empty<ManifestItem>();

        var dir = Path.Combine(payloadRoot, "power");
        Directory.CreateDirectory(dir);
        var pow = Path.Combine(dir, "active.pow");

        var export = ProcessRunner.Run("powercfg", $"/export \"{pow}\" {guid}", 30_000);
        if (!export.Ok || !File.Exists(pow)) return Array.Empty<ManifestItem>();

        return new[]
        {
            new ManifestItem
            {
                Type = Type,
                DisplayName = $"{DisplayName} (활성 구성표)",
                PayloadPath = "power",
                Meta = { ["sourceGuid"] = guid },
            },
        };
    }

    public void Apply(ManifestItem item, string payloadRoot, AppliedManifest applied, MigrationRequest request)
    {
        if (item.PayloadPath is null) return;
        var pow = Path.Combine(payloadRoot, item.PayloadPath, "active.pow");
        if (!File.Exists(pow)) return;

        var prevActive = FirstGuid(ProcessRunner.Run("powercfg", "/getactivescheme", 20_000).StdOut);

        var import = ProcessRunner.Run("powercfg", $"/import \"{pow}\"", 30_000);
        if (!import.Ok) return;
        var newGuid = FirstGuid(import.StdOut);
        if (newGuid is null) return;

        ProcessRunner.Run("powercfg", $"/setactive {newGuid}", 20_000);

        // Reset runs actions newest-first, so append delete FIRST and restore LAST:
        // restore-previous will run before delete (you can't delete the active scheme).
        applied.Actions.Add(new AppliedAction
        {
            Kind = AppliedActionKind.RunProcessOnReset,
            Target = $"전원 구성표 삭제: {newGuid}",
            ResetExe = "powercfg",
            ResetArgs = $"/delete {newGuid}",
        });
        if (prevActive is not null)
        {
            applied.Actions.Add(new AppliedAction
            {
                Kind = AppliedActionKind.RunProcessOnReset,
                Target = $"이전 전원 구성표 활성화: {prevActive}",
                ResetExe = "powercfg",
                ResetArgs = $"/setactive {prevActive}",
            });
        }
    }

    private static string? FirstGuid(string text)
    {
        var m = GuidRegex().Match(text);
        return m.Success ? m.Value : null;
    }

    [GeneratedRegex("[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}")]
    private static partial Regex GuidRegex();
}
