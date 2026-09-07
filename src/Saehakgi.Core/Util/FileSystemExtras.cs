namespace Saehakgi.Core.Util;

public static class FileSystemExtras
{
    /// <summary>Recursively copies a directory tree, preserving empty subfolders.</summary>
    public static void CopyDirectory(string source, string destination)
    {
        source = Path.GetFullPath(source);
        destination = Path.GetFullPath(destination);
        Directory.CreateDirectory(destination);

        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    public static void DeletePath(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else if (File.Exists(path)) File.Delete(path);
    }
}
