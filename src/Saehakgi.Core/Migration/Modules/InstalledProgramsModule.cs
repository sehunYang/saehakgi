using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Saehakgi.Core.Manifest;
using Saehakgi.Core.Util;

namespace Saehakgi.Core.Migration.Modules;

/// <summary>A program found in the Windows uninstall registry.</summary>
internal readonly record struct InstalledProgram(string Name, string Version, string Publisher);

/// <summary>
/// Captures what's installed so the new PC can be brought up to match:
///   • <c>winget export</c> → a machine-readable list winget can reinstall
///   • the full uninstall-registry inventory → a human-readable reference for
///     everything winget doesn't know about
/// Import does NOT silently install anything (that would mean dozens of unattended
/// UAC prompts). Instead it drops the list plus an <c>install.cmd</c> onto the
/// desktop for the user to run. Reset just deletes that folder.
/// </summary>
public sealed class InstalledProgramsModule : IMigrationModule
{
    private const string TargetFolderName = "saehakgi-재설치";

    public MigrationItemType Type => MigrationItemType.InstalledPrograms;
    public string DisplayName => "설치 프로그램 목록";

    public IReadOnlyList<ManifestItem> Collect(MigrationRequest request, string payloadRoot)
    {
        var dir = Path.Combine(payloadRoot, "programs");
        Directory.CreateDirectory(dir);

        int wingetCount = 0;
        var wingetJson = Path.Combine(dir, "winget.json");
        var fullExport = Path.Combine(dir, "winget-full.json");
        var res = ProcessRunner.Run("winget", $"export -o \"{fullExport}\" --accept-source-agreements", 120_000);
        if (res.Ok && File.Exists(fullExport))
        {
            if (request.SelectedWingetIds is { } selected)
                WriteFilteredJson(fullExport, wingetJson, new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase));
            else
                File.Copy(fullExport, wingetJson, overwrite: true);

            wingetCount = CountWingetPackages(wingetJson);
            try { File.Delete(fullExport); } catch { /* best effort */ }
        }

        var installed = EnumerateInstalled();
        File.WriteAllLines(
            Path.Combine(dir, "programs.txt"),
            installed.Select(p => $"{p.Name}\t{p.Version}\t{p.Publisher}"),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        return new[]
        {
            new ManifestItem
            {
                Type = Type,
                DisplayName = $"{DisplayName} (winget {wingetCount}개 / 전체 {installed.Count}개)",
                PayloadPath = "programs",
                Meta = { ["wingetPackages"] = wingetCount.ToString(), ["totalPrograms"] = installed.Count.ToString() },
            },
        };
    }

    public void Apply(ManifestItem item, string payloadRoot, AppliedManifest applied, MigrationRequest request)
    {
        var source = Path.Combine(payloadRoot, item.PayloadPath!);
        var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), TargetFolderName);

        if (Directory.Exists(target))
        {
            var backup = Path.Combine(AppPaths.NewBackupSlot(), TargetFolderName);
            Directory.Move(target, backup);
            applied.Actions.Add(new AppliedAction { Kind = AppliedActionKind.ReplacedPath, Target = target, BackupPath = backup });
        }
        else
        {
            applied.Actions.Add(new AppliedAction { Kind = AppliedActionKind.CreatedPath, Target = target });
        }

        FileSystemExtras.CopyDirectory(source, target);
        File.WriteAllText(Path.Combine(target, "install.cmd"), InstallScript(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string InstallScript() =>
        "@echo off\r\n" +
        "chcp 65001 >nul\r\n" +
        "echo saehakgi - winget 일괄 재설치\r\n" +
        "if not exist \"%~dp0winget.json\" ( echo winget.json 이 없습니다. & pause & exit /b 1 )\r\n" +
        "winget import -i \"%~dp0winget.json\" --accept-source-agreements --accept-package-agreements --ignore-unavailable\r\n" +
        "echo.\r\n" +
        "echo 완료. winget 이 모르는 프로그램은 programs.txt 를 참고해 수동 설치하세요.\r\n" +
        "pause\r\n";

    private static int CountWingetPackages(string wingetJsonPath) => ReadPackageIds(wingetJsonPath).Count;

    /// <summary>Path on the desktop where import drops the reinstall list + script.</summary>
    public static string DesktopReinstallJsonPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), TargetFolderName, "winget.json");

    /// <summary>Runs <c>winget export</c> and returns the installed package identifiers (for the picker).</summary>
    public static List<string> ListWingetIds()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "saehakgi-wg-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var res = ProcessRunner.Run("winget", $"export -o \"{tmp}\" --accept-source-agreements", 120_000);
            return res.Ok && File.Exists(tmp) ? ReadPackageIds(tmp) : new List<string>();
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    public static List<string> ReadPackageIds(string wingetJsonPath)
    {
        var ids = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(wingetJsonPath));
            if (doc.RootElement.TryGetProperty("Sources", out var sources))
                foreach (var src in sources.EnumerateArray())
                    if (src.TryGetProperty("Packages", out var pkgs))
                        foreach (var p in pkgs.EnumerateArray())
                            if (p.TryGetProperty("PackageIdentifier", out var id) && id.GetString() is { } s)
                                ids.Add(s);
        }
        catch
        {
            // Unreadable export — treat as empty.
        }
        ids.Sort(StringComparer.OrdinalIgnoreCase);
        return ids;
    }

    internal static void WriteFilteredJson(string srcPath, string destPath, HashSet<string> keepIds)
    {
        var root = JsonNode.Parse(File.ReadAllText(srcPath))!;
        if (root["Sources"] is JsonArray sources)
        {
            foreach (var src in sources)
            {
                if (src?["Packages"] is not JsonArray pkgs) continue;
                var kept = new JsonArray();
                foreach (var p in pkgs)
                {
                    var id = p?["PackageIdentifier"]?.GetValue<string>();
                    if (id is not null && keepIds.Contains(id)) kept.Add(p!.DeepClone());
                }
                src["Packages"] = kept;
            }
        }
        File.WriteAllText(destPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static List<InstalledProgram> EnumerateInstalled()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<InstalledProgram>();

        var roots = new (RegistryHive Hive, RegistryView View)[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Registry64),
        };

        foreach (var (hive, view) in roots)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;

                foreach (var name in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using var app = uninstall.OpenSubKey(name);
                        if (app is null) continue;
                        var display = app.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(display)) continue;
                        if (app.GetValue("SystemComponent") is int sc && sc == 1) continue;
                        if (app.GetValue("ParentKeyName") is not null) continue; // updates/hotfixes

                        if (!seen.Add(display)) continue;
                        result.Add(new InstalledProgram(
                            display,
                            app.GetValue("DisplayVersion") as string ?? "",
                            app.GetValue("Publisher") as string ?? ""));
                    }
                    catch { /* skip a malformed entry */ }
                }
            }
            catch { /* skip an inaccessible hive/view */ }
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }
}
