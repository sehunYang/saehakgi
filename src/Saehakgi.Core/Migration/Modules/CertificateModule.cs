using System.Security.Cryptography.X509Certificates;
using Saehakgi.Core.Manifest;
using Saehakgi.Core.Util;

namespace Saehakgi.Core.Migration.Modules;

/// <summary>A place to look for certificate stores and where to restore them to.</summary>
internal sealed record CertStoreRoot(string DetectPath, string StoreType, string RestoreBaseTokenized);

/// <summary>
/// Migrates Korean public/government certificates (NPKI/GPKI). Certificates live
/// as a directory per identity containing a cert file (SignCert.der) and an
/// encrypted private key (SignPri.key). We scan every standard location — plus
/// every ready drive root and USB (\NPKI, \GPKI) — and, on import, normalize each
/// one back into the standard LocalLow store so the new PC's cert software finds it.
///
/// Private keys stay password-encrypted the whole time; we only copy files, never
/// decrypt. The copies live only inside the AES-256-GCM bundle.
/// </summary>
public sealed class CertificateModule : IMigrationModule
{
    private readonly IReadOnlyList<CertStoreRoot> _roots;
    private const int MaxDepth = 8;

    public CertificateModule() : this(DefaultRoots()) { }

    internal CertificateModule(IReadOnlyList<CertStoreRoot> roots) => _roots = roots;

    public MigrationItemType Type => MigrationItemType.Certificate;
    public string DisplayName => "공동인증서 (GPKI/NPKI)";

    private static IReadOnlyList<CertStoreRoot> DefaultRoots()
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        const string restoreNpki = @"%USERPROFILE%\AppData\LocalLow\NPKI";
        const string restoreGpki = @"%USERPROFILE%\AppData\LocalLow\GPKI";

        var roots = new List<CertStoreRoot>
        {
            new(Path.Combine(user, @"AppData\LocalLow\NPKI"), "NPKI", restoreNpki),
            new(Path.Combine(user, @"AppData\LocalLow\GPKI"), "GPKI", restoreGpki),
            new(@"C:\Program Files\NPKI", "NPKI", restoreNpki),
            new(@"C:\Program Files (x86)\NPKI", "NPKI", restoreNpki),
            new(Path.Combine(local, "LowiSA"), "NPKI", restoreNpki),
        };

        foreach (var drive in SafeDrives())
        {
            roots.Add(new(Path.Combine(drive, "NPKI"), "NPKI", restoreNpki));
            roots.Add(new(Path.Combine(drive, "GPKI"), "GPKI", restoreGpki));
        }
        return roots;
    }

    private static IEnumerable<string> SafeDrives()
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch { yield break; }
        foreach (var d in drives)
        {
            string? root = null;
            try { if (d.IsReady) root = d.RootDirectory.FullName; }
            catch { root = null; }
            if (root is not null) yield return root;
        }
    }

    public IReadOnlyList<ManifestItem> Collect(MigrationRequest request, string payloadRoot)
    {
        var items = new List<ManifestItem>();
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in _roots)
        {
            if (!Directory.Exists(root.DetectPath)) continue;

            foreach (var certDir in FindCertDirs(root.DetectPath))
            {
                var rel = Path.GetRelativePath(root.DetectPath, certDir);
                var restoreTarget = Path.Combine(root.RestoreBaseTokenized, rel);
                if (!seenTargets.Add(restoreTarget)) continue; // same cert seen via another root

                var id = Guid.NewGuid().ToString("N");
                var payloadRel = Path.Combine("certs", id);
                FileSystemExtras.CopyDirectory(certDir, Path.Combine(payloadRoot, payloadRel));

                var (subject, notAfter) = DescribeCert(certDir);
                var expiry = notAfter is { } na ? $" (~{na:yyyy-MM-dd})" : "";

                items.Add(new ManifestItem
                {
                    Id = id,
                    Type = Type,
                    DisplayName = $"{root.StoreType}: {subject}{expiry}",
                    PayloadPath = payloadRel,
                    Meta =
                    {
                        ["storeType"] = root.StoreType,
                        ["restoreTarget"] = restoreTarget,
                        ["sourceDir"] = certDir,
                    },
                });
            }
        }
        return items;
    }

    public void Apply(ManifestItem item, string payloadRoot, AppliedManifest applied, MigrationRequest request)
    {
        var source = Path.Combine(payloadRoot, item.PayloadPath!);
        var target = PathTokens.Expand(item.Meta["restoreTarget"]);

        if (Directory.Exists(target))
        {
            if (!request.OverwriteExisting) return;
            var backup = Path.Combine(AppPaths.NewBackupSlot(), Path.GetFileName(target.TrimEnd('\\', '/')));
            Directory.Move(target, backup);
            applied.Actions.Add(new AppliedAction
            {
                Kind = AppliedActionKind.ReplacedPath,
                Target = target,
                BackupPath = backup,
            });
        }
        else
        {
            applied.Actions.Add(new AppliedAction { Kind = AppliedActionKind.CreatedPath, Target = target });
        }

        FileSystemExtras.CopyDirectory(source, target);
    }

    /// <summary>Breadth-first walk that skips folders we can't read, bounded in depth.</summary>
    private static IEnumerable<string> FindCertDirs(string root)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();

            if (IsCertDir(dir)) yield return dir;
            if (depth >= MaxDepth) continue;

            string[] children;
            try { children = Directory.GetDirectories(dir); }
            catch { continue; } // access denied / gone
            foreach (var child in children) queue.Enqueue((child, depth + 1));
        }
    }

    private static bool IsCertDir(string dir)
    {
        try
        {
            bool hasCert = File.Exists(Path.Combine(dir, "SignCert.der"))
                           || Directory.EnumerateFiles(dir, "*.der").Any()
                           || Directory.EnumerateFiles(dir, "*.cer").Any();
            bool hasKey = File.Exists(Path.Combine(dir, "SignPri.key"))
                          || Directory.EnumerateFiles(dir, "*.key").Any();
            return hasCert && hasKey;
        }
        catch
        {
            return false;
        }
    }

    private static (string Subject, DateTime? NotAfter) DescribeCert(string dir)
    {
        try
        {
            var certFile = File.Exists(Path.Combine(dir, "SignCert.der"))
                ? Path.Combine(dir, "SignCert.der")
                : Directory.EnumerateFiles(dir, "*.der").Concat(Directory.EnumerateFiles(dir, "*.cer")).FirstOrDefault();

            if (certFile is not null)
            {
                using var cert = new X509Certificate2(File.ReadAllBytes(certFile));
                var name = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
                return (string.IsNullOrWhiteSpace(name) ? Path.GetFileName(dir) : name, cert.NotAfter);
            }
        }
        catch
        {
            // Unreadable/oddly-encoded cert — fall back to the folder name.
        }
        return (Path.GetFileName(dir), null);
    }
}
