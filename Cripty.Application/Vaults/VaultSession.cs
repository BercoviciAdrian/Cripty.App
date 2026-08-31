using System.Security.Cryptography;
using Cripty.Core.Entries;
using Cripty.Core.Vaults;
using Cripty.Cryptography.Keys;
using Cripty.Storage.Codecs;
using Cripty.Storage.FileSystem;
using Cripty.Storage.Formats;

namespace Cripty.Application.Vaults;

public sealed class VaultSession : IAsyncDisposable
{
    private readonly VaultFileCodec _vaultFileCodec;
    private readonly EntryFileCodec _entryFileCodec;
    private readonly BlobFileCodec _blobFileCodec;

    private readonly VaultFileStore _vaultFileStore;
    private readonly EntryFileStore _entryFileStore;
    private readonly BlobFileStore _blobFileStore;

    private readonly byte[] _vaultRootKey;

    private readonly SemaphoreSlim _operationGate =
        new(1, 1);

    private readonly Dictionary<Guid, PendingEntryChange>
        _pendingEntryChanges = [];

    private readonly Dictionary<Guid, PendingBlobWrite>
        _pendingBlobWrites = [];

    // Reversible deletions which have not yet been saved.
    private readonly HashSet<Guid>
        _entriesPendingDeletion = [];

    // Entry metadata changes are persisted in the manifest rather
    // than in the encrypted entry file. Track affected entries so
    // the UI can distinguish them from unchanged entries.
    private readonly HashSet<Guid>
        _entriesWithPendingMetadataChanges = [];

    // Files belonging to entries already removed from the
    // persisted manifest, but whose physical deletion failed.
    private readonly HashSet<Guid>
        _orphanedEntryFilesPendingCleanup = [];

    // Encrypted blob files which are no longer referenced by a
    // committed entry. Failed deletion remains retryable.
    private readonly HashSet<Guid>
        _orphanedBlobFilesPendingCleanup = [];

    private VaultFile _vaultFile;
    private VaultIndex _index;

    private bool _manifestDirty;
    private bool _disposed;

    private VaultSession(
        string vaultDirectoryPath,
        VaultFile vaultFile,
        VaultManifest manifest,
        byte[] vaultRootKey,
        VaultFileCodec vaultFileCodec,
        EntryFileCodec entryFileCodec,
        BlobFileCodec blobFileCodec,
        VaultFileStore vaultFileStore,
        EntryFileStore entryFileStore,
        BlobFileStore blobFileStore)
    {
        VaultDirectoryPath = vaultDirectoryPath;

        _vaultFile = vaultFile;
        Manifest = manifest;
        _index = VaultIndex.Build(manifest);

        _vaultRootKey = vaultRootKey;

        _vaultFileCodec = vaultFileCodec;
        _entryFileCodec = entryFileCodec;
        _blobFileCodec = blobFileCodec;

        _vaultFileStore = vaultFileStore;
        _entryFileStore = entryFileStore;
        _blobFileStore = blobFileStore;
    }

    public string VaultDirectoryPath { get; }

    private VaultManifest Manifest { get; set; }

    public VaultIndex Index =>
        ReadState(() => _index);

    public bool IsManifestDirty =>
        ReadState(IsManifestDirtyCore);

    public bool HasPendingEntryChanges =>
        ReadState(() =>
            _pendingEntryChanges.Count > 0);

    public bool HasPendingEntryContentChanges(
        Guid entryId)
    {
        return ReadState(() =>
        {
            ValidateEntryId(entryId);
            GetEntryDescriptor(entryId);

            return _pendingEntryChanges.TryGetValue(
                       entryId,
                       out PendingEntryChange? pendingChange) &&
                   pendingChange.Kind ==
                   EntryChangeKind.Modified;
        });
    }

    public bool HasPendingEntryDeletions =>
        ReadState(() =>
            _entriesPendingDeletion.Count > 0);

    public bool HasPendingEntryFileDeletions =>
        ReadState(() =>
            _orphanedEntryFilesPendingCleanup.Count > 0);

    public bool HasPendingBlobFileDeletions =>
        ReadState(() =>
            _orphanedBlobFilesPendingCleanup.Count > 0);

    public bool RequiresSaveRetry =>
        ReadState(RequiresSaveRetryCore);

    public bool HasUnsavedChanges =>
        ReadState(HasUnsavedUserChangesCore);

    public int ManifestSchemaVersion =>
        ReadState(() =>
            Manifest.SchemaVersion);

    public Guid VaultId =>
        ReadState(() =>
            Manifest.VaultId);

    public long ManifestGeneration =>
        ReadState(() =>
            Manifest.Generation);

    public Argon2idParameters PasswordKdfParameters =>
    ReadState(() =>
    {
        Argon2idParameters parameters =
            _vaultFile.PasswordKeySlot.KdfParameters;

        return new Argon2idParameters
        {
            Version = parameters.Version,
            MemorySizeKiB = parameters.MemorySizeKiB,
            Iterations = parameters.Iterations,
            DegreeOfParallelism = parameters.DegreeOfParallelism
        };
    });

    public IReadOnlyList<FolderDescriptor> Folders =>
        ReadState(() =>
            (IReadOnlyList<FolderDescriptor>)
            Manifest.Folders.ToArray());

    public IReadOnlyList<TagDescriptor> Tags =>
        ReadState(() =>
            (IReadOnlyList<TagDescriptor>)
            Manifest.Tags.ToArray());

    public IReadOnlyList<EntryDescriptor> Entries =>
        ReadState(() =>
            (IReadOnlyList<EntryDescriptor>)
            Manifest.Entries.ToArray());

    public IReadOnlyCollection<Guid>
        EntriesPendingDeletion =>
        ReadState(() =>
            (IReadOnlyCollection<Guid>)
            _entriesPendingDeletion.ToArray());

