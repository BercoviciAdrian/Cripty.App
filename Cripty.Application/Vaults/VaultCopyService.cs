using System.Security.Cryptography;
using Cripty.Core.Entries;
using Cripty.Core.Vaults;

namespace Cripty.Application.Vaults;

public sealed record VaultCopyResult(
    int EntryCount,
    int BlobCount,
    int CreatedFolderCount,
    int CreatedTagCount);

public sealed class VaultCopyService
{
    private static readonly StringComparer NameComparer =
        StringComparer.OrdinalIgnoreCase;

    public async Task<VaultCopyResult> CopyAsync(
        VaultSession source,
        string destinationVaultDirectoryPath,
        string destinationPassword,
        IEnumerable<Guid> selectedEntryIds,
        IEnumerable<Guid> selectedFolderIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destinationPassword);
        ArgumentNullException.ThrowIfNull(selectedEntryIds);
        ArgumentNullException.ThrowIfNull(selectedFolderIds);

        EnsureSourceManifestIsClean(source);

        if (string.IsNullOrWhiteSpace(
                destinationVaultDirectoryPath))
        {
            throw new ArgumentException(
                "The destination vault path cannot be empty.",
                nameof(destinationVaultDirectoryPath));
        }

        string destinationPath =
            Path.GetFullPath(
                destinationVaultDirectoryPath);

        if (PathsEqual(
                source.VaultDirectoryPath,
                destinationPath))
        {
            throw new InvalidOperationException(
                "The destination must be a different vault.");
        }

        await using VaultSession destination =
            await VaultSession.OpenAsync(
                    destinationPath,
                    destinationPassword,
                    cancellationToken)
                .ConfigureAwait(false);

        if (destination.VaultId == source.VaultId)
        {
            throw new InvalidOperationException(
                "The destination has the same vault identity as " +
                "the source. Choose a distinct vault.");
        }

