using Microsoft.Win32;
using Saehakgi.Core.Bundle;
using Saehakgi.Core.Manifest;
using Saehakgi.Core.Migration.Modules;
using Saehakgi.Core.Util;

namespace Saehakgi.Core.Migration;

/// <summary>
/// Orchestrates export → import → reset across the registered modules.
/// Reset is centralized here and driven purely by the AppliedManifest, so the
/// tool only ever undoes what it did — never the machine's baseline.
/// </summary>
public sealed class MigrationEngine
{
    private readonly List<IMigrationModule> _modules;

    public MigrationEngine(IEnumerable<IMigrationModule>? modules = null)
    {
        _modules = (modules ?? DefaultModules()).ToList();
    }

    public static IEnumerable<IMigrationModule> DefaultModules() => new IMigrationModule[]
    {
        new FolderModule(),
        new BookmarksModule(),
        new MouseSettingsModule(),
        new CertificateModule(),
    };

    public IReadOnlyList<IMigrationModule> Modules => _modules;

    private IMigrationModule Module(MigrationItemType type) =>
        _modules.FirstOrDefault(m => m.Type == type)
        ?? throw new InvalidOperationException($"No module registered for {type}.");

    /// <summary>Collect the selected types into an encrypted bundle at <paramref name="bundlePath"/>.</summary>
    public ExportManifest Export(
        IEnumerable<MigrationItemType> types,
        MigrationRequest request,
        string bundlePath,
        string passphrase,
        Action<string>? log = null)
    {
        var staging = AppPaths.NewStagingDir();
        try
        {
            var manifest = new ExportManifest();
            foreach (var type in types.Distinct())
            {
                var module = Module(type);
                var items = module.Collect(request, staging);
                manifest.Items.AddRange(items);
                log?.Invoke($"수집: {module.DisplayName} — {items.Count}개 항목");
            }

            File.WriteAllText(Path.Combine(staging, "manifest.json"), Json.Serialize(manifest));
            BundleFile.Pack(staging, bundlePath, passphrase);
            log?.Invoke($"번들 저장 완료: {bundlePath}");
            return manifest;
        }
        finally
        {
            TryDeleteDir(staging);
        }
    }

    /// <summary>Decrypt a bundle and read its manifest without applying anything.</summary>
    public ExportManifest Inspect(string bundlePath, string passphrase)
    {
        var staging = AppPaths.NewStagingDir();
        try
        {
            BundleFile.Unpack(bundlePath, staging, passphrase);
            return Json.Deserialize<ExportManifest>(File.ReadAllText(Path.Combine(staging, "manifest.json")));
        }
        finally
        {
            TryDeleteDir(staging);
        }
    }

    /// <summary>Apply the selected item types from a bundle and persist a reset record.</summary>
    public AppliedManifest Import(
        string bundlePath,
        string passphrase,
        IEnumerable<MigrationItemType> selectedTypes,
        MigrationRequest request,
        Action<string>? log = null)
    {
        var staging = AppPaths.NewStagingDir();
        try
        {
            BundleFile.Unpack(bundlePath, staging, passphrase);
            var manifest = Json.Deserialize<ExportManifest>(File.ReadAllText(Path.Combine(staging, "manifest.json")));

            var applied = new AppliedManifest { BundlePath = bundlePath };
            var selected = new HashSet<MigrationItemType>(selectedTypes);

            foreach (var item in manifest.Items.Where(i => selected.Contains(i.Type)))
            {
                Module(item.Type).Apply(item, staging, applied, request);
                log?.Invoke($"적용: {item.DisplayName}");
            }

            SaveApplied(applied);
            log?.Invoke($"적용 완료 — {applied.Actions.Count}개 변경 기록됨 (초기화 가능)");
            return applied;
        }
        finally
        {
            TryDeleteDir(staging);
        }
    }

    /// <summary>Reverse every recorded action, newest first, restoring the pre-import state.</summary>
    public void Reset(AppliedManifest applied, Action<string>? log = null)
    {
        for (int i = applied.Actions.Count - 1; i >= 0; i--)
        {
            ReverseAction(applied.Actions[i], log);
        }
        log?.Invoke("초기화 완료 — 옮긴 항목만 원래대로 되돌렸습니다.");
    }

    private static void ReverseAction(AppliedAction action, Action<string>? log)
    {
        switch (action.Kind)
        {
            case AppliedActionKind.CreatedPath:
                FileSystemExtras.DeletePath(action.Target);
                log?.Invoke($"삭제: {action.Target}");
                break;

            case AppliedActionKind.ReplacedPath:
                FileSystemExtras.DeletePath(action.Target);
                if (action.BackupPath is not null && (Directory.Exists(action.BackupPath) || File.Exists(action.BackupPath)))
                {
                    if (Directory.Exists(action.BackupPath))
                        Directory.Move(action.BackupPath, action.Target);
                    else
                        File.Move(action.BackupPath, action.Target, overwrite: true);
                }
                log?.Invoke($"복원: {action.Target}");
                break;

            case AppliedActionKind.SetRegistryValue:
                RestoreRegistryValue(action);
                log?.Invoke($"레지스트리 복원: {action.Target}");
                break;
        }
    }

    private static void RestoreRegistryValue(AppliedAction action)
    {
        // Slice 2 only writes under HKCU\Control Panel\Mouse.
        var subKey = action.RegistryKey!.Replace(@"HKCU\", "", StringComparison.OrdinalIgnoreCase);
        using var key = Registry.CurrentUser.CreateSubKey(subKey, writable: true);
        var name = action.RegistryValueName!;

        if (action.PreviousExisted)
            key.SetValue(name, action.PreviousValue ?? "", RegistryValueKind.String);
        else
            key.DeleteValue(name, throwOnMissingValue: false);
    }

    public static void SaveApplied(AppliedManifest applied) =>
        File.WriteAllText(AppPaths.AppliedManifestPath, Json.Serialize(applied));

    public static AppliedManifest? LoadLatestApplied() =>
        File.Exists(AppPaths.AppliedManifestPath)
            ? Json.Deserialize<AppliedManifest>(File.ReadAllText(AppPaths.AppliedManifestPath))
            : null;

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }
}