    public static async Task<VaultSession> CreateAsync(
        string vaultDirectoryPath,
        string password,
        Argon2idParameters? kdfParameters = null,
        CancellationToken cancellationToken = default)
    {
        string normalizedPath =
            NormalizeVaultDirectoryPath(
                vaultDirectoryPath);

        ValidatePassword(password);

        string vaultFilePath =
            Path.Combine(
                normalizedPath,
                VaultFileStore.VaultFileName);

        if (File.Exists(vaultFilePath))
        {
            throw new InvalidOperationException(
                $"A vault already exists at '{normalizedPath}'.");
        }

        VaultFileCodec vaultFileCodec = new();
        EntryFileCodec entryFileCodec = new();
        BlobFileCodec blobFileCodec = new();

        VaultFileStore vaultFileStore = new();
        EntryFileStore entryFileStore = new();
        BlobFileStore blobFileStore = new();

        Guid vaultId = Guid.NewGuid();

        VaultManifest manifest = new(
            StorageSchemaVersions.CurrentManifest,
            vaultId,
            generation: 0,
            folders: [],
            tags: [],
            entries: []);

        byte[] vaultRootKey =
            new byte[VaultRootKeyGenerator.KeySize];

        try
        {
            VaultRootKeyGenerator.Generate(
                vaultRootKey);

            VaultFile vaultFile =
                vaultFileCodec.Create(
                    manifest,
                    vaultRootKey,
                    password,
                    kdfParameters);

            await vaultFileStore.WriteAsync(
                    normalizedPath,
                    vaultFile,
                    cancellationToken)
                .ConfigureAwait(false);

            return new VaultSession(
                normalizedPath,
                vaultFile,
                manifest,
                vaultRootKey,
                vaultFileCodec,
                entryFileCodec,
                blobFileCodec,
                vaultFileStore,
                entryFileStore,
                blobFileStore);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(
                vaultRootKey);

            throw;
        }
    }

