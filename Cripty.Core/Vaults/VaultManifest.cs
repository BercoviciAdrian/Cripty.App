namespace Cripty.Core.Vaults;

public sealed class VaultManifest
{
    private static readonly StringComparer NameComparer =
        StringComparer.OrdinalIgnoreCase;

    private readonly List<FolderDescriptor> _folders;
    private readonly List<TagDescriptor> _tags;
    private readonly List<EntryDescriptor> _entries;

    private readonly IReadOnlyList<FolderDescriptor> _foldersView;
    private readonly IReadOnlyList<TagDescriptor> _tagsView;
    private readonly IReadOnlyList<EntryDescriptor> _entriesView;

    public int SchemaVersion { get; }
    public Guid VaultId { get; }
    public long Generation { get; private set; }

    public IReadOnlyList<FolderDescriptor> Folders =>
        _foldersView;

    public IReadOnlyList<TagDescriptor> Tags =>
        _tagsView;

    public IReadOnlyList<EntryDescriptor> Entries =>
        _entriesView;

    public VaultSortPreferences SortPreferences { get; }

    public VaultManifest(
        int schemaVersion,
        Guid vaultId,
        long generation,
        IEnumerable<FolderDescriptor> folders,
        IEnumerable<TagDescriptor> tags,
        IEnumerable<EntryDescriptor> entries,
        VaultSortPreferences? sortPreferences = null)
    {
        SchemaVersion = schemaVersion;
        VaultId = vaultId;
        Generation = generation;

        _folders = [.. folders];
        _tags = [.. tags];
        _entries = [.. entries];

        _foldersView = _folders.AsReadOnly();
        _tagsView = _tags.AsReadOnly();
        _entriesView = _entries.AsReadOnly();

        SortPreferences =
            sortPreferences ?? new VaultSortPreferences();
    }

    // Entries

    public void AddEntryDescriptor(EntryDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (_entries.Any(
                entry => entry.EntryId == descriptor.EntryId))
        {
            throw new InvalidOperationException(
                $"Entry '{descriptor.EntryId}' already exists.");
        }

        //throw exception if FolderId is not null and invalid
        EnsureFolderExists(descriptor.FolderId);

        HashSet<Guid> tagIds = [];

        //ensure no duplicate tags and each tag is valid

        foreach (Guid tagId in descriptor.TagIds)
        {
            if (!tagIds.Add(tagId))
            {
                throw new InvalidOperationException(
                    $"Entry '{descriptor.EntryId}' contains " +
                    $"duplicate tag '{tagId}'.");
            }

            EnsureTagExists(tagId);
        }

        _entries.Add(descriptor);
    }

    public void RemoveEntryDescriptor(Guid entryId)
    {
        EntryDescriptor descriptor = GetEntry(entryId);
        _entries.Remove(descriptor);
    }

    public void RenameEntry(Guid entryId, string newName)
    {
        GetEntry(entryId).Rename(RequireName(newName));
    }

    public void SetEntryTimelineDate(
        Guid entryId,
        DateOnly? timelineDateOverride)
    {
        GetEntry(entryId).SetTimelineDateOverride(
            timelineDateOverride);
    }

    public void MoveEntry(
        Guid entryId,
        Guid? destinationFolderId)
    {
        EnsureFolderExists(destinationFolderId);

        GetEntry(entryId).MoveTo(destinationFolderId);
    }

    public void AddTagToEntry(Guid entryId, Guid tagId)
    {
        EnsureTagExists(tagId);

        EntryDescriptor entry = GetEntry(entryId);

        if (entry.TagIds.Contains(tagId))
        {
            throw new InvalidOperationException(
                $"Entry '{entryId}' already has tag '{tagId}'.");
        }

        entry.AddTag(tagId);
    }

    public void RemoveTagFromEntry(
        Guid entryId,
        Guid tagId)
    {
        EntryDescriptor entry = GetEntry(entryId);

        if (!entry.TagIds.Contains(tagId))
        {
            throw new InvalidOperationException(
                $"Entry '{entryId}' does not have tag '{tagId}'.");
        }

        entry.RemoveTag(tagId);
    }