        return await CopyIntoOpenVaultAsync(
                source,
                destination,
                selectedEntryIds,
                selectedFolderIds,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<VaultCopyResult>
        CopyIntoOpenVaultAsync(
            VaultSession source,
            VaultSession destination,
            IEnumerable<Guid> selectedEntryIds,
            IEnumerable<Guid> selectedFolderIds,
            CancellationToken cancellationToken)
    {
        EnsureSourceManifestIsClean(source);

        if (destination.HasUnsavedChanges ||
            destination.RequiresSaveRetry)
        {
            throw new InvalidOperationException(
                "The destination vault must be clean before copying.");
        }

        FolderDescriptor[] sourceFolders =
            [.. source.Folders];

        TagDescriptor[] sourceTags =
            [.. source.Tags];

        EntryDescriptor[] sourceEntries =
            [.. source.Entries];

        HashSet<Guid> pendingDeletionIds =
            source.EntriesPendingDeletion.ToHashSet();

        Dictionary<Guid, FolderDescriptor> foldersById =
            sourceFolders.ToDictionary(
                folder => folder.FolderId);

        Dictionary<Guid, TagDescriptor> tagsById =
            sourceTags.ToDictionary(
                tag => tag.TagId);

        Dictionary<Guid, EntryDescriptor> entriesById =
            sourceEntries.ToDictionary(
                entry => entry.EntryId);

        HashSet<Guid> explicitlySelectedEntries =
            selectedEntryIds.ToHashSet();

        HashSet<Guid> selectedFolders =
            selectedFolderIds.ToHashSet();

        ValidateSelections(
            explicitlySelectedEntries,
            selectedFolders,
            entriesById,
            foldersById,
            pendingDeletionIds);

        HashSet<Guid> selectedSubtreeFolderIds =
            ExpandSelectedFolderSubtrees(
                selectedFolders,
                sourceFolders);

        HashSet<Guid> entryIdsToCopy =
            new(explicitlySelectedEntries);

        entryIdsToCopy.UnionWith(
            sourceEntries
                .Where(entry =>
                    entry.FolderId is Guid folderId &&
                    selectedSubtreeFolderIds.Contains(folderId) &&
                    !pendingDeletionIds.Contains(entry.EntryId))
                .Select(entry => entry.EntryId));

        if (entryIdsToCopy.Count == 0)
        {
            throw new InvalidOperationException(
                "The selection does not contain any entries to copy.");
        }

        EntryDescriptor[] entriesToCopy =
            entryIdsToCopy
                .Select(entryId => entriesById[entryId])
                .OrderBy(entry =>
                    BuildFolderPath(
                        entry.FolderId,
                        foldersById),
                    NameComparer)
                .ThenBy(entry => entry.Name, NameComparer)
                .ThenBy(entry => entry.EntryId)
                .ToArray();

        HashSet<Guid> requiredFolderIds =
            GetRequiredFolderIds(
                entriesToCopy,
                foldersById);

        Dictionary<Guid, Guid> destinationFolderIds = [];
        int createdFolderCount = 0;

        foreach (FolderDescriptor sourceFolder in
                 OrderFoldersParentFirst(
                     requiredFolderIds,
                     foldersById))
        {
            Guid? destinationParentId =
                sourceFolder.ParentFolderId is Guid sourceParentId
                    ? destinationFolderIds[sourceParentId]
                    : null;

            FolderDescriptor? existing =
                destination.Folders.FirstOrDefault(folder =>
                    folder.ParentFolderId == destinationParentId &&
                    NameComparer.Equals(
                        folder.Name,
                        sourceFolder.Name));

            FolderDescriptor destinationFolder =
                existing ??
                destination.CreateFolder(
                    sourceFolder.Name,
                    destinationParentId);

            if (existing is null)
            {
                createdFolderCount++;
            }

            destinationFolderIds.Add(
                sourceFolder.FolderId,
                destinationFolder.FolderId);
        }

        HashSet<Guid> requiredTagIds =
            entriesToCopy
                .SelectMany(entry => entry.TagIds)
                .ToHashSet();

        Dictionary<Guid, Guid> destinationTagIds = [];
        int createdTagCount = 0;

        foreach (TagDescriptor sourceTag in
                 sourceTags
                     .Where(tag =>
                         requiredTagIds.Contains(tag.TagId))
                     .OrderBy(tag => tag.Name, NameComparer))
        {
            TagDescriptor? existing =
                destination.Tags.FirstOrDefault(tag =>
                    NameComparer.Equals(
                        tag.Name,
                        sourceTag.Name));

            TagDescriptor destinationTag =
                existing ??
                destination.CreateTag(
                    sourceTag.Name,
                    sourceTag.Color);

            if (existing is null)
            {
                createdTagCount++;
            }

            destinationTagIds.Add(
                sourceTag.TagId,
                destinationTag.TagId);
        }

        HashSet<string> destinationEntryNames =
            destination.Entries
                .Select(entry => entry.Name)
                .ToHashSet(NameComparer);

        int blobCount = 0;

        foreach (EntryDescriptor sourceDescriptor in entriesToCopy)
        {
            cancellationToken.ThrowIfCancellationRequested();

            VaultEntry sourceEntry =
                await source.GetEntryAsync(
                        sourceDescriptor.EntryId,
                        cancellationToken)
                    .ConfigureAwait(false);

            Guid? destinationFolderId =
                sourceDescriptor.FolderId is Guid sourceFolderId
                    ? destinationFolderIds[sourceFolderId]
                    : null;

            string destinationName =
                MakeUniqueEntryName(
                    sourceDescriptor.Name,
                    destinationEntryNames);

            List<EntryField> destinationFields = [];
            List<BlobTransfer> blobTransfers = [];

            foreach (EntryField sourceField in sourceEntry.Fields)
            {
                Guid destinationFieldId = Guid.NewGuid();

                switch (sourceField.Value)
                {
                    case TextFieldValue text:
                        destinationFields.Add(
                            new EntryField(
                                destinationFieldId,
                                sourceField.Name,
                                new TextFieldValue(text.Text)));
                        break;

                    case BlobFieldValue blob:
                    {
                        Guid destinationBlobId = Guid.NewGuid();

                        destinationFields.Add(
                            new EntryField(
                                destinationFieldId,
                                sourceField.Name,
                                new BlobFieldValue(
                                    destinationBlobId,
                                    blob.FileName,
                                    blob.ContentType,
                                    blob.Length)));

                        blobTransfers.Add(
                            new BlobTransfer(
                                blob.BlobId,
                                destinationBlobId,
                                blob.Length));
                        break;
                    }

                    default:
                        throw new NotSupportedException(
                            $"Field '{sourceField.FieldId}' has an " +
                            "unsupported value type.");
                }
            }

            VaultEntry destinationEntry =
                destination.CreateCopiedEntry(
                    destinationName,
                    destinationFolderId,
                    sourceDescriptor.TagIds.Select(tagId =>
                        destinationTagIds[tagId]),
                    destinationFields,
                    sourceDescriptor.CreatedUtc,
                    sourceDescriptor.ModifiedUtc,
                    sourceDescriptor.TimelineDateOverride);

            foreach (BlobTransfer transfer in blobTransfers)
            {
                using SensitiveBuffer plaintext =
                    await source.GetBlobAsync(
                            sourceDescriptor.EntryId,
                            transfer.SourceBlobId,
                            transfer.Length,
                            cancellationToken)
                        .ConfigureAwait(false);

                destination.ReplaceEntryWithBlob(
                    destinationEntry,
                    transfer.DestinationBlobId,
                    plaintext.ReadOnlyMemory);

                blobCount++;
            }
        }

        // Every tag, folder, entry, and blob has been staged. One save
        // publishes the complete batch in a single manifest generation.
        await destination.SaveAsync(
                cancellationToken)
            .ConfigureAwait(false);

        return new VaultCopyResult(
            entriesToCopy.Length,
            blobCount,
            createdFolderCount,
            createdTagCount);
    }

    private static void EnsureSourceManifestIsClean(
        VaultSession source)
    {
        if (source.IsManifestDirty)
        {
            throw new InvalidOperationException(
                "Save the source vault's manifest changes before " +
                "copying entries to another vault.");
        }
    }

    private static void ValidateSelections(
        IReadOnlyCollection<Guid> selectedEntryIds,
        IReadOnlyCollection<Guid> selectedFolderIds,
        IReadOnlyDictionary<Guid, EntryDescriptor> entriesById,
        IReadOnlyDictionary<Guid, FolderDescriptor> foldersById,
        IReadOnlySet<Guid> pendingDeletionIds)
    {
        foreach (Guid entryId in selectedEntryIds)
        {
            if (!entriesById.ContainsKey(entryId))
            {
                throw new KeyNotFoundException(
                    $"Selected entry '{entryId}' does not exist.");
            }

            if (pendingDeletionIds.Contains(entryId))
            {
                throw new InvalidOperationException(
                    $"Entry '{entryId}' is marked for deletion and " +
                    "cannot be copied.");
            }
        }

        foreach (Guid folderId in selectedFolderIds)
        {
            if (!foldersById.ContainsKey(folderId))
            {
                throw new KeyNotFoundException(
                    $"Selected folder '{folderId}' does not exist.");
            }
        }
    }

    private static HashSet<Guid> ExpandSelectedFolderSubtrees(
        IReadOnlySet<Guid> selectedFolderIds,
        IReadOnlyCollection<FolderDescriptor> folders)
    {
        HashSet<Guid> expanded =
            new(selectedFolderIds);

        bool added;

        do
        {
            added = false;

            foreach (FolderDescriptor folder in folders)
            {
                if (folder.ParentFolderId is Guid parentId &&
                    expanded.Contains(parentId))
                {
                    added |= expanded.Add(folder.FolderId);
                }
            }
        }
        while (added);

        return expanded;
    }

    private static HashSet<Guid> GetRequiredFolderIds(
        IEnumerable<EntryDescriptor> entries,
        IReadOnlyDictionary<Guid, FolderDescriptor> foldersById)
    {
        HashSet<Guid> required = [];

        foreach (EntryDescriptor entry in entries)
        {
            Guid? folderId = entry.FolderId;

            while (folderId is Guid currentId &&
                   required.Add(currentId))
            {
                folderId =
                    foldersById[currentId].ParentFolderId;
            }
        }

        return required;
    }

    private static IEnumerable<FolderDescriptor>
        OrderFoldersParentFirst(
            IEnumerable<Guid> folderIds,
            IReadOnlyDictionary<Guid, FolderDescriptor> foldersById)
    {
        return folderIds
            .Select(folderId => foldersById[folderId])
            .OrderBy(folder =>
                GetFolderDepth(folder, foldersById))
            .ThenBy(folder =>
                BuildFolderPath(
                    folder.FolderId,
                    foldersById),
                NameComparer);
    }

    private static int GetFolderDepth(
        FolderDescriptor folder,
        IReadOnlyDictionary<Guid, FolderDescriptor> foldersById)
    {
        int depth = 0;
        Guid? parentId = folder.ParentFolderId;

        while (parentId is Guid currentId)
        {
            depth++;
            parentId = foldersById[currentId].ParentFolderId;
        }

        return depth;
    }

    private static string BuildFolderPath(
        Guid? folderId,
        IReadOnlyDictionary<Guid, FolderDescriptor> foldersById)
    {
        if (folderId is null)
        {
            return string.Empty;
        }

        Stack<string> names = [];
        Guid? currentId = folderId;

        while (currentId is Guid id)
        {
            FolderDescriptor folder = foldersById[id];
            names.Push(folder.Name);
            currentId = folder.ParentFolderId;
        }

        return string.Join('/', names);
    }

    private static string MakeUniqueEntryName(
        string requestedName,
        ISet<string> existingNames)
    {
        if (existingNames.Add(requestedName))
        {
            return requestedName;
        }

        while (true)
        {
            string candidate =
                requestedName + " " +
                Convert.ToBase64String(
                    RandomNumberGenerator.GetBytes(6));

            if (existingNames.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static bool PathsEqual(
        string first,
        string second)
    {
        string normalizedFirst =
            Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(first));

        string normalizedSecond =
            Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(second));

        return string.Equals(
            normalizedFirst,
            normalizedSecond,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    private sealed record BlobTransfer(
        Guid SourceBlobId,
        Guid DestinationBlobId,
        long Length);
}
