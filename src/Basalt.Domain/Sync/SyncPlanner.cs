using Basalt.Domain.Notes;

namespace Basalt.Domain.Sync;

/// <summary>
/// Three-way, per-file planner (spec 06 §5). Pure: it works on manifests only, so text/binario is decided by extension
/// and the materialiser re-checks the bytes (ADR-0017).
/// </summary>
public static class SyncPlanner
{
    public static SyncPlan Plan(SyncBase baseline, Manifest local, Manifest remote, SyncPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(policy);

        var actions = new List<SyncAction>();
        var expected = new Dictionary<VaultPath, ManifestEntry>();
        var handled = new HashSet<VaultPath>();

        ResolveFileFolderConflicts(local, remote, actions, handled);


        var paths = local.Entries.Select(entry => entry.Path)
            .Concat(remote.Entries.Select(entry => entry.Path))
            .Concat(baseline.Manifest.Entries.Select(entry => entry.Path))
            .Distinct()
            .Where(path => !handled.Contains(path))
            .Order();

        foreach (var path in paths)
        {
            PlanPath(path, baseline.Manifest.Get(path), local.Get(path), remote.Get(path), policy, actions, expected);
        }

        actions.Sort(static (left, right) =>
        {
            var byPath = left.Path.CompareTo(right.Path);
            return byPath != 0 ? byPath : Rank(left).CompareTo(Rank(right));
        });
        return new SyncPlan(actions, Manifest.Create(expected.Values).Value);
    }

    private static void PlanPath(
        VaultPath path,
        ManifestEntry? b,
        ManifestEntry? l,
        ManifestEntry? r,
        SyncPolicy policy,
        List<SyncAction> actions,
        Dictionary<VaultPath, ManifestEntry> expected)
    {
        if (b is { } ancestor)
        {
            if (l is { } local && r is { } remote)
            {
                if (local.Sha == ancestor.Sha && remote.Sha == ancestor.Sha)
                {
                    expected[path] = local;
                }
                else if (remote.Sha == ancestor.Sha)
                {
                    Upload(actions, expected, local);
                }
                else if (local.Sha == ancestor.Sha)
                {
                    Download(actions, expected, remote);
                }
                else if (local.Sha == remote.Sha)
                {
                    actions.Add(new SyncAction.NoOp(path));
                    expected[path] = local;
                }
                else
                {
                    ResolveConflict(path, ancestor, local, remote, policy, actions, expected);
                }
            }
            else if (l is null && r is { } deletedRemote && deletedRemote.Sha == ancestor.Sha)
            {
                actions.Add(new SyncAction.DeleteRemote(path));
            }
            else if (l is { } deletedLocal && r is null && deletedLocal.Sha == ancestor.Sha)
            {
                actions.Add(new SyncAction.DeleteLocal(path, deletedLocal.Sha));
            }
            else if (l is null && r is null)
            {
                actions.Add(new SyncAction.NoOp(path));
            }
            else if (l is { } modifiedLocal && r is null)
            {
                if (policy.DeleteVsModify == DeleteConflictPolicy.KeepModified)
                {
                    Upload(actions, expected, modifiedLocal);
                }
                else
                {
                    actions.Add(new SyncAction.DeleteLocal(path, modifiedLocal.Sha));
                }
            }
            else if (l is null && r is { } modifiedRemote)
            {
                if (policy.DeleteVsModify == DeleteConflictPolicy.KeepModified)
                {
                    Download(actions, expected, modifiedRemote);
                }
                else
                {
                    actions.Add(new SyncAction.DeleteRemote(path));
                }
            }
        }
        else if (l is { } addedLocal && r is null)
        {
            Upload(actions, expected, addedLocal);
        }
        else if (l is null && r is { } addedRemote)
        {
            Download(actions, expected, addedRemote);
        }
        else if (l is { } added && r is { } same && added.Sha == same.Sha)
        {
            actions.Add(new SyncAction.NoOp(path));
            expected[path] = added;
        }
        else if (l is { } addedLocal2 && r is { } addedRemote2)
        {
            ResolveConflict(path, null, addedLocal2, addedRemote2, policy, actions, expected);
        }
    }