    // Folders

    public FolderDescriptor CreateFolder(
        string name,
        Guid? parentFolderId)
    {
        string validName = RequireName(name);

        EnsureFolderExists(parentFolderId);
        EnsureUniqueFolderName(validName, parentFolderId);

        FolderDescriptor folder = new(
            Guid.NewGuid(),
            validName,
            parentFolderId);

        _folders.Add(folder);

        return folder;
    }

    public void DeleteFolder(Guid folderId)
    {
        FolderDescriptor folder = GetFolder(folderId);
        Guid? destinationId = folder.ParentFolderId;

        List<FolderDescriptor> childFolders =
            _folders
                .Where(item =>
                    item.ParentFolderId == folderId)
                .ToList();

        // Reject before changing anything if promoting a child
        // would create a duplicate sibling-folder name.
        foreach (FolderDescriptor child in childFolders)
        {
            EnsureUniqueFolderName(
                child.Name,
                destinationId,
                childFolders
                    .Select(item => item.FolderId)
                    .Append(folderId));
        }

        foreach (EntryDescriptor entry in
                 _entries.Where(
                     item => item.FolderId == folderId))
        {
            entry.MoveTo(destinationId);
        }

        foreach (FolderDescriptor child in childFolders)
        {
            child.MoveTo(destinationId);
        }

        _folders.Remove(folder);
        SortPreferences.RemoveFolder(folderId);
    }

    // Browser sort preferences

    public void SetAllEntriesSortMode(
        EntrySortMode sortMode)
    {
        SortPreferences.SetAllEntriesSortMode(sortMode);
    }

    public void SetRootSortMode(
        EntrySortMode sortMode)
    {
        SortPreferences.SetRootSortMode(sortMode);
    }

    public void SetFolderSortMode(
        Guid folderId,
        EntrySortMode sortMode)
    {
        EnsureFolderExists(folderId);
        SortPreferences.SetFolderSortMode(
            folderId,
            sortMode);
    }

    public EntrySortMode GetFolderSortMode(
        Guid folderId)
    {
        EnsureFolderExists(folderId);
        return SortPreferences.GetFolderSortMode(folderId);
    }

    public void RenameFolder(
        Guid folderId,
        string newName)
    {
        FolderDescriptor folder = GetFolder(folderId);
        string validName = RequireName(newName);

        EnsureUniqueFolderName(
            validName,
            folder.ParentFolderId,
            [folderId]);

        folder.Rename(validName);
    }

    public void MoveFolder(
        Guid folderId,
        Guid? newParentFolderId)
    {
        FolderDescriptor folder = GetFolder(folderId);

        EnsureFolderExists(newParentFolderId);

        if (newParentFolderId == folderId)
        {
            throw new InvalidOperationException(
                "A folder cannot be its own parent.");
        }

        Guid? ancestorId = newParentFolderId;
        HashSet<Guid> visited = [];

        while (ancestorId is Guid currentId)
        {
            if (currentId == folderId)
            {
                throw new InvalidOperationException(
                    "Moving this folder would create a cycle.");
            }

            if (!visited.Add(currentId))
            {
                throw new InvalidOperationException(
                    "The existing folder hierarchy " +
                    "contains a cycle.");
            }

            ancestorId =
                GetFolder(currentId).ParentFolderId;
        }

        EnsureUniqueFolderName(
            folder.Name,
            newParentFolderId,
            [folderId]);

        folder.MoveTo(newParentFolderId);
    }

    // Tags

    public TagDescriptor CreateTag(
        string name,
        string? color = null)
    {
        string validName = RequireName(name);

        EnsureUniqueTagName(validName);

        TagDescriptor tag = new(
            Guid.NewGuid(),
            validName,
            color);

        _tags.Add(tag);

        return tag;
    }

