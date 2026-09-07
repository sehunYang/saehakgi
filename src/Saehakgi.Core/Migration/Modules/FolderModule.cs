using Saehakgi.Core.Manifest;
using Saehakgi.Core.Util;

namespace Saehakgi.Core.Migration.Modules;

/// <summary>Migrates user-selected folders (recursively). Paths are tokenized so a
/// folder under the user profile lands correctly even if the account name differs.</summary>
public sealed class FolderModule : IMigrationModule
{
    public MigrationItemType Type => MigrationItemType.Folder;
    public string DisplayName => "폴더";

    public IReadOnlyList<ManifestItem> Collect(MigrationRequest request, string payloadRoot)
    {
        var items = new List<ManifestItem>();
        foreach (var raw in request.FolderPaths)
        {
            var path = raw.TrimEnd('\\', '/');
            if (!Directory.Exists(path)) continue;

            var id = Guid.NewGuid().ToString("N");
            var rel = Path.Combine("folders", id);
            FileSystemExtras.CopyDirectory(path, Path.Combine(payloadRoot, rel));

            items.Add(new ManifestItem
            {
                Id = id,
                Type = Type,
                DisplayName = Path.GetFileName(path) is { Length: > 0 } name ? name : path,
                PayloadPath = rel,
                Meta =
                {
                    ["originalPath"] = path,
                    ["tokenizedPath"] = PathTokens.Tokenize(path),
                },
            });
        }
        return items;
    }

    public void Apply(ManifestItem item, string payloadRoot, AppliedManifest applied, MigrationRequest request)
    {
        var source = Path.Combine(payloadRoot, item.PayloadPath!);
        var target = PathTokens.Expand(item.Meta.GetValueOrDefault("tokenizedPath") ?? item.Meta["originalPath"]);

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
}
