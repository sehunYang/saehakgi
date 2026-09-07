using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using Saehakgi.Core.Manifest;

namespace Saehakgi.Core.Migration.Modules;

/// <summary>
/// Migrates Windows mouse settings from <c>HKCU\Control Panel\Mouse</c>
/// (pointer speed, acceleration thresholds, button swap, double-click speed).
/// These values are REG_SZ. On apply, the registry is written and — when
/// requested — the change is pushed live via SystemParametersInfo.
/// </summary>
public sealed class MouseSettingsModule : IMigrationModule
{
    public MigrationItemType Type => MigrationItemType.MouseSettings;
    public string DisplayName => "마우스 설정";

    private const string SubKey = @"Control Panel\Mouse";

    private static readonly string[] ValueNames =
    {
        "MouseSpeed", "MouseThreshold1", "MouseThreshold2",
        "MouseSensitivity", "SwapMouseButtons", "DoubleClickSpeed",
    };

    public IReadOnlyList<ManifestItem> Collect(MigrationRequest request, string payloadRoot)
    {
        using var key = Registry.CurrentUser.OpenSubKey(SubKey);
        if (key is null) return Array.Empty<ManifestItem>();

        var values = new Dictionary<string, string>();
        foreach (var name in ValueNames)
        {
            if (key.GetValue(name) is { } v) values[name] = v.ToString() ?? "";
        }
        if (values.Count == 0) return Array.Empty<ManifestItem>();

        return new[]
        {
            new ManifestItem
            {
                Type = Type,
                DisplayName = DisplayName,
                InlineJson = JsonSerializer.Serialize(values),
            },
        };
    }

    public void Apply(ManifestItem item, string payloadRoot, AppliedManifest applied, MigrationRequest request)
    {
        if (item.InlineJson is null) return;
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(item.InlineJson);
        if (values is null) return;

        using var key = Registry.CurrentUser.CreateSubKey(SubKey, writable: true);
        foreach (var (name, newValue) in values)
        {
            object? previous = key.GetValue(name);
            applied.Actions.Add(new AppliedAction
            {
                Kind = AppliedActionKind.SetRegistryValue,
                Target = $@"HKCU\{SubKey}\{name}",
                RegistryKey = $@"HKCU\{SubKey}",
                RegistryValueName = name,
                PreviousExisted = previous is not null,
                PreviousValue = previous?.ToString(),
                PreviousValueKind = nameof(RegistryValueKind.String),
            });
            key.SetValue(name, newValue, RegistryValueKind.String);
        }

        if (request.ApplyLiveSettings) PushMouseSettingsLive(values);
    }

    /// <summary>Pushes the applied values to the running session so they take effect now.</summary>
    public static void PushMouseSettingsLive(IReadOnlyDictionary<string, string> values)
    {
        if (values.TryGetValue("MouseSensitivity", out var sens) && int.TryParse(sens, out var speed))
        {
            speed = Math.Clamp(speed, 1, 20);
            SystemParametersInfo(SPI_SETMOUSESPEED, 0, new IntPtr(speed), SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
        }

        if (values.TryGetValue("SwapMouseButtons", out var swap))
        {
            SwapMouseButton(swap == "1");
        }
    }

    private const uint SPI_SETMOUSESPEED = 0x0071;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDCHANGE = 0x02;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uAction, uint uParam, IntPtr pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SwapMouseButton(bool fSwap);
}