    public void DeleteTag(Guid tagId)
    {
        TagDescriptor tag = GetTag(tagId);

        foreach (EntryDescriptor entry in
                 _entries.Where(
                     item => item.TagIds.Contains(tagId)))
        {
            entry.RemoveTag(tagId);
        }

        _tags.Remove(tag);
    }

    public void RenameTag(Guid tagId, string newName)
    {
        TagDescriptor tag = GetTag(tagId);
        string validName = RequireName(newName);

        EnsureUniqueTagName(validName, tagId);

        tag.Rename(validName);
    }

    public void SetTagColor(Guid tagId, string? color)
    {
        GetTag(tagId).SetColor(color);
    }

    // Save synchronization — intentionally deferred

    /*public void RecordSuccessfulSave(
        long newGeneration)
    {
        long expectedGeneration =
            checked(Generation + 1);

        if (newGeneration != expectedGeneration)
        {
            throw new InvalidOperationException(
                $"Expected manifest generation " +
                $"'{expectedGeneration}', but received " +
                $"'{newGeneration}'.");
        }

        Generation = newGeneration;
    }*/

    public void RecordEntryCommit(
        Guid entryId,
        long committedRevision,
        DateTimeOffset modifiedUtc)
    {
        EntryDescriptor entry =
            GetEntry(entryId);

        long expectedRevision =
            checked(entry.Revision + 1);

        if (committedRevision != expectedRevision)
        {
            throw new InvalidOperationException(
                $"Expected entry revision '{expectedRevision}', " +
                $"but received '{committedRevision}'.");
        }

        if (modifiedUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "The modification time must use the UTC offset.",
                nameof(modifiedUtc));
        }

        if (modifiedUtc < entry.ModifiedUtc)
        {
            throw new ArgumentException(
                "The modification time cannot move backwards.",
                nameof(modifiedUtc));
        }

        entry.RecordCommit(
            committedRevision,
            modifiedUtc);
    }

    // Lookup and validation helpers

    private EntryDescriptor GetEntry(Guid entryId)
    {
        return _entries.FirstOrDefault(
                   entry => entry.EntryId == entryId)
               ?? throw new KeyNotFoundException(
                   $"Entry '{entryId}' does not exist.");
    }

    private FolderDescriptor GetFolder(Guid folderId)
    {
        return _folders.FirstOrDefault(
                   folder => folder.FolderId == folderId)
               ?? throw new KeyNotFoundException(
                   $"Folder '{folderId}' does not exist.");
    }

    private TagDescriptor GetTag(Guid tagId)
    {
        return _tags.FirstOrDefault(
                   tag => tag.TagId == tagId)
               ?? throw new KeyNotFoundException(
                   $"Tag '{tagId}' does not exist.");
    }

    private void EnsureFolderExists(Guid? folderId)
    {
        if (folderId is Guid id)
        {
            GetFolder(id);
        }
    }

    private void EnsureTagExists(Guid tagId)
    {
        GetTag(tagId);
    }

    private void EnsureUniqueFolderName(
        string name,
        Guid? parentFolderId,
        IEnumerable<Guid>? ignoredFolderIds = null)
    {
        HashSet<Guid> ignored =
            ignoredFolderIds?.ToHashSet() ?? [];

        bool duplicateExists = _folders.Any(folder =>
            folder.ParentFolderId == parentFolderId
            && !ignored.Contains(folder.FolderId)
            && NameComparer.Equals(folder.Name, name));

        if (duplicateExists)
        {
            throw new InvalidOperationException(
                $"A folder named '{name}' already exists " +
                "in that location.");
        }
    }

    private void EnsureUniqueTagName(
        string name,
        Guid? ignoredTagId = null)
    {
        bool duplicateExists = _tags.Any(tag =>
            tag.TagId != ignoredTagId
            && NameComparer.Equals(tag.Name, name));

        if (duplicateExists)
        {
            throw new InvalidOperationException(
                $"A tag named '{name}' already exists.");
        }
    }

    private static string RequireName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name;
    }
}