    private static void ResolveConflict(
        VaultPath path,
        ManifestEntry? b,
        ManifestEntry local,
        ManifestEntry remote,
        SyncPolicy policy,
        List<SyncAction> actions,
        Dictionary<VaultPath, ManifestEntry> expected)
    {
        var text = TextBinaryClassifier.IsTextPath(path);
        if (text && policy.TextConflicts is TextConflictPolicy.AutoMergeOrConflictCopy or TextConflictPolicy.AutoMergeOrMarkers)
        {
            actions.Add(new SyncAction.MergeText(path, b?.Sha, local.Sha, remote.Sha));
            expected[path] = remote;
            return;
        }

        var preference = text
            ? policy.TextConflicts switch
            {
                TextConflictPolicy.PreferLocal => (int?)1,
                TextConflictPolicy.PreferRemote => 2,
                _ => null,
            }
            : policy.BinaryConflicts switch
            {
                BinaryConflictPolicy.PreferLocal => 1,
                BinaryConflictPolicy.PreferRemote => 2,
                _ => null,
            };

        if (preference == 1)
        {
            Upload(actions, expected, local);
        }
        else if (preference == 2)
        {
            Download(actions, expected, remote);
        }
        else
        {
            Download(actions, expected, remote);
            actions.Add(new SyncAction.CreateConflictCopy(path, ConflictSide.Local, local.Sha));
        }
    }

    private static void Upload(List<SyncAction> actions, Dictionary<VaultPath, ManifestEntry> expected, ManifestEntry entry)
    {
        actions.Add(new SyncAction.Upload(entry.Path, entry.Sha));
        expected[entry.Path] = entry;
    }

    private static void Download(List<SyncAction> actions, Dictionary<VaultPath, ManifestEntry> expected, ManifestEntry entry)
    {
        actions.Add(new SyncAction.Download(entry.Path, entry.Sha));
        expected[entry.Path] = entry;
    }

    private static void ResolveFileFolderConflicts(
        Manifest local,
        Manifest remote,
        List<SyncAction> actions,
        HashSet<VaultPath> handled)
    {
        var localFolders = Folders(local);
        var remoteFolders = Folders(remote);

        foreach (var entry in local.Entries.Where(entry => remoteFolders.Contains(entry.Path) && !remote.Contains(entry.Path)))
        {
            if (!handled.Add(entry.Path))
            {
                continue;
            }

            actions.Add(new SyncAction.CreateConflictCopy(entry.Path, ConflictSide.Local, entry.Sha));
            actions.Add(new SyncAction.DeleteLocal(entry.Path, entry.Sha));
        }

        foreach (var entry in remote.Entries.Where(entry => localFolders.Contains(entry.Path) && !local.Contains(entry.Path)))
        {
            if (!handled.Add(entry.Path))
            {
                continue;
            }

            actions.Add(new SyncAction.CreateConflictCopy(entry.Path, ConflictSide.Remote, entry.Sha));
            actions.Add(new SyncAction.DeleteRemote(entry.Path));
        }
    }

    private static HashSet<VaultPath> Folders(Manifest manifest)
    {
        var folders = new HashSet<VaultPath>();
        foreach (var entry in manifest.Entries)
        {
            var parent = entry.Path.Parent;
            while (!parent.IsRoot && folders.Add(parent))
            {
                parent = parent.Parent;
            }
        }

        return folders;
    }

    private static int Rank(SyncAction action) => action switch
    {
        SyncAction.NoOp => 0,
        SyncAction.CreateConflictCopy => 1,
        SyncAction.DeleteLocal => 2,
        SyncAction.DeleteRemote => 3,
        SyncAction.Download => 4,
        SyncAction.Upload => 5,
        SyncAction.MergeText => 6,
        _ => 7,
    };
}
