using System.Text.Json;
using Saehakgi.Core.Manifest;
using Saehakgi.Core.Util;

namespace Saehakgi.Core.Migration.Modules;

/// <summary>
/// Migrates the user's startup programs: the <c>HKCU\...\Run</c> registry entries
/// and the shortcuts in the user's Startup folder. Both use the existing
/// reversible primitives (registry values + file copy), so reset undoes exactly
/// what was added. Machine-wide (HKLM) startup is out of scope for this slice.
/// </summary>
public sealed class StartupProgramsModule : IMigrationModule
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public MigrationItemType Type => MigrationItemType.StartupPrograms;
    public string DisplayName => "시작프로그램";

    public IReadOnlyList<ManifestItem> Collect(MigrationRequest request, string payloadRoot)
    {
        var runEntries = RegistryValues.ReadAll(RunKey);

        var startupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        int fileCount = 0;
        if (Directory.Exists(startupDir))
        {
            var files = Directory.GetFiles(startupDir);
            if (files.Length > 0)
            {
                var dest = Path.Combine(payloadRoot, "startup");
                Directory.CreateDirectory(dest);
                foreach (var file in files)
                {
                    File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
                    fileCount++;
                }
            }
        }

        if (runEntries.Count == 0 && fileCount == 0) return Array.Empty<ManifestItem>();

        return new[]
        {
            new ManifestItem
            {
                Type = Type,
                DisplayName = $"{DisplayName} (Run {runEntries.Count}개 / 시작폴더 {fileCount}개)",
                PayloadPath = fileCount > 0 ? "startup" : null,
                InlineJson = JsonSerializer.Serialize(runEntries),
            },
        };
    }

    public void Apply(ManifestItem item, string payloadRoot, AppliedManifest applied, MigrationRequest request)
    {
        if (item.InlineJson is not null)
        {
            var runEntries = JsonSerializer.Deserialize<List<RegEntry>>(item.InlineJson);
            if (runEntries is { Count: > 0 })
            {
                RegistryValues.Apply(runEntries, applied);
                if (request.ApplyLiveSettings) RegistryValues.Broadcast("Environment");
            }
        }

        if (item.PayloadPath is null) return;

        var source = Path.Combine(payloadRoot, item.PayloadPath);
        if (!Directory.Exists(source)) return;

        var targetDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(source))
        {
            var target = Path.Combine(targetDir, Path.GetFileName(file));
            if (File.Exists(target))
            {
                if (!request.OverwriteExisting) continue;
                var backup = Path.Combine(AppPaths.NewBackupSlot(), Path.GetFileName(target));
                File.Copy(target, backup, overwrite: true);
                applied.Actions.Add(new AppliedAction { Kind = AppliedActionKind.ReplacedPath, Target = target, BackupPath = backup });
            }
            else
            {
                applied.Actions.Add(new AppliedAction { Kind = AppliedActionKind.CreatedPath, Target = target });
            }
            File.Copy(file, target, overwrite: true);
        }
    }
}
