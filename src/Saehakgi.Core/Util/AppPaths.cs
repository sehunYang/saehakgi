namespace Saehakgi.Core.Util;

/// <summary>Local, per-machine locations saehakgi uses for state and backups.</summary>
public static class AppPaths
{
    public static string Root
    {
        get
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "saehakgi");
            Directory.CreateDirectory(root);
            return root;
        }
    }

    /// <summary>Where the latest import's reversible record is stored (for reset).</summary>
    public static string AppliedManifestPath => Path.Combine(Root, "applied-manifest.json");

    /// <summary>Backups of files/folders/registry replaced during import.</summary>
    public static string BackupsRoot
    {
        get
        {
            var dir = Path.Combine(Root, "backups");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string NewBackupSlot()
    {
        var dir = Path.Combine(BackupsRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string NewStagingDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "saehakgi-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
