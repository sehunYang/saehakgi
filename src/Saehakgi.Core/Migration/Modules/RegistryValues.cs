using System.Runtime.InteropServices;
using Microsoft.Win32;
using Saehakgi.Core.Manifest;

namespace Saehakgi.Core.Migration.Modules;

/// <summary>One HKCU registry value to migrate: subkey + name + value + kind.</summary>
internal sealed record RegEntry(string SubKey, string Name, string Value, string Kind);

/// <summary>
/// Applies a set of HKCU registry values, recording a reversible action per value
/// (previous value + kind, or "did not exist") so reset restores the exact prior
/// state. Kind-aware so REG_SZ, REG_EXPAND_SZ (e.g. PATH) and REG_DWORD all
/// round-trip correctly.
/// </summary>
internal static class RegistryValues
{
    public static void Apply(IEnumerable<RegEntry> entries, AppliedManifest applied)
    {
        foreach (var e in entries)
        {
            using var key = Registry.CurrentUser.CreateSubKey(e.SubKey, writable: true);
            object? previous = key.GetValue(e.Name);
            string? previousKind = previous is null ? null : key.GetValueKind(e.Name).ToString();

            applied.Actions.Add(new AppliedAction
            {
                Kind = AppliedActionKind.SetRegistryValue,
                Target = $@"HKCU\{e.SubKey}\{e.Name}",
                RegistryKey = $@"HKCU\{e.SubKey}",
                RegistryValueName = e.Name,
                PreviousExisted = previous is not null,
                PreviousValue = previous?.ToString(),
                PreviousValueKind = previousKind,
            });

            var kind = ParseKind(e.Kind);
            key.SetValue(e.Name, Convert(e.Value, kind), kind);
        }
    }

    /// <summary>Reads named values (with their kinds) from an HKCU subkey.</summary>
    public static List<RegEntry> Read(string subKey, IEnumerable<string> names)
    {
        var result = new List<RegEntry>();
        using var key = Registry.CurrentUser.OpenSubKey(subKey);
        if (key is null) return result;

        foreach (var name in names)
        {
            var value = key.GetValue(name);
            if (value is null) continue;
            result.Add(new RegEntry(subKey, name, value.ToString() ?? "", key.GetValueKind(name).ToString()));
        }
        return result;
    }

    /// <summary>Reads every value under an HKCU subkey (used for the Environment key).</summary>
    public static List<RegEntry> ReadAll(string subKey)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey);
        if (key is null) return new List<RegEntry>();
        return Read(subKey, key.GetValueNames());
    }

    public static RegistryValueKind ParseKind(string kind) =>
        Enum.TryParse<RegistryValueKind>(kind, out var k) ? k : RegistryValueKind.String;

    public static object Convert(string value, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => int.Parse(value),
        RegistryValueKind.QWord => long.Parse(value),
        _ => value,
    };

    // ---- Live refresh so changes take effect without a sign-out ----

    private const int HWND_BROADCAST = 0xFFFF;
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    public static void Broadcast(string context)
    {
        SendMessageTimeout(new IntPtr(HWND_BROADCAST), WM_SETTINGCHANGE, IntPtr.Zero, context,
            SMTO_ABORTIFHUNG, 2000, out _);
    }
}
