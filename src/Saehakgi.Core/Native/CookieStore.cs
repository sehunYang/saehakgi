using System.Text;
using Saehakgi.Core.Crypto;

namespace Saehakgi.Core.Native;

/// <summary>
/// Persists the browser cookie set (as the extension's JSON) to a passphrase-
/// encrypted file on the USB, reusing the same chunked AES-256-GCM stream as the
/// main bundle. The cookies are only ever plaintext in memory here; on disk they
/// are encrypted.
/// </summary>
public static class CookieStore
{
    public static void Save(string path, string cookiesJson, string passphrase)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var plain = new MemoryStream(Encoding.UTF8.GetBytes(cookiesJson));
        using var output = File.Create(path);
        SecureBundleStream.Encrypt(plain, output, passphrase);
    }

    public static string Load(string path, string passphrase)
    {
        using var input = File.OpenRead(path);
        using var output = new MemoryStream();
        SecureBundleStream.Decrypt(input, output, passphrase);
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
