using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Saehakgi.Core.Util;

namespace Saehakgi.Core.Native;

/// <summary>
/// Installs/removes the native-messaging host so Chrome and Edge can launch our
/// (non-elevated) bridge exe for the extension. Writes the host manifest and the
/// per-user registry pointers under HKCU.
/// </summary>
public static class NativeHostRegistration
{
    public const string HostName = "com.saehakgi.host";

    /// <summary>
    /// The extension's pinned id, derived from the public key committed in
    /// extension/manifest.json. Fixed on every PC/USB, so the host can be
    /// registered without the user copying a per-machine id.
    /// </summary>
    public const string DefaultExtensionId = "ggchmogolefbbkginnmcepbdallkhfip";

    private static readonly string[] RegistryPaths =
    {
        @"Software\Google\Chrome\NativeMessagingHosts\" + HostName,
        @"Software\Microsoft\Edge\NativeMessagingHosts\" + HostName,
    };

    /// <summary>Writes the manifest and registry keys; returns the manifest path.</summary>
    public static string Register(string hostExePath, string extensionId)
    {
        if (!File.Exists(hostExePath))
            throw new FileNotFoundException("Native host exe not found.", hostExePath);
        if (string.IsNullOrWhiteSpace(extensionId))
            throw new ArgumentException("Extension id is required.", nameof(extensionId));

        var manifestDir = Path.Combine(AppPaths.Root, "host");
        Directory.CreateDirectory(manifestDir);
        var manifestPath = Path.Combine(manifestDir, HostName + ".json");

        var manifest = new JsonObject
        {
            ["name"] = HostName,
            ["description"] = "saehakgi cookie bridge",
            ["path"] = hostExePath,
            ["type"] = "stdio",
            ["allowed_origins"] = new JsonArray($"chrome-extension://{extensionId}/"),
        };
        File.WriteAllText(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        foreach (var path in RegistryPaths)
        {
            using var key = Registry.CurrentUser.CreateSubKey(path, writable: true);
            key.SetValue(null, manifestPath, RegistryValueKind.String); // (Default) = manifest path
        }
        return manifestPath;
    }

    public static void Unregister()
    {
        foreach (var path in RegistryPaths)
        {
            try { Registry.CurrentUser.DeleteSubKey(path, throwOnMissingSubKey: false); }
            catch { /* ignore */ }
        }
        var manifestPath = Path.Combine(AppPaths.Root, "host", HostName + ".json");
        try { if (File.Exists(manifestPath)) File.Delete(manifestPath); } catch { /* ignore */ }
    }
}
