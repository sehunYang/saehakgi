using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Runtime.InteropServices;
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
    private const string ChecklistFileName = "설치-체크리스트.html";

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

        // Icons parsed now, embedded, so the new PC shows them without the apps installed.
        File.WriteAllText(Path.Combine(dir, "icons.json"), JsonSerializer.Serialize(ExtractIcons()));

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

        var programsTxt = Path.Combine(target, "programs.txt");
        if (File.Exists(programsTxt))
        {
            var wingetJson = Path.Combine(target, "winget.json");
            var iconsJson = Path.Combine(target, "icons.json");
            WriteChecklistHtml(programsTxt, File.Exists(wingetJson) ? wingetJson : null,
                File.Exists(iconsJson) ? iconsJson : null, Path.Combine(target, ChecklistFileName),
                failedWingetIds: null);
        }
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

    /// <summary>Desktop folder where import drops the reinstall list, script, and checklist.</summary>
    public static string DesktopReinstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), TargetFolderName);

    public static string DesktopReinstallJsonPath => Path.Combine(DesktopReinstallDir, "winget.json");
    public static string DesktopChecklistPath => Path.Combine(DesktopReinstallDir, ChecklistFileName);

    /// <summary>
    /// Rewrites the desktop checklist. Pass the winget ids that failed to install so
    /// they are demoted into the manual list; pass null before any install attempt.
    /// </summary>
    public static void RegenerateDesktopChecklist(IReadOnlyCollection<string>? failedWingetIds)
    {
        var programs = Path.Combine(DesktopReinstallDir, "programs.txt");
        if (!File.Exists(programs)) return;
        var winget = Path.Combine(DesktopReinstallDir, "winget.json");
        var icons = Path.Combine(DesktopReinstallDir, "icons.json");
        WriteChecklistHtml(programs, File.Exists(winget) ? winget : null,
            File.Exists(icons) ? icons : null, DesktopChecklistPath, failedWingetIds);
    }

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

    internal static List<InstalledProgram> ParsePrograms(string programsTxtPath)
    {
        var list = new List<InstalledProgram>();
        foreach (var line in File.ReadAllLines(programsTxtPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split('\t');
            list.Add(new InstalledProgram(
                parts.ElementAtOrDefault(0) ?? "",
                parts.ElementAtOrDefault(1) ?? "",
                parts.ElementAtOrDefault(2) ?? ""));
        }
        return list;
    }

    /// <summary>
    /// Writes an offline HTML checklist of programs that still need manual install:
    /// those not matched to any winget id, plus — after an install attempt — the
    /// winget ids that failed (pass their ids in <paramref name="failedWingetIds"/>;
    /// null before any attempt). Matching is a conservative heuristic — when unsure a
    /// program stays on the manual list. Icons parsed at export time are embedded;
    /// checkbox state persists in the browser's localStorage.
    /// </summary>
    internal static void WriteChecklistHtml(string programsTxtPath, string? wingetJsonPath, string? iconsJsonPath, string htmlPath, IReadOnlyCollection<string>? failedWingetIds)
    {
        var programs = ParsePrograms(programsTxtPath);
        var tokens = WingetProductTokens(wingetJsonPath is not null ? ReadPackageIds(wingetJsonPath) : new List<string>());
        var icons = LoadIcons(iconsJsonPath);
        var failedTokens = failedWingetIds is { Count: > 0 } ? WingetProductTokens(failedWingetIds) : null;

        var manual = new List<InstalledProgram>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in programs)
        {
            bool covered = IsCoveredByWinget(p.Name, tokens);
            bool needsManual = !covered || (failedTokens is not null && IsCoveredByWinget(p.Name, failedTokens));
            if (needsManual && seen.Add(p.Name)) manual.Add(p);
        }
        if (failedWingetIds is not null)
            foreach (var id in failedWingetIds)
            {
                var idTokens = WingetProductTokens(new[] { id });
                if (!programs.Any(p => IsCoveredByWinget(p.Name, idTokens)) && seen.Add(id))
                    manual.Add(new InstalledProgram(id, "", "winget id"));
            }
        manual.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        var sb = new StringBuilder();
        sb.Append("""
<!doctype html><html lang="ko"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>saehakgi 설치 체크리스트</title>
<style>
:root{--bg:#f7f7f8;--fg:#1a1a1a;--card:#fff;--muted:#666;--line:#e3e3e6;--accent:#2f6df6;--done:#8a8a8a}
@media(prefers-color-scheme:dark){:root{--bg:#16171a;--fg:#e8e8ea;--card:#212226;--muted:#9a9aa0;--line:#33343a;--accent:#6c9bff;--done:#6a6a70}}
*{box-sizing:border-box}body{margin:0;font:15px/1.5 'Segoe UI',system-ui,sans-serif;background:var(--bg);color:var(--fg)}
header{position:sticky;top:0;background:var(--card);border-bottom:1px solid var(--line);padding:14px 18px;z-index:1}
h1{font-size:18px;margin:0 0 2px}.sub{color:var(--muted);font-size:13px}
.counts{margin-top:8px;font-size:14px}.counts b{color:var(--accent)}
.tools{padding:12px 18px;display:flex;gap:8px;align-items:center;flex-wrap:wrap}
input[type=search]{flex:1;min-width:180px;padding:8px 10px;border:1px solid var(--line);border-radius:8px;background:var(--card);color:var(--fg)}
button{padding:8px 12px;border:1px solid var(--line);border-radius:8px;background:var(--card);color:var(--fg);cursor:pointer}
main{padding:0 18px 48px}section{margin-top:18px}
h2{font-size:15px;border-left:3px solid var(--accent);padding-left:8px;display:flex;justify-content:space-between}
.secount{color:var(--muted);font-weight:400;font-size:13px}
ul{list-style:none;margin:0;padding:0;border:1px solid var(--line);border-radius:10px;overflow:hidden;background:var(--card)}
li{border-top:1px solid var(--line)}li:first-child{border-top:0}
li:nth-child(even){background:rgba(127,127,127,.05)}
label{display:flex;align-items:center;gap:10px;padding:10px 12px;cursor:pointer}
input[type=checkbox]{width:18px;height:18px;flex:0 0 auto}
.ico{width:20px;height:20px;flex:0 0 auto;border-radius:4px;object-fit:contain}.ico.ph{background:var(--line)}
.name{font-weight:600}.meta{color:var(--muted);font-size:12px;margin-left:auto;text-align:right;padding-left:10px}
label:has(input:checked) .name{text-decoration:line-through;color:var(--done);font-weight:400}
.empty{padding:14px;color:var(--muted)}
@media print{header{position:static}.tools{display:none}}
</style></head><body>
<header>
<h1>saehakgi 설치 체크리스트</h1>
<div class="sub">새 PC에서 <b>직접 설치</b>해야 하는 프로그램만 모았습니다. 설치한 항목을 체크하세요(체크는 이 브라우저에 저장됩니다).</div>
<div class="counts">완료 <b id="progress">0</b> / 전체
""");
        sb.Append(manual.Count).Append("""
</div></header>
<div class="tools"><input type="search" id="filter" placeholder="프로그램 검색…"><button onclick="window.print()">인쇄</button></div>
<main>
<section><h2>직접 설치가 필요한 프로그램</h2>
<div class="sub" style="padding:4px 0 8px">winget으로 자동 설치되지 않거나 설치에 실패한 목록입니다. 벤더 사이트나 학교 SW센터에서 직접 설치하세요.</div>
<ul>
""");
        AppendRows(sb, manual, icons);
        sb.Append("""
</ul></section></main>
<script>
var KEY='saehakgi:checklist:';
function updateCounts(){
  document.querySelectorAll('section').forEach(function(s){
    var b=s.querySelectorAll('input[type=checkbox]');
    var d=[].filter.call(b,function(x){return x.checked;}).length;
    var c=s.querySelector('.secount'); if(c)c.textContent=d+' / '+b.length;
  });
  var all=document.querySelectorAll('input[type=checkbox]');
  document.getElementById('progress').textContent=[].filter.call(all,function(x){return x.checked;}).length;
}
document.querySelectorAll('input[type=checkbox]').forEach(function(b){
  var k=KEY+b.dataset.key;
  try{ if(localStorage.getItem(k)==='1') b.checked=true; }catch(e){}
  b.addEventListener('change',function(){ try{ b.checked?localStorage.setItem(k,'1'):localStorage.removeItem(k);}catch(e){} updateCounts(); });
});
document.getElementById('filter').addEventListener('input',function(e){
  var q=e.target.value.trim().toLowerCase();
  document.querySelectorAll('li').forEach(function(li){ li.style.display=li.textContent.toLowerCase().indexOf(q)>=0?'':'none'; });
});
updateCounts();
</script></body></html>
""");
        File.WriteAllText(htmlPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static void AppendRows(StringBuilder sb, List<InstalledProgram> items, Dictionary<string, string> icons)
    {
        if (items.Count == 0) { sb.Append("<li class=\"empty\">직접 설치할 항목이 없습니다. 🎉</li>\n"); return; }
        foreach (var p in items)
        {
            var name = WebUtility.HtmlEncode(p.Name);
            var metaText = string.Join(" · ", new[] { p.Version, p.Publisher }.Where(x => !string.IsNullOrWhiteSpace(x)));
            var meta = WebUtility.HtmlEncode(metaText);
            var key = WebUtility.HtmlEncode(p.Name);
            sb.Append("<li><label><input type=\"checkbox\" data-key=\"").Append(key).Append("\">");
            if (icons.TryGetValue(p.Name, out var uri))
                sb.Append("<img class=\"ico\" src=\"").Append(uri).Append("\" alt=\"\">");
            else
                sb.Append("<span class=\"ico ph\"></span>");
            sb.Append("<span class=\"name\">").Append(name).Append("</span>")
              .Append("<span class=\"meta\">").Append(meta).Append("</span></label></li>\n");
        }
    }

    private static Dictionary<string, string> LoadIcons(string? iconsJsonPath)
    {
        if (iconsJsonPath is null || !File.Exists(iconsJsonPath)) return new Dictionary<string, string>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(iconsJsonPath))
                   ?? new Dictionary<string, string>();
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    private static HashSet<string> WingetProductTokens(IEnumerable<string> wingetIds)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in wingetIds)
        {
            var whole = NormalizeName(id);
            if (whole.Length >= 4) set.Add(whole);

            var segments = id.Split('.');
            var last = NormalizeName(segments[^1]);
            if (last.Length >= 4) set.Add(last);
            if (segments.Length >= 2)
            {
                var lastTwo = NormalizeName(segments[^2] + segments[^1]);
                if (lastTwo.Length >= 4) set.Add(lastTwo);
            }
        }
        return set;
    }

    private static bool IsCoveredByWinget(string displayName, HashSet<string> tokens)
    {
        var d = NormalizeName(displayName);
        if (d.Length == 0) return false;
        foreach (var t in tokens)
            if (t.Length >= 4 && d.Contains(t, StringComparison.Ordinal)) return true;
        return false;
    }

    private static string NormalizeName(string s) =>
        new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    internal static List<InstalledProgram> EnumerateInstalled() =>
        EnumerateRaw().Select(x => x.Program).ToList();

    /// <summary>Maps each program's display name to a small PNG data URI of its icon.</summary>
    internal static Dictionary<string, string> ExtractIcons()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (prog, iconSource) in EnumerateRaw())
        {
            if (map.ContainsKey(prog.Name)) continue;
            var uri = IconDataUri(iconSource);
            if (uri is not null) map[prog.Name] = uri;
        }
        return map;
    }

    private static List<(InstalledProgram Program, string? IconSource)> EnumerateRaw()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(InstalledProgram, string?)>();

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
                        var prog = new InstalledProgram(
                            display,
                            app.GetValue("DisplayVersion") as string ?? "",
                            app.GetValue("Publisher") as string ?? "");
                        result.Add((prog, app.GetValue("DisplayIcon") as string));
                    }
                    catch { /* skip a malformed entry */ }
                }
            }
            catch { /* skip an inaccessible hive/view */ }
        }

        result.Sort((a, b) => string.Compare(a.Item1.Name, b.Item1.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    // ---- Icon extraction (export-time; embedded so the new PC needs nothing installed) ----

    private static string? IconDataUri(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon)) return null;
        var (path, index) = ParseIconRef(displayIcon);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        try
        {
            using var icon = LoadIcon(path, index);
            if (icon is null) return null;
            using var bmp = icon.ToBitmap();
            using var resized = new Bitmap(bmp, new Size(20, 20));
            using var ms = new MemoryStream();
            resized.Save(ms, ImageFormat.Png);
            return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch
        {
            return null;
        }
    }

    private static (string Path, int Index) ParseIconRef(string raw)
    {
        raw = raw.Trim().Trim('"');
        int comma = raw.LastIndexOf(',');
        if (comma > 1 && int.TryParse(raw[(comma + 1)..].Trim(), out var index))
            return (raw[..comma].Trim().Trim('"'), index);
        return (raw, 0);
    }

    private static Icon? LoadIcon(string path, int index)
    {
        if (string.Equals(Path.GetExtension(path), ".ico", StringComparison.OrdinalIgnoreCase))
        {
            try { return new Icon(path); } catch { return null; }
        }

        var handle = ExtractIconByIndex(path, index);
        if (handle != IntPtr.Zero)
        {
            try { return (Icon)Icon.FromHandle(handle).Clone(); }
            finally { DestroyIcon(handle); }
        }

        try { return Icon.ExtractAssociatedIcon(path); } catch { return null; }
    }

    private static IntPtr ExtractIconByIndex(string path, int index)
    {
        var large = new IntPtr[1];
        try { return ExtractIconEx(path, index, large, null, 1) > 0 ? large[0] : IntPtr.Zero; }
        catch { return IntPtr.Zero; }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
