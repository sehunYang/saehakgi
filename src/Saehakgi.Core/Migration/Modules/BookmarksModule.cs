using Saehakgi.Core.Manifest;
using Saehakgi.Core.Util;

namespace Saehakgi.Core.Migration.Modules;

/// <summary>
/// Migrates Chromium bookmarks (Chrome + Edge, Default profile) by copying the
/// plaintext <c>Bookmarks</c> JSON file.
///
/// Caveat: Chromium stores a MAC of the bookmarks in <c>Preferences</c> keyed to
/// a machine seed. After a raw copy the browser still loads the bookmarks but may
/// show a one-time "changed elsewhere" notice. Full-fidelity import via the
/// extension API is planned alongside the cookie subsystem (see SPEC.md §3).
/// </summary>
public sealed class BookmarksModule : IMigrationModule
{
    public MigrationItemType Type => MigrationItemType.Bookmarks;
    public string DisplayName => "즐겨찾기 (크롬·엣지)";

    private static IEnumerable<(string Browser, string Path)> KnownBookmarkFiles()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return ("Chrome", Path.Combine(local, @"Google\Chrome\User Data\Default\Bookmarks"));
        yield return ("Edge", Path.Combine(local, @"Microsoft\Edge\User Data\Default\Bookmarks"));
    }

    public IReadOnlyList<ManifestItem> Collect(MigrationRequest request, string payloadRoot)
    {
        var items = new List<ManifestItem>();
        foreach (var (browser, path) in KnownBookmarkFiles())
        {
            if (!File.Exists(path)) continue;

            var rel = Path.Combine("bookmarks", browser + ".json");
            var dest = Path.Combine(payloadRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(path, dest, overwrite: true);

            items.Add(new ManifestItem
            {
                Type = Type,
                DisplayName = $"{browser} 즐겨찾기",
                PayloadPath = rel,
                Meta =
                {
                    ["browser"] = browser,
                    ["tokenizedPath"] = PathTokens.Tokenize(path),
                },
            });
        }
        return items;
    }

    public void Apply(ManifestItem item, string payloadRoot, AppliedManifest applied, MigrationRequest request)
    {
        var source = Path.Combine(payloadRoot, item.PayloadPath!);
        var target = PathTokens.Expand(item.Meta["tokenizedPath"]);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        if (File.Exists(target))
        {
            if (!request.OverwriteExisting) return;
            var backup = Path.Combine(AppPaths.NewBackupSlot(), Path.GetFileName(target));
            File.Copy(target, backup, overwrite: true);
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

        File.Copy(source, target, overwrite: true);
    }
}
