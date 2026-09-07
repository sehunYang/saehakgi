using System.Runtime.InteropServices;
using System.Text.Json;
using Saehakgi.Core.Manifest;
using Saehakgi.Core.Util;

namespace Saehakgi.Core.Migration.Modules;

/// <summary>One user-installed font: its registry title and the font file name.</summary>
internal sealed record FontEntry(string RegName, string FileName);

/// <summary>
/// Migrates per-user installed fonts (those under HKCU + %LOCALAPPDATA%\Microsoft\
/// Windows\Fonts). Copies the font files and re-registers them; both the files and
/// the registry entries use the existing reversible primitives, so reset removes
/// exactly the fonts that were added. Machine-wide fonts (needing admin) are out of
/// scope for this slice.
/// </summary>
public sealed class FontsModule : IMigrationModule
{
    private const string FontsKey = @"Software\Microsoft\Windows NT\CurrentVersion\Fonts";

    public MigrationItemType Type => MigrationItemType.Fonts;
    public string DisplayName => "설치 폰트 (사용자)";

    private static string UserFontsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\Windows\Fonts");

    public IReadOnlyList<ManifestItem> Collect(MigrationRequest request, string payloadRoot)
    {
        var registered = RegistryValues.ReadAll(FontsKey);
        if (registered.Count == 0) return Array.Empty<ManifestItem>();

        var dest = Path.Combine(payloadRoot, "fonts");
        var fonts = new List<FontEntry>();

        foreach (var entry in registered)
        {
            var file = ResolveFontFile(entry.Value);
            if (file is null || !File.Exists(file)) continue; // system font stored by name only — skip

            Directory.CreateDirectory(dest);
            var fileName = Path.GetFileName(file);
            File.Copy(file, Path.Combine(dest, fileName), overwrite: true);
            fonts.Add(new FontEntry(entry.Name, fileName));
        }

        if (fonts.Count == 0) return Array.Empty<ManifestItem>();

        return new[]
        {
            new ManifestItem
            {
                Type = Type,
                DisplayName = $"{DisplayName} — {fonts.Count}개",
                PayloadPath = "fonts",
                InlineJson = JsonSerializer.Serialize(fonts),
            },
        };
    }

    public void Apply(ManifestItem item, string payloadRoot, AppliedManifest applied, MigrationRequest request)
    {
        if (item.InlineJson is null || item.PayloadPath is null) return;
        var fonts = JsonSerializer.Deserialize<List<FontEntry>>(item.InlineJson);
        if (fonts is null || fonts.Count == 0) return;

        var source = Path.Combine(payloadRoot, item.PayloadPath);
        var targetDir = UserFontsDir;
        Directory.CreateDirectory(targetDir);

        var regEntries = new List<RegEntry>();
        var installedPaths = new List<string>();

        foreach (var font in fonts)
        {
            var src = Path.Combine(source, font.FileName);
            if (!File.Exists(src)) continue;
            var target = Path.Combine(targetDir, font.FileName);

            if (File.Exists(target))
            {
                if (!request.OverwriteExisting) continue;
                var backup = Path.Combine(AppPaths.NewBackupSlot(), font.FileName);
                File.Copy(target, backup, overwrite: true);
                applied.Actions.Add(new AppliedAction { Kind = AppliedActionKind.ReplacedPath, Target = target, BackupPath = backup });
            }
            else
            {
                applied.Actions.Add(new AppliedAction { Kind = AppliedActionKind.CreatedPath, Target = target });
            }
            File.Copy(src, target, overwrite: true);

            regEntries.Add(new RegEntry(FontsKey, font.RegName, target, "String"));
            installedPaths.Add(target);
        }

        RegistryValues.Apply(regEntries, applied);

        if (request.ApplyLiveSettings)
        {
            foreach (var path in installedPaths) AddFontResource(path);
            if (installedPaths.Count > 0) BroadcastFontChange();
        }
    }

    private static string? ResolveFontFile(string registryData)
    {
        if (string.IsNullOrWhiteSpace(registryData)) return null;
        return Path.IsPathRooted(registryData)
            ? registryData
            : Path.Combine(UserFontsDir, registryData);
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Auto)]
    private static extern int AddFontResource(string lpFileName);

    private const int HWND_BROADCAST = 0xFFFF;
    private const uint WM_FONTCHANGE = 0x001D;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    private static void BroadcastFontChange() =>
        SendMessageTimeout(new IntPtr(HWND_BROADCAST), WM_FONTCHANGE, IntPtr.Zero, IntPtr.Zero, 0x0002, 2000, out _);
}