    public static async Task<VaultSession> OpenAsync(
        string vaultDirectoryPath,
        string password,
        CancellationToken cancellationToken = default)
    {
        string normalizedPath =
            NormalizeVaultDirectoryPath(
                vaultDirectoryPath);

        ValidatePassword(password);

        VaultFileCodec vaultFileCodec = new();
        EntryFileCodec entryFileCodec = new();
        BlobFileCodec blobFileCodec = new();

        VaultFileStore vaultFileStore = new();
        EntryFileStore entryFileStore = new();
        BlobFileStore blobFileStore = new();

        VaultFile vaultFile =
            await vaultFileStore.ReadAsync(
                    normalizedPath,
                    cancellationToken)
                .ConfigureAwait(false);

        byte[] vaultRootKey =
            new byte[VaultRootKeyGenerator.KeySize];

        try
        {
            VaultManifest manifest =
                vaultFileCodec.Open(
                    vaultFile,
                    password,
                    vaultRootKey);

            if (manifest.SchemaVersion <
                StorageSchemaVersions.CurrentManifest)
            {
                VaultManifest upgradedManifest = new(
                    StorageSchemaVersions.CurrentManifest,
                    manifest.VaultId,
                    checked(manifest.Generation + 1),
                    manifest.Folders,
                    manifest.Tags,
                    manifest.Entries);

                VaultFile upgradedVaultFile =
                    vaultFileCodec.UpdateManifest(
                        vaultFile,
                        upgradedManifest,
                        vaultRootKey);

                await vaultFileStore.WriteAsync(
                        normalizedPath,
                        upgradedVaultFile,
                        cancellationToken)
                    .ConfigureAwait(false);

                manifest = upgradedManifest;
                vaultFile = upgradedVaultFile;
            }

            if (vaultFile.ManifestGeneration is null)
            {
                vaultFile = new VaultFile
                {
                    FormatVersion = vaultFile.FormatVersion,
                    VaultId = vaultFile.VaultId,
                    ManifestGeneration = manifest.Generation,
                    PasswordKeySlot = vaultFile.PasswordKeySlot,
                    ManifestEnvelope = vaultFile.ManifestEnvelope
                };

                try
                {
                    await vaultFileStore.WriteAsync(
                            normalizedPath,
                            vaultFile,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // The hint is optional. A read-only legacy vault should
                    // still be unlockable even when it cannot be upgraded.
                }
                catch (UnauthorizedAccessException)
                {
                    // Keep the authenticated in-memory generation hint.
                }
            }

            return new VaultSession(
                normalizedPath,
                vaultFile,
                manifest,
                vaultRootKey,
                vaultFileCodec,
                entryFileCodec,
                blobFileCodec,
                vaultFileStore,
                entryFileStore,
                blobFileStore);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(
                vaultRootKey);

            throw;
        }
    }

    public Task ChangePasswordAsync(
        string newPassword,
        Argon2idParameters? newKdfParameters = null,
        CancellationToken cancellationToken = default)
    {
        return ChangePasswordAsync(
            newPassword,
            newKdfParameters,
            progress: null,
            cancellationToken);
    }

    public async Task ChangePasswordAsync(
        string newPassword,
        Argon2idParameters? newKdfParameters,
        IProgress<VaultPasswordChangeProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            EnsureNotDisposed();
            ValidatePassword(newPassword);

            if (HasPendingSaveWorkCore())
            {
                throw new InvalidOperationException(
                    "Save, discard, or complete cleanup of all " +
                    "pending changes before changing the password.");
            }

            VaultKeyRotationService rotationService = new(
                _vaultFileCodec,
                _entryFileCodec,
                _blobFileCodec,
                _vaultFileStore,
                _entryFileStore,
                _blobFileStore);

            using VaultKeyRotationResult rotation =
                await rotationService.RotateAsync(
                    VaultDirectoryPath,
                    Manifest,
                    _vaultRootKey,
                    newPassword,
                    newKdfParameters,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);

            rotation.CopyRootKeyTo(_vaultRootKey);
            Manifest = rotation.Manifest;
            _vaultFile = rotation.VaultFile;
            RebuildIndex();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    // Entry content operations

    public VaultEntry CreateEntry(
        string name,
        Guid? folderId = null,
        IEnumerable<Guid>? tagIds = null,
        IEnumerable<EntryField>? fields = null)
    {
        return MutateState(() =>
        {
            DateTimeOffset createdUtc =
                DateTimeOffset.UtcNow;

            return CreateEntryCore(
                name,
                folderId,
                tagIds,
                fields,
                createdUtc,
                createdUtc,
                preserveTimestampsOnFirstSave: false);
        });
    }

    internal VaultEntry CreateCopiedEntry(
        string name,
        Guid? folderId,
        IEnumerable<Guid>? tagIds,
        IEnumerable<EntryField>? fields,
        DateTimeOffset createdUtc,
        DateTimeOffset modifiedUtc,
        DateOnly? timelineDateOverride)
    {
        return MutateState(() =>
            CreateEntryCore(
                name,
                folderId,
                tagIds,
                fields,
                createdUtc,
                modifiedUtc,
                preserveTimestampsOnFirstSave: true,
                timelineDateOverride:
                    timelineDateOverride));
    }

    public async Task<VaultEntry> GetEntryAsync(
        Guid entryId,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            EnsureNotDisposed();
            ValidateEntryId(entryId);

            EntryDescriptor descriptor =
                GetEntryDescriptor(entryId);

            if (_pendingEntryChanges.TryGetValue(
                    entryId,
                    out PendingEntryChange? pendingChange))
            {
                return pendingChange.WorkingEntry;
            }

            return await ReadPersistedEntryCoreAsync(
                    entryId,
                    descriptor,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<VaultEntry> GetPersistedEntryAsync(
        Guid entryId,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            EnsureNotDisposed();
            ValidateEntryId(entryId);

            EntryDescriptor descriptor =
                GetEntryDescriptor(entryId);

            if (_pendingEntryChanges.TryGetValue(
                    entryId,
                    out PendingEntryChange? pendingChange) &&
                pendingChange.Kind == EntryChangeKind.New)
            {
                throw new InvalidOperationException(
                    $"Entry '{entryId}' has not been saved yet.");
            }

            return await ReadPersistedEntryCoreAsync(
                    entryId,
                    descriptor,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void ReplaceEntry(
        VaultEntry modifiedEntry)
    {
        MutateState(() =>
        {
            ReplaceEntryCore(modifiedEntry);
            PruneUnreferencedPendingBlobs(
                modifiedEntry.EntryId,
                modifiedEntry);
        });
    }

    public void ReplaceEntryWithBlob(
        VaultEntry modifiedEntry,
        Guid blobId,
        ReadOnlyMemory<byte> plaintext)
    {
        MutateState(() =>
        {
            ArgumentNullException.ThrowIfNull(modifiedEntry);
            ValidateEntryId(modifiedEntry.EntryId);

            if (blobId == Guid.Empty)
            {
                throw new ArgumentException(
                    "The blob ID cannot be empty.",
                    nameof(blobId));
            }

            BlobFieldValue[] matchingValues =
                modifiedEntry.Fields
                    .Select(field => field.Value)
                    .OfType<BlobFieldValue>()
                    .Where(value => value.BlobId == blobId)
                    .ToArray();

            if (matchingValues.Length != 1)
            {
                throw new ArgumentException(
                    "The modified entry must contain exactly one " +
                    "field referencing the staged blob.",
                    nameof(modifiedEntry));
            }

            if (matchingValues[0].Length != plaintext.Length)
            {
                throw new ArgumentException(
                    "The blob reference length does not match the " +
                    "staged plaintext length.",
                    nameof(plaintext));
            }

            if (_pendingBlobWrites.ContainsKey(blobId))
            {
                throw new InvalidOperationException(
                    $"Blob '{blobId}' is already staged.");
            }

            byte[] ownedPlaintext = plaintext.ToArray();

            try
            {
                ReplaceEntryCore(modifiedEntry);

                _pendingBlobWrites.Add(
                    blobId,
                    new PendingBlobWrite(
                        modifiedEntry.EntryId,
                        blobId,
                        ownedPlaintext));

                PruneUnreferencedPendingBlobs(
                    modifiedEntry.EntryId,
                    modifiedEntry);
            }
            catch
            {
                CryptographicOperations.ZeroMemory(
                    ownedPlaintext);

                throw;
            }
        });
    }

    public async Task<SensitiveBuffer> GetBlobAsync(
        Guid entryId,
        Guid blobId,
        long expectedLength,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            EnsureNotDisposed();
            ValidateEntryId(entryId);

            if (blobId == Guid.Empty)
            {
                throw new ArgumentException(
                    "The blob ID cannot be empty.",
                    nameof(blobId));
            }

            if (expectedLength < 0 ||
                expectedLength > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(expectedLength));
            }

            GetEntryDescriptor(entryId);

            VaultEntry referencingEntry;

            if (_pendingEntryChanges.TryGetValue(
                    entryId,
                    out PendingEntryChange? entryChange))
            {
                referencingEntry = entryChange.WorkingEntry;
            }
            else
            {
                EntryDescriptor descriptor =
                    GetEntryDescriptor(entryId);

                referencingEntry =
                    await ReadPersistedEntryCoreAsync(
                            entryId,
                            descriptor,
                            cancellationToken)
                        .ConfigureAwait(false);
            }

            if (!GetBlobIds(referencingEntry).Contains(blobId))
            {
                throw new InvalidOperationException(
                    $"Entry '{entryId}' does not reference blob " +
                    $"'{blobId}'.");
            }

            byte[] plaintext;

            if (_pendingBlobWrites.TryGetValue(
                    blobId,
                    out PendingBlobWrite? pendingWrite) &&
                !pendingWrite.BlobFileWritten)
            {
                if (pendingWrite.EntryId != entryId)
                {
                    throw new InvalidDataException(
                        "The staged blob belongs to a different entry.");
                }

                plaintext = pendingWrite.CopyPlaintext();
            }
            else
            {
                BlobFile blobFile =
                    await _blobFileStore.ReadAsync(
                            VaultDirectoryPath,
                            blobId,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (blobFile.VaultId != Manifest.VaultId)
                {
                    throw new InvalidDataException(
                        $"Blob '{blobId}' belongs to a different vault.");
                }

                plaintext =
                    _blobFileCodec.Open(
                        blobFile,
                        _vaultRootKey);
            }

            if (plaintext.Length != expectedLength)
            {
                CryptographicOperations.ZeroMemory(plaintext);

                throw new InvalidDataException(
                    $"Blob '{blobId}' has an unexpected length.");
            }

            return new SensitiveBuffer(plaintext);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void DiscardEntryChanges(
        Guid entryId)
    {
        MutateState(() =>
        {
            ValidateEntryId(entryId);

            if (!_pendingEntryChanges.TryGetValue(
                    entryId,
                    out PendingEntryChange? pendingChange))
            {
                throw new InvalidOperationException(
                    $"Entry '{entryId}' has no unsaved " +
                    "content changes.");
            }

            if (pendingChange.Kind ==
                EntryChangeKind.New)
            {
                Manifest.RemoveEntryDescriptor(
                    entryId);

                _entriesPendingDeletion.Remove(
                    entryId);

                _entriesWithPendingMetadataChanges.Remove(
                    entryId);

                RecordManifestChange(
                    rebuildIndex: true);
            }

            // Modified entries fall back to their persisted
            // encrypted file. Pending deletion remains staged.
            _pendingEntryChanges.Remove(
                entryId);

            RemovePendingBlobsForEntry(entryId);
        });
    }

    // Staged entry deletion

    public void MarkEntryForDeletion(
        Guid entryId)
    {
        MutateState(() =>
        {
            ValidateEntryId(entryId);
            GetEntryDescriptor(entryId);

            _entriesPendingDeletion.Add(
                entryId);
        });
    }

    public void UndoEntryDeletion(
        Guid entryId)
    {
        MutateState(() =>
        {
            ValidateEntryId(entryId);
            GetEntryDescriptor(entryId);

            if (!_entriesPendingDeletion.Remove(
                    entryId))
            {
                throw new InvalidOperationException(
                    $"Entry '{entryId}' is not marked " +
                    "for deletion.");
            }
        });
    }

    public EntrySessionState GetEntrySessionState(
        Guid entryId)
    {
        return ReadState(() =>
        {
            ValidateEntryId(entryId);
            GetEntryDescriptor(entryId);

            EntryChangeKind changeKind =
                _pendingEntryChanges.TryGetValue(
                    entryId,
                    out PendingEntryChange? pendingChange)
                    ? pendingChange.Kind
                    : _entriesWithPendingMetadataChanges.Contains(
                        entryId)
                        ? EntryChangeKind.Modified
                    : EntryChangeKind.None;

            return new EntrySessionState(
                changeKind,
                _entriesPendingDeletion.Contains(
                    entryId));
        });
    }

    // Folder operations

    public FolderDescriptor CreateFolder(
        string name,
        Guid? parentFolderId = null)
    {
        return MutateState(() =>
        {
            FolderDescriptor folder =
                Manifest.CreateFolder(
                    name,
                    parentFolderId);

            RecordManifestChange(
                rebuildIndex: false);

            return folder;
        });
    }

    public void RenameFolder(
        Guid folderId,
        string newName)
    {
        MutateState(() =>
        {
            Manifest.RenameFolder(
                folderId,
                newName);

            RecordManifestChange(
                rebuildIndex: false);
        });
    }

    public void MoveFolder(
        Guid folderId,
        Guid? newParentFolderId)
    {
        MutateState(() =>
        {
            Manifest.MoveFolder(
                folderId,
                newParentFolderId);

            // The index maps entries to their direct folder.
            // A folder-parent change does not affect that mapping.
            RecordManifestChange(
                rebuildIndex: false);
        });
    }

    public void DeleteFolder(
        Guid folderId)
    {
        MutateState(() =>
        {
            Manifest.DeleteFolder(
                folderId);

            // Entries in the deleted folder are moved
            // to its parent.
            RecordManifestChange(
                rebuildIndex: true);
        });
    }

    // Tag operations

    public TagDescriptor CreateTag(
        string name,
        string? color = null)
    {
        return MutateState(() =>
        {
            TagDescriptor tag =
                Manifest.CreateTag(
                    name,
                    color);

            RecordManifestChange(
                rebuildIndex: false);

            return tag;
        });
    }

    public void RenameTag(
        Guid tagId,
        string newName)
    {
        MutateState(() =>
        {
            Manifest.RenameTag(
                tagId,
                newName);

            RecordManifestChange(
                rebuildIndex: false);
        });
    }

    public void SetTagColor(
        Guid tagId,
        string? color)
    {
        MutateState(() =>
        {
            Manifest.SetTagColor(
                tagId,
                color);

            RecordManifestChange(
                rebuildIndex: false);
        });
    }

    public void DeleteTag(
        Guid tagId)
    {
        MutateState(() =>
        {
            Manifest.DeleteTag(
                tagId);

            // DeleteTag removes the tag from every entry.
            RecordManifestChange(
                rebuildIndex: true);
        });
    }

    // Entry metadata operations

    public void RenameEntry(
        Guid entryId,
        string newName)
    {
        MutateState(() =>
        {
            EnsureEntryIsNotPendingDeletion(
                entryId);

            Manifest.RenameEntry(
                entryId,
                newName);

            _entriesWithPendingMetadataChanges.Add(
                entryId);

            RecordManifestChange(
                rebuildIndex: false);
        });
    }

    public void SetEntryTimelineDate(
        Guid entryId,
        DateOnly? timelineDateOverride)
    {
        MutateState(() =>
        {
            EnsureEntryIsNotPendingDeletion(
                entryId);

            EntryDescriptor descriptor =
                GetEntryDescriptor(entryId);

            if (descriptor.TimelineDateOverride ==
                timelineDateOverride)
            {
                return;
            }

            Manifest.SetEntryTimelineDate(
                entryId,
                timelineDateOverride);

            _entriesWithPendingMetadataChanges.Add(
                entryId);

            RecordManifestChange(
                rebuildIndex: false);
        });
    }

    public void MoveEntry(
        Guid entryId,
        Guid? destinationFolderId)
    {
        MutateState(() =>
        {
            EnsureEntryIsNotPendingDeletion(
                entryId);

            Manifest.MoveEntry(
                entryId,
                destinationFolderId);

            _entriesWithPendingMetadataChanges.Add(
                entryId);

            RecordManifestChange(
                rebuildIndex: true);
        });
    }

    public void AddTagToEntry(
        Guid entryId,
        Guid tagId)
    {
        MutateState(() =>
        {
            EnsureEntryIsNotPendingDeletion(
                entryId);

            Manifest.AddTagToEntry(
                entryId,
                tagId);

            _entriesWithPendingMetadataChanges.Add(
                entryId);

            RecordManifestChange(
                rebuildIndex: true);
        });
    }

    public void RemoveTagFromEntry(
        Guid entryId,
        Guid tagId)
    {
        MutateState(() =>
        {
            EnsureEntryIsNotPendingDeletion(
                entryId);

            Manifest.RemoveTagFromEntry(
                entryId,
                tagId);

            _entriesWithPendingMetadataChanges.Add(
                entryId);

            RecordManifestChange(
                rebuildIndex: true);
        });
    }

    // Persistence

    public async Task SaveAsync(
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            EnsureNotDisposed();

            if (!HasPendingSaveWorkCore())
            {
                return;
            }

            await SavePendingEntryFilesAsync(
                    cancellationToken)
                .ConfigureAwait(false);

            if (RequiresManifestWriteCore())
            {
                await SaveManifestAsync(
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            DeletePendingEntryFiles(
                cancellationToken);

            DeletePendingBlobFiles(
                cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task SavePendingEntryFilesAsync(
        CancellationToken cancellationToken)
    {
        await SavePendingBlobFilesAsync(
                cancellationToken)
            .ConfigureAwait(false);

        PendingEntryChange[] changes =
            _pendingEntryChanges
                .Values
                .ToArray();

        foreach (PendingEntryChange change in changes)
        {
            Guid entryId =
                change.WorkingEntry.EntryId;

            if (change.EntryFileWritten ||
                _entriesPendingDeletion.Contains(entryId))
            {
                continue;
            }

            await SavePendingEntryFileAsync(
                    change,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task SavePendingBlobFilesAsync(
        CancellationToken cancellationToken)
    {
        PendingBlobWrite[] writes =
            _pendingBlobWrites.Values.ToArray();

        foreach (PendingBlobWrite write in writes)
        {
            if (write.BlobFileWritten ||
                _entriesPendingDeletion.Contains(
                    write.EntryId))
            {
                continue;
            }

            if (!_pendingEntryChanges.TryGetValue(
                    write.EntryId,
                    out PendingEntryChange? entryChange) ||
                !GetBlobIds(entryChange.WorkingEntry)
                    .Contains(write.BlobId))
            {
                throw new InvalidOperationException(
                    $"Pending blob '{write.BlobId}' is not " +
                    "referenced by its working entry.");
            }

            BlobFile blobFile =
                _blobFileCodec.Create(
                    Manifest.VaultId,
                    write.BlobId,
                    write.Plaintext,
                    _vaultRootKey);

            await _blobFileStore.WriteAsync(
                    VaultDirectoryPath,
                    blobFile,
                    cancellationToken)
                .ConfigureAwait(false);

            write.RecordBlobFileWrite();
        }
    }

    private async Task SavePendingEntryFileAsync(
        PendingEntryChange pendingChange,
        CancellationToken cancellationToken)
    {
        VaultEntry entry =
            pendingChange.WorkingEntry;

        if (pendingChange.Kind == EntryChangeKind.Modified &&
            !pendingChange.ObsoleteBlobIdsRecorded)
        {
            EntryDescriptor persistedDescriptor =
                GetEntryDescriptor(entry.EntryId);

            VaultEntry persistedEntry =
                await ReadPersistedEntryCoreAsync(
                        entry.EntryId,
                        persistedDescriptor,
                        cancellationToken)
                    .ConfigureAwait(false);

            pendingChange.RecordObsoleteBlobIds(
                GetBlobIds(persistedEntry)
                    .Except(GetBlobIds(entry)));
        }

        EntryDescriptor descriptor =
            GetEntryDescriptor(
                entry.EntryId);

        if (entry.Revision != descriptor.Revision)
        {
            throw new InvalidOperationException(
                $"Entry '{entry.EntryId}' has revision " +
                $"'{entry.Revision}', but its descriptor has " +
                $"revision '{descriptor.Revision}'.");
        }

        long committedRevision =
            checked(entry.Revision + 1);

        DateTimeOffset modifiedUtc =
            pendingChange.FirstCommitModifiedUtc ??
            DateTimeOffset.UtcNow;

        // Protect against the system clock moving backwards.
        if (modifiedUtc < descriptor.ModifiedUtc)
        {
            modifiedUtc = descriptor.ModifiedUtc;
        }

        VaultEntry committedEntry = new(
            entry.SchemaVersion,
            entry.EntryId,
            committedRevision,
            entry.Fields);

        EntryFile entryFile =
            _entryFileCodec.Create(
                Manifest.VaultId,
                committedEntry,
                _vaultRootKey);

        await _entryFileStore.WriteAsync(
                VaultDirectoryPath,
                entryFile,
                cancellationToken)
            .ConfigureAwait(false);

        // Only advance the live descriptor after the complete
        // encrypted entry file was replaced successfully.
        Manifest.RecordEntryCommit(
            entry.EntryId,
            committedRevision,
            modifiedUtc);

        pendingChange.RecordEntryFileWrite(
            committedEntry);

        // The new revision now needs to be recorded
        // in the persisted manifest.
        _manifestDirty = true;
    }

    private async Task SaveManifestAsync(
        CancellationToken cancellationToken)
    {
        foreach (KeyValuePair<Guid, PendingEntryChange> pair
                 in _pendingEntryChanges)
        {
            if (_entriesPendingDeletion.Contains(pair.Key))
            {
                continue;
            }

            if (!pair.Value.EntryFileWritten)
            {
                throw new InvalidOperationException(
                    $"Entry '{pair.Key}' has unsaved contents " +
                    "which were not written before the manifest save.");
            }
        }

        Guid[] deletedEntryIds =
            _entriesPendingDeletion.ToArray();

        HashSet<Guid> deletedEntryBlobIds = [];

        foreach (Guid entryId in deletedEntryIds)
        {
            EntryDescriptor descriptor =
                GetEntryDescriptor(entryId);

            if (descriptor.Revision == 0)
            {
                continue;
            }

            VaultEntry persistedEntry =
                await ReadPersistedEntryCoreAsync(
                        entryId,
                        descriptor,
                        cancellationToken)
                    .ConfigureAwait(false);

            deletedEntryBlobIds.UnionWith(
                GetBlobIds(persistedEntry));
        }

        HashSet<Guid> deletedEntryIdSet =
            deletedEntryIds.ToHashSet();

        EntryDescriptor[] entriesToPersist =
            Manifest.Entries
                .Where(entry =>
                    !deletedEntryIdSet.Contains(
                        entry.EntryId))
                .ToArray();

        long newGeneration =
            checked(Manifest.Generation + 1);

        VaultManifest manifestToPersist = new(
            StorageSchemaVersions.CurrentManifest,
            Manifest.VaultId,
            newGeneration,
            Manifest.Folders,
            Manifest.Tags,
            entriesToPersist);

        VaultFile updatedVaultFile =
            _vaultFileCodec.UpdateManifest(
                _vaultFile,
                manifestToPersist,
                _vaultRootKey);

        await _vaultFileStore.WriteAsync(
                VaultDirectoryPath,
                updatedVaultFile,
                cancellationToken)
            .ConfigureAwait(false);

        // The replacement manifest is now committed. From here,

        foreach (Guid entryId in deletedEntryIds)
        {
            EntryDescriptor descriptor =
                GetEntryDescriptor(entryId);

            // Revision zero means the new entry never had
            // an encrypted entry file successfully committed.
            if (descriptor.Revision > 0)
            {
                _orphanedEntryFilesPendingCleanup.Add(
                    entryId);
            }
        }

        _orphanedBlobFilesPendingCleanup.UnionWith(
            deletedEntryBlobIds);

        foreach (PendingEntryChange change in
                 _pendingEntryChanges.Values)
        {
            if (!_entriesPendingDeletion.Contains(
                    change.WorkingEntry.EntryId))
            {
                _orphanedBlobFilesPendingCleanup.UnionWith(
                    change.ObsoleteBlobIds);
            }
        }

        // update the live session to match the persisted snapshot.

        Manifest = manifestToPersist;
        _vaultFile = updatedVaultFile;
        _manifestDirty = false;

        _pendingEntryChanges.Clear();
        ClearPendingBlobWrites();
        _entriesPendingDeletion.Clear();
        _entriesWithPendingMetadataChanges.Clear();

        RebuildIndex();
    }

    private void DeletePendingEntryFiles(
        CancellationToken cancellationToken)
    {
        foreach (Guid entryId in
                 _orphanedEntryFilesPendingCleanup.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            _entryFileStore.Delete(
                VaultDirectoryPath,
                entryId);

            // Remove only after physical deletion succeeds.
            _orphanedEntryFilesPendingCleanup.Remove(
                entryId);
        }
    }

    private void DeletePendingBlobFiles(
        CancellationToken cancellationToken)
    {
        foreach (Guid blobId in
                 _orphanedBlobFilesPendingCleanup.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            _blobFileStore.Delete(
                VaultDirectoryPath,
                blobId);

            _orphanedBlobFilesPendingCleanup.Remove(
                blobId);
        }
    }

    // State and synchronization helpers

    private VaultEntry CreateEntryCore(
        string name,
        Guid? folderId,
        IEnumerable<Guid>? tagIds,
        IEnumerable<EntryField>? fields,
        DateTimeOffset createdUtc,
        DateTimeOffset modifiedUtc,
        bool preserveTimestampsOnFirstSave,
        DateOnly? timelineDateOverride = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException(
                "The entry name cannot be empty.",
                nameof(name));
        }

        if (createdUtc == default ||
            modifiedUtc == default ||
            createdUtc.Offset != TimeSpan.Zero ||
            modifiedUtc.Offset != TimeSpan.Zero ||
            modifiedUtc < createdUtc)
        {
            throw new ArgumentException(
                "Entry timestamps must be valid UTC values, with " +
                "the modification time on or after creation.");
        }

        Guid entryId = Guid.NewGuid();

        List<Guid> assignedTagIds =
            tagIds?.ToList() ?? [];

        List<EntryField> entryFields =
            fields?.ToList() ?? [];

        EntryDescriptor descriptor = new(
            entryId,
            name,
            folderId,
            assignedTagIds,
            revision: 0,
            createdUtc,
            modifiedUtc,
            timelineDateOverride);

        VaultEntry entry = new(
            StorageSchemaVersions.CurrentEntry,
            entryId,
            revision: 0,
            entryFields);

        // Validates the folder, tags, duplicate ID,
        // and duplicate tag assignments.
        Manifest.AddEntryDescriptor(
            descriptor);

        _pendingEntryChanges.Add(
            entryId,
            new PendingEntryChange(
                entry,
                EntryChangeKind.New,
                preserveTimestampsOnFirstSave
                    ? modifiedUtc
                    : null));

        RecordManifestChange(
            rebuildIndex: true);

        return entry;
    }

    private void ReplaceEntryCore(
        VaultEntry modifiedEntry)
    {
        ArgumentNullException.ThrowIfNull(modifiedEntry);

        EntryDescriptor descriptor =
            GetEntryDescriptor(modifiedEntry.EntryId);

        EnsureEntryIsNotPendingDeletion(
            modifiedEntry.EntryId);

        if (modifiedEntry.SchemaVersion !=
            StorageSchemaVersions.CurrentEntry)
        {
            throw new ArgumentException(
                "The modified entry has an unsupported " +
                "schema version.",
                nameof(modifiedEntry));
        }

        if (modifiedEntry.Revision !=
            descriptor.Revision)
        {
            throw new ArgumentException(
                "The entry revision must match the current " +
                "descriptor revision. VaultSession increments " +
                "it during SaveAsync.",
                nameof(modifiedEntry));
        }

        if (_pendingEntryChanges.TryGetValue(
                modifiedEntry.EntryId,
                out PendingEntryChange? pendingChange))
        {
            pendingChange.ReplaceWorkingEntry(modifiedEntry);
        }
        else
        {
            _pendingEntryChanges.Add(
                modifiedEntry.EntryId,
                new PendingEntryChange(
                    modifiedEntry,
                    EntryChangeKind.Modified));
        }
    }

    private void PruneUnreferencedPendingBlobs(
        Guid entryId,
        VaultEntry workingEntry)
    {
        HashSet<Guid> referencedBlobIds =
            GetBlobIds(workingEntry);

        foreach (PendingBlobWrite pendingWrite in
                 _pendingBlobWrites.Values
                     .Where(write =>
                         write.EntryId == entryId &&
                         !referencedBlobIds.Contains(
                             write.BlobId))
                     .ToArray())
        {
            _pendingBlobWrites.Remove(
                pendingWrite.BlobId);

            pendingWrite.Dispose();
        }
    }

    private void RemovePendingBlobsForEntry(
        Guid entryId)
    {
        foreach (PendingBlobWrite pendingWrite in
                 _pendingBlobWrites.Values
                     .Where(write =>
                         write.EntryId == entryId)
                     .ToArray())
        {
            _pendingBlobWrites.Remove(
                pendingWrite.BlobId);

            pendingWrite.Dispose();
        }
    }

    private void ClearPendingBlobWrites()
    {
        foreach (PendingBlobWrite pendingWrite in
                 _pendingBlobWrites.Values)
        {
            pendingWrite.Dispose();
        }

        _pendingBlobWrites.Clear();
    }

    private static HashSet<Guid> GetBlobIds(
        VaultEntry entry)
    {
        return entry.Fields
            .Select(field => field.Value)
            .OfType<BlobFieldValue>()
            .Select(value => value.BlobId)
            .ToHashSet();
    }

    private bool IsManifestDirtyCore()
    {
        return _manifestDirty ||
               _entriesPendingDeletion.Count > 0;
    }

    private bool RequiresManifestWriteCore()
    {
        return IsManifestDirtyCore();
    }

    private bool RequiresSaveRetryCore()
    {
        return _pendingEntryChanges
                   .Values
                   .Any(change =>
                       change.EntryFileWritten) ||
               _pendingBlobWrites
                   .Values
                   .Any(write =>
                       write.BlobFileWritten);
    }

    private bool HasUnsavedUserChangesCore()
    {
        return _manifestDirty ||
               _pendingEntryChanges.Count > 0 ||
               _entriesPendingDeletion.Count > 0;
    }

    private bool HasPendingSaveWorkCore()
    {
        return HasUnsavedUserChangesCore() ||
               _orphanedEntryFilesPendingCleanup.Count > 0 ||
               _orphanedBlobFilesPendingCleanup.Count > 0;
    }

    private async Task<VaultEntry>
        ReadPersistedEntryCoreAsync(
            Guid entryId,
            EntryDescriptor descriptor,
            CancellationToken cancellationToken)
    {
        EntryFile entryFile =
            await _entryFileStore.ReadAsync(
                    VaultDirectoryPath,
                    entryId,
                    cancellationToken)
                .ConfigureAwait(false);

        if (entryFile.VaultId != Manifest.VaultId)
        {
            throw new InvalidDataException(
                $"Entry '{entryId}' belongs to a different vault.");
        }

        VaultEntry entry =
            _entryFileCodec.Open(
                entryFile,
                _vaultRootKey);

        if (entry.Revision != descriptor.Revision)
        {
            throw new InvalidDataException(
                $"Entry '{entryId}' has revision " +
                $"'{entry.Revision}', but the manifest expects " +
                $"revision '{descriptor.Revision}'.");
        }

        return entry;
    }

    private T ReadState<T>(
        Func<T> readOperation)
    {
        ArgumentNullException.ThrowIfNull(
            readOperation);

        EnterSynchronousOperation();

        try
        {
            return readOperation();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private T MutateState<T>(
        Func<T> mutation)
    {
        ArgumentNullException.ThrowIfNull(
            mutation);

        EnterSynchronousOperation();

        try
        {
            EnsureStateCanBeMutated();
            return mutation();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void MutateState(
        Action mutation)
    {
        ArgumentNullException.ThrowIfNull(
            mutation);

        EnterSynchronousOperation();

        try
        {
            EnsureStateCanBeMutated();
            mutation();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void EnterSynchronousOperation()
    {
        if (!_operationGate.Wait(0))
        {
            throw new InvalidOperationException(
                "Another vault operation is already in progress.");
        }

        try
        {
            EnsureNotDisposed();
        }
        catch
        {
            _operationGate.Release();
            throw;
        }
    }

    private void EnsureStateCanBeMutated()
    {
        if (RequiresSaveRetryCore())
        {
            throw new InvalidOperationException(
                "A previous save wrote one or more entry files " +
                "but did not successfully write the manifest. " +
                "Call SaveAsync again before making more changes.");
        }
    }

    private void EnsureEntryIsNotPendingDeletion(
        Guid entryId)
    {
        ValidateEntryId(entryId);
        GetEntryDescriptor(entryId);

        if (_entriesPendingDeletion.Contains(
                entryId))
        {
            throw new InvalidOperationException(
                $"Entry '{entryId}' is marked for deletion. " +
                "Undo its deletion before modifying it.");
        }
    }

    private EntryDescriptor GetEntryDescriptor(
        Guid entryId)
    {
        return Manifest.Entries.FirstOrDefault(
                   entry =>
                       entry.EntryId == entryId)
               ?? throw new KeyNotFoundException(
                   $"Entry '{entryId}' does not exist.");
    }

    private void RecordManifestChange(
        bool rebuildIndex)
    {
        _manifestDirty = true;

        if (rebuildIndex)
        {
            RebuildIndex();
        }
    }

    private void RebuildIndex()
    {
        _index = VaultIndex.Build(
            Manifest);
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);
    }

    private static void ValidateEntryId(
        Guid entryId)
    {
        if (entryId == Guid.Empty)
        {
            throw new ArgumentException(
                "The entry ID cannot be empty.",
                nameof(entryId));
        }
    }

    private static string NormalizeVaultDirectoryPath(
        string vaultDirectoryPath)
    {
        if (string.IsNullOrWhiteSpace(
                vaultDirectoryPath))
        {
            throw new ArgumentException(
                "The vault directory path cannot be empty.",
                nameof(vaultDirectoryPath));
        }

        return Path.GetFullPath(
            vaultDirectoryPath);
    }

    private static void ValidatePassword(
        string password)
    {
        ArgumentNullException.ThrowIfNull(
            password);

        if (password.Length == 0)
        {
            throw new ArgumentException(
                "The password cannot be empty.",
                nameof(password));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _operationGate
            .WaitAsync()
            .ConfigureAwait(false);

        try
        {
            if (_disposed)
                return;

            _disposed = true;

            CryptographicOperations.ZeroMemory(
                _vaultRootKey);

            ClearPendingBlobWrites();
            _pendingEntryChanges.Clear();
            _entriesPendingDeletion.Clear();
            _orphanedEntryFilesPendingCleanup.Clear();
            _orphanedBlobFilesPendingCleanup.Clear();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private sealed class PendingEntryChange
    {
        public VaultEntry WorkingEntry { get; private set; }

        public EntryChangeKind Kind { get; }

        // True means the entry file was replaced successfully
        // and the session is waiting for the manifest write.
        public bool EntryFileWritten { get; private set; }

        public bool ObsoleteBlobIdsRecorded { get; private set; }

        public IReadOnlyCollection<Guid> ObsoleteBlobIds =>
            _obsoleteBlobIds;

        public DateTimeOffset? FirstCommitModifiedUtc
        {
            get;
        }

        private readonly HashSet<Guid> _obsoleteBlobIds = [];

        public PendingEntryChange(
            VaultEntry workingEntry,
            EntryChangeKind kind,
            DateTimeOffset? firstCommitModifiedUtc = null)
        {
            ArgumentNullException.ThrowIfNull(
                workingEntry);

            if (kind is not
                (EntryChangeKind.New or
                 EntryChangeKind.Modified))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(kind),
                    kind,
                    "A pending entry must be new or modified.");
            }

            WorkingEntry = workingEntry;
            Kind = kind;
            FirstCommitModifiedUtc =
                firstCommitModifiedUtc;
        }

        public void ReplaceWorkingEntry(
            VaultEntry workingEntry)
        {
            ArgumentNullException.ThrowIfNull(
                workingEntry);

            if (EntryFileWritten)
            {
                throw new InvalidOperationException(
                    "An entry whose file has already been written " +
                    "cannot be modified until the manifest save " +
                    "is retried.");
            }

            WorkingEntry = workingEntry;
        }

        public void RecordEntryFileWrite(
            VaultEntry committedEntry)
        {
            ArgumentNullException.ThrowIfNull(
                committedEntry);

            WorkingEntry = committedEntry;
            EntryFileWritten = true;
        }

        public void RecordObsoleteBlobIds(
            IEnumerable<Guid> blobIds)
        {
            ArgumentNullException.ThrowIfNull(blobIds);

            if (ObsoleteBlobIdsRecorded)
            {
                throw new InvalidOperationException(
                    "Obsolete blob IDs were already recorded.");
            }

            _obsoleteBlobIds.UnionWith(blobIds);
            ObsoleteBlobIdsRecorded = true;
        }
    }

    private sealed class PendingBlobWrite : IDisposable
    {
        private byte[]? _plaintext;

        public PendingBlobWrite(
            Guid entryId,
            Guid blobId,
            byte[] plaintext)
        {
            EntryId = entryId;
            BlobId = blobId;
            _plaintext = plaintext;
        }

        public Guid EntryId { get; }
        public Guid BlobId { get; }
        public bool BlobFileWritten { get; private set; }

        public ReadOnlySpan<byte> Plaintext =>
            GetPlaintext();

        public byte[] CopyPlaintext()
        {
            return GetPlaintext().ToArray();
        }

        public void RecordBlobFileWrite()
        {
            if (BlobFileWritten)
            {
                throw new InvalidOperationException(
                    "The blob file was already written.");
            }

            BlobFileWritten = true;
            DisposePlaintext();
        }

        public void Dispose()
        {
            DisposePlaintext();
        }

        private ReadOnlySpan<byte> GetPlaintext()
        {
            return _plaintext ??
                throw new InvalidOperationException(
                    "The pending blob plaintext is unavailable.");
        }

        private void DisposePlaintext()
        {
            byte[]? plaintext =
                Interlocked.Exchange(
                    ref _plaintext,
                    null);

            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(
                    plaintext);
            }
        }
    }
}
