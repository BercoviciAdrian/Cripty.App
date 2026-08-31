using Cripty.Core.Vaults;
using Cripty.Storage.Formats;

namespace Cripty.Storage.Validation;

internal static class VaultManifestValidator
{
    private static readonly StringComparer NameComparer =
        StringComparer.OrdinalIgnoreCase;

    public static void Validate(VaultManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        ValidateSchemaVersion(manifest.SchemaVersion);

        if (manifest.VaultId == Guid.Empty)
        {
            throw new InvalidDataException(
                "The manifest has an empty vault ID.");
        }

        if (manifest.Generation < 0)
        {
            throw new InvalidDataException(
                "The manifest has a negative generation.");
        }

        Dictionary<Guid, FolderDescriptor> foldersById =
            ValidateFolders(manifest.Folders);

        ValidateSortPreferences(
            manifest.SortPreferences,
            foldersById);

        Dictionary<Guid, TagDescriptor> tagsById =
            ValidateTags(manifest.Tags);

        ValidateEntries(
            manifest.Entries,
            foldersById,
            tagsById);
    }

    private static void ValidateSortPreferences(
        VaultSortPreferences preferences,
        IReadOnlyDictionary<Guid, FolderDescriptor>
            foldersById)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        ValidateSortMode(
            preferences.AllEntriesSortMode,
            "all-entries");

        ValidateSortMode(
            preferences.RootSortMode,
            "root");

        foreach (KeyValuePair<Guid, EntrySortMode> preference in
                 preferences.FolderSortModes)
        {
            if (preference.Key == Guid.Empty)
            {
                throw new InvalidDataException(
                    "A folder sort preference has an empty " +
                    "folder ID.");
            }

            if (!foldersById.ContainsKey(preference.Key))
            {
                throw new InvalidDataException(
                    $"A sort preference refers to missing folder " +
                    $"'{preference.Key}'.");
            }

            ValidateSortMode(
                preference.Value,
                $"folder '{preference.Key}'");
        }
    }

    private static void ValidateSortMode(
        EntrySortMode sortMode,
        string target)
    {
        if (!Enum.IsDefined(sortMode))
        {
            throw new InvalidDataException(
                $"The {target} sort preference " +
                $"'{sortMode}' is not supported.");
        }
    }

    public static void ValidateSchemaVersion(
        int schemaVersion)
    {
        if (schemaVersion <
                StorageSchemaVersions.OldestSupportedManifest ||
            schemaVersion >
                StorageSchemaVersions.CurrentManifest)
        {
            throw new NotSupportedException(
                $"Manifest schema version " +
                $"'{schemaVersion}' is not supported.");
        }
    }

    private static Dictionary<Guid, FolderDescriptor>
        ValidateFolders(
            IReadOnlyList<FolderDescriptor> folders)
    {
        Dictionary<Guid, FolderDescriptor> foldersById = [];

        Dictionary<Guid, HashSet<string>>
            namesByParentId = [];

        foreach (FolderDescriptor? folder in folders)
        {
            if (folder is null)
            {
                throw new InvalidDataException(
                    "The manifest contains a null folder.");
            }

            if (folder.FolderId == Guid.Empty)
            {
                throw new InvalidDataException(
                    "The manifest contains a folder " +
                    "with an empty ID.");
            }

            if (!foldersById.TryAdd(
                    folder.FolderId,
                    folder))
            {
                throw new InvalidDataException(
                    $"The manifest contains duplicate folder ID " +
                    $"'{folder.FolderId}'.");
            }

            if (string.IsNullOrWhiteSpace(folder.Name))
            {
                throw new InvalidDataException(
                    $"Folder '{folder.FolderId}' has no name.");
            }

            if (folder.ParentFolderId == folder.FolderId)
            {
                throw new InvalidDataException(
                    $"Folder '{folder.FolderId}' is its own parent.");
            }

            Guid parentKey =
                folder.ParentFolderId ?? Guid.Empty;

            if (!namesByParentId.TryGetValue(
                    parentKey,
                    out HashSet<string>? siblingNames))
            {
                siblingNames = new HashSet<string>(
                    NameComparer);

                namesByParentId.Add(
                    parentKey,
                    siblingNames);
            }

            if (!siblingNames.Add(folder.Name))
            {
                throw new InvalidDataException(
                    $"More than one folder named '{folder.Name}' " +
                    "exists in the same location.");
            }
        }

        foreach (FolderDescriptor folder in foldersById.Values)
        {
            if (folder.ParentFolderId is Guid parentId &&
                !foldersById.ContainsKey(parentId))
            {
                throw new InvalidDataException(
                    $"Folder '{folder.FolderId}' refers to missing " +
                    $"parent folder '{parentId}'.");
            }
        }

        ValidateFolderHierarchy(foldersById);

        return foldersById;
    }

    private static void ValidateFolderHierarchy(
        IReadOnlyDictionary<Guid, FolderDescriptor>
            foldersById)
    {
        HashSet<Guid> validatedFolderIds = [];

        foreach (Guid startingFolderId in foldersById.Keys)
        {
            if (validatedFolderIds.Contains(startingFolderId))
            {
                continue;
            }

            HashSet<Guid> currentPath = [];
            Guid? currentFolderId = startingFolderId;

            while (currentFolderId is Guid folderId &&
                   !validatedFolderIds.Contains(folderId))
            {
                if (!currentPath.Add(folderId))
                {
                    throw new InvalidDataException(
                        "The manifest folder hierarchy " +
                        "contains a cycle.");
                }

                currentFolderId =
                    foldersById[folderId].ParentFolderId;
            }

            validatedFolderIds.UnionWith(currentPath);
        }
    }

    private static Dictionary<Guid, TagDescriptor>
        ValidateTags(
            IReadOnlyList<TagDescriptor> tags)
    {
        Dictionary<Guid, TagDescriptor> tagsById = [];
        HashSet<string> tagNames = new(NameComparer);

        foreach (TagDescriptor? tag in tags)
        {
            if (tag is null)
            {
                throw new InvalidDataException(
                    "The manifest contains a null tag.");
            }

            if (tag.TagId == Guid.Empty)
            {
                throw new InvalidDataException(
                    "The manifest contains a tag with an empty ID.");
            }

            if (!tagsById.TryAdd(tag.TagId, tag))
            {
                throw new InvalidDataException(
                    $"The manifest contains duplicate tag ID " +
                    $"'{tag.TagId}'.");
            }

            if (string.IsNullOrWhiteSpace(tag.Name))
            {
                throw new InvalidDataException(
                    $"Tag '{tag.TagId}' has no name.");
            }

            if (!tagNames.Add(tag.Name))
            {
                throw new InvalidDataException(
                    $"More than one tag named '{tag.Name}' exists.");
            }
        }

        return tagsById;
    }

    private static void ValidateEntries(
        IReadOnlyList<EntryDescriptor> entries,
        IReadOnlyDictionary<Guid, FolderDescriptor>
            foldersById,
        IReadOnlyDictionary<Guid, TagDescriptor>
            tagsById)
    {
        HashSet<Guid> entryIds = [];

        foreach (EntryDescriptor? entry in entries)
        {
            if (entry is null)
            {
                throw new InvalidDataException(
                    "The manifest contains a null entry descriptor.");
            }

            if (entry.EntryId == Guid.Empty)
            {
                throw new InvalidDataException(
                    "The manifest contains an entry descriptor " +
                    "with an empty ID.");
            }

            if (!entryIds.Add(entry.EntryId))
            {
                throw new InvalidDataException(
                    $"The manifest contains duplicate entry ID " +
                    $"'{entry.EntryId}'.");
            }

            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                throw new InvalidDataException(
                    $"Entry descriptor '{entry.EntryId}' has no name.");
            }

            if (entry.FolderId is Guid folderId &&
                !foldersById.ContainsKey(folderId))
            {
                throw new InvalidDataException(
                    $"Entry '{entry.EntryId}' refers to missing " +
                    $"folder '{folderId}'.");
            }

            ValidateEntryTags(entry, tagsById);
            ValidateEntryVersionAndTimestamps(entry);
        }
    }

    private static void ValidateEntryTags(
        EntryDescriptor entry,
        IReadOnlyDictionary<Guid, TagDescriptor> tagsById)
    {
        HashSet<Guid> assignedTagIds = [];

        foreach (Guid tagId in entry.TagIds)
        {
            if (tagId == Guid.Empty)
            {
                throw new InvalidDataException(
                    $"Entry '{entry.EntryId}' contains an empty " +
                    "tag ID.");
            }

            if (!assignedTagIds.Add(tagId))
            {
                throw new InvalidDataException(
                    $"Entry '{entry.EntryId}' contains duplicate " +
                    $"tag ID '{tagId}'.");
            }

            if (!tagsById.ContainsKey(tagId))
            {
                throw new InvalidDataException(
                    $"Entry '{entry.EntryId}' refers to missing " +
                    $"tag '{tagId}'.");
            }
        }
    }

    private static void ValidateEntryVersionAndTimestamps(
        EntryDescriptor entry)
    {
        if (entry.Revision < 0)
        {
            throw new InvalidDataException(
                $"Entry '{entry.EntryId}' has a negative revision.");
        }

        if (entry.CreatedUtc == default)
        {
            throw new InvalidDataException(
                $"Entry '{entry.EntryId}' has no creation time.");
        }

        if (entry.ModifiedUtc == default)
        {
            throw new InvalidDataException(
                $"Entry '{entry.EntryId}' has no modification time.");
        }

        if (entry.CreatedUtc.Offset != TimeSpan.Zero ||
            entry.ModifiedUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                $"Entry '{entry.EntryId}' timestamps must use " +
                "the UTC offset.");
        }

        if (entry.ModifiedUtc < entry.CreatedUtc)
        {
            throw new InvalidDataException(
                $"Entry '{entry.EntryId}' was modified before " +
                "it was created.");
        }
    }
}
