using System.Security.Cryptography;
using Cripty.Core.Entries;
using Cripty.Core.Vaults;
using Cripty.Cryptography.Keys;
using Cripty.Storage.Codecs;
using Cripty.Storage.FileSystem;
using Cripty.Storage.Formats;

namespace Cripty.Application.Vaults;

internal sealed class VaultKeyRotationService
{
    private const string StagingDirectoryPrefix =
        ".cripty-password-change-";

    private const string RollbackDirectoryPrefix =
        ".cripty-password-rollback-";

    private readonly VaultFileCodec _vaultFileCodec;
    private readonly EntryFileCodec _entryFileCodec;
    private readonly BlobFileCodec _blobFileCodec;

    private readonly VaultFileStore _vaultFileStore;
    private readonly EntryFileStore _entryFileStore;
    private readonly BlobFileStore _blobFileStore;

    public VaultKeyRotationService(
        VaultFileCodec vaultFileCodec,
        EntryFileCodec entryFileCodec,
        BlobFileCodec blobFileCodec,
        VaultFileStore vaultFileStore,
        EntryFileStore entryFileStore,
        BlobFileStore blobFileStore)
    {
        _vaultFileCodec = vaultFileCodec;
        _entryFileCodec = entryFileCodec;
        _blobFileCodec = blobFileCodec;

        _vaultFileStore = vaultFileStore;
        _entryFileStore = entryFileStore;
        _blobFileStore = blobFileStore;
    }

    public async Task<VaultKeyRotationResult> RotateAsync(
        string vaultDirectoryPath,
        VaultManifest currentManifest,
        byte[] currentRootKey,
        string newPassword,
        Argon2idParameters? newKdfParameters,
        IProgress<VaultPasswordChangeProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentManifest);
        ArgumentNullException.ThrowIfNull(currentRootKey);
        ArgumentNullException.ThrowIfNull(newPassword);

        if (currentRootKey.Length != VaultRootKeyGenerator.KeySize)
        {
            throw new ArgumentException(
                $"The vault root key must be exactly " +
                $"{VaultRootKeyGenerator.KeySize} bytes.",
                nameof(currentRootKey));
        }

        string normalizedVaultPath =
            Path.GetFullPath(vaultDirectoryPath);

        if (!Directory.Exists(normalizedVaultPath))
        {
            throw new DirectoryNotFoundException(
                $"The vault directory '{normalizedVaultPath}' " +
                "does not exist.");
        }

        string? vaultRootPath =
            Path.GetDirectoryName(normalizedVaultPath);

        if (string.IsNullOrWhiteSpace(vaultRootPath))
        {
            throw new InvalidOperationException(
                "The vault parent directory could not be resolved.");
        }

        VaultManifest rotatedManifest = new(
            currentManifest.SchemaVersion,
            currentManifest.VaultId,
            checked(currentManifest.Generation + 1),
            currentManifest.Folders,
            currentManifest.Tags,
            currentManifest.Entries,
            currentManifest.SortPreferences);

        string operationId = Guid.NewGuid().ToString("N");

        string stagingPath = Path.Combine(
            vaultRootPath,
            StagingDirectoryPrefix + operationId + ".tmp");

        string rollbackPath = Path.Combine(
            vaultRootPath,
            RollbackDirectoryPrefix + operationId + ".tmp");

        byte[] rotatedRootKey =
            new byte[VaultRootKeyGenerator.KeySize];

        bool existingVaultMoved = false;
        bool rotatedVaultPublished = false;

        try
        {
            ReportProgress(
                progress,
                0,
                VaultPasswordChangeStage.GeneratingRootKey,
                currentManifest.Entries.Count,
                totalBlobs: 0);

            VaultRootKeyGenerator.Generate(rotatedRootKey);

            ReportProgress(
                progress,
                10,
                VaultPasswordChangeStage.PreparingVault,
                currentManifest.Entries.Count,
                totalBlobs: 0);

            RotationWorkEstimate workEstimate =
                EstimateRotationWork(
                    normalizedVaultPath,
                    currentManifest);

            VaultFile rotatedVaultFile =
                _vaultFileCodec.Create(
                    rotatedManifest,
                    rotatedRootKey,
                    newPassword,
                    newKdfParameters);

            await _vaultFileStore.WriteAsync(
                    stagingPath,
                    rotatedVaultFile,
                    cancellationToken)
                .ConfigureAwait(false);

            Dictionary<Guid, long> migratedBlobLengths = [];
            long processedBytes = 0;
            int processedEntries = 0;
            int processedBlobs = 0;

            ReportProgress(
                progress,
                10,
                VaultPasswordChangeStage.ReencryptingContent,
                workEstimate.TotalEntries,
                workEstimate.TotalBlobs);

            foreach (EntryDescriptor descriptor in
                     currentManifest.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                VaultEntry entry =
                    await ReadCurrentEntryAsync(
                            normalizedVaultPath,
                            currentManifest.VaultId,
                            descriptor,
                            currentRootKey,
                            cancellationToken)
                        .ConfigureAwait(false);

                EntryFile rotatedEntryFile =
                    _entryFileCodec.Create(
                        rotatedManifest.VaultId,
                        entry,
                        rotatedRootKey);

                await _entryFileStore.WriteAsync(
                        stagingPath,
                        rotatedEntryFile,
                        cancellationToken)
                    .ConfigureAwait(false);

                processedEntries++;
                processedBytes = AddWithoutOverflow(
                    processedBytes,
                    GetEntryFileLength(
                        normalizedVaultPath,
                        descriptor.EntryId));

                ReportContentProgress(
                    progress,
                    workEstimate,
                    processedBytes,
                    processedEntries,
                    processedBlobs);

                foreach (BlobFieldValue blob in
                         entry.Fields
                             .Select(field => field.Value)
                             .OfType<BlobFieldValue>())
                {
                    bool wasMigrated =
                        await MigrateBlobOnceAsync(
                            normalizedVaultPath,
                            stagingPath,
                            rotatedManifest.VaultId,
                            blob,
                            currentRootKey,
                            rotatedRootKey,
                            migratedBlobLengths,
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (wasMigrated)
                    {
                        processedBlobs++;
                        processedBytes = AddWithoutOverflow(
                            processedBytes,
                            GetBlobFileLength(
                                normalizedVaultPath,
                                blob.BlobId));

                        ReportContentProgress(
                            progress,
                            workEstimate,
                            processedBytes,
                            processedEntries,
                            processedBlobs);
                    }
                }
            }

            ReportProgress(
                progress,
                95,
                VaultPasswordChangeStage.Verifying,
                workEstimate.TotalEntries,
                workEstimate.TotalBlobs,
                processedEntries,
                processedBlobs);

            await ValidateStagedVaultAsync(
                    stagingPath,
                    rotatedManifest,
                    newPassword,
                    rotatedRootKey,
                    migratedBlobLengths,
                    cancellationToken)
                .ConfigureAwait(false);

            ReportProgress(
                progress,
                98,
                VaultPasswordChangeStage.Publishing,
                workEstimate.TotalEntries,
                workEstimate.TotalBlobs,
                processedEntries,
                processedBlobs);

            // Do not observe cancellation between the two directory moves.
            // Once publication starts it must either complete or restore the
            // original directory.
            cancellationToken.ThrowIfCancellationRequested();

            Directory.Move(
                normalizedVaultPath,
                rollbackPath);

            existingVaultMoved = true;

            try
            {
                Directory.Move(
                    stagingPath,
                    normalizedVaultPath);

                rotatedVaultPublished = true;
            }
            catch
            {
                if (!Directory.Exists(normalizedVaultPath) &&
                    Directory.Exists(rollbackPath))
                {
                    Directory.Move(
                        rollbackPath,
                        normalizedVaultPath);

                    existingVaultMoved = false;
                }

                throw;
            }

            // The rotated vault is already the live vault. A transient
            // cleanup failure must not make the caller retry a completed
            // password change.
            TryDeleteDirectory(rollbackPath);
            existingVaultMoved = false;

            ReportProgress(
                progress,
                100,
                VaultPasswordChangeStage.Completed,
                workEstimate.TotalEntries,
                workEstimate.TotalBlobs,
                processedEntries,
                processedBlobs);

            return new VaultKeyRotationResult(
                rotatedVaultFile,
                rotatedManifest,
                rotatedRootKey);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(
                rotatedRootKey);

            throw;
        }
        finally
        {
            if (!rotatedVaultPublished)
            {
                TryDeleteDirectory(stagingPath);
            }

            if (existingVaultMoved &&
                !Directory.Exists(normalizedVaultPath) &&
                Directory.Exists(rollbackPath))
            {
                TryRestoreDirectory(
                    rollbackPath,
                    normalizedVaultPath);
            }
        }
    }

    private async Task<VaultEntry> ReadCurrentEntryAsync(
        string vaultDirectoryPath,
        Guid vaultId,
        EntryDescriptor descriptor,
        byte[] currentRootKey,
        CancellationToken cancellationToken)
    {
        EntryFile entryFile =
            await _entryFileStore.ReadAsync(
                    vaultDirectoryPath,
                    descriptor.EntryId,
                    cancellationToken)
                .ConfigureAwait(false);

        if (entryFile.VaultId != vaultId)
        {
            throw new InvalidDataException(
                $"Entry '{descriptor.EntryId}' belongs to a " +
                "different vault.");
        }

        VaultEntry entry =
            _entryFileCodec.Open(
                entryFile,
                currentRootKey);

        if (entry.Revision != descriptor.Revision)
        {
            throw new InvalidDataException(
                $"Entry '{descriptor.EntryId}' has revision " +
                $"'{entry.Revision}', but the manifest expects " +
                $"revision '{descriptor.Revision}'.");
        }

        return entry;
    }

    private async Task<bool> MigrateBlobOnceAsync(
        string sourceVaultPath,
        string stagingVaultPath,
        Guid vaultId,
        BlobFieldValue blob,
        byte[] currentRootKey,
        byte[] rotatedRootKey,
        Dictionary<Guid, long> migratedBlobLengths,
        CancellationToken cancellationToken)
    {
        if (migratedBlobLengths.TryGetValue(
                blob.BlobId,
                out long existingLength))
        {
            if (existingLength != blob.Length)
            {
                throw new InvalidDataException(
                    $"Blob '{blob.BlobId}' is referenced with " +
                    "conflicting lengths.");
            }

            return false;
        }

        BlobFile blobFile =
            await _blobFileStore.ReadAsync(
                    sourceVaultPath,
                    blob.BlobId,
                    cancellationToken)
                .ConfigureAwait(false);

        if (blobFile.VaultId != vaultId)
        {
            throw new InvalidDataException(
                $"Blob '{blob.BlobId}' belongs to a different vault.");
        }

        byte[] plaintext =
            _blobFileCodec.Open(
                blobFile,
                currentRootKey);

        try
        {
            if (plaintext.LongLength != blob.Length)
            {
                throw new InvalidDataException(
                    $"Blob '{blob.BlobId}' has an unexpected length.");
            }

            BlobFile rotatedBlobFile =
                _blobFileCodec.Create(
                    vaultId,
                    blob.BlobId,
                    plaintext,
                    rotatedRootKey);

            await _blobFileStore.WriteAsync(
                    stagingVaultPath,
                    rotatedBlobFile,
                    cancellationToken)
                .ConfigureAwait(false);

            migratedBlobLengths.Add(
                blob.BlobId,
                blob.Length);

            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(
                plaintext);
        }
    }

    private static RotationWorkEstimate EstimateRotationWork(
        string vaultDirectoryPath,
        VaultManifest manifest)
    {
        long totalBytes = 0;

        foreach (EntryDescriptor descriptor in manifest.Entries)
        {
            totalBytes = AddWithoutOverflow(
                totalBytes,
                GetEntryFileLength(
                    vaultDirectoryPath,
                    descriptor.EntryId));
        }

        string blobsDirectoryPath = Path.Combine(
            vaultDirectoryPath,
            BlobFileStore.BlobsDirectoryName);

        string[] blobFilePaths = Directory.Exists(blobsDirectoryPath)
            ? Directory.GetFiles(
                blobsDirectoryPath,
                "*" + BlobFileStore.BlobFileExtension,
                SearchOption.TopDirectoryOnly)
            : [];

        foreach (string blobFilePath in blobFilePaths)
        {
            totalBytes = AddWithoutOverflow(
                totalBytes,
                new FileInfo(blobFilePath).Length);
        }

        return new RotationWorkEstimate(
            totalBytes,
            manifest.Entries.Count,
            blobFilePaths.Length);
    }

    private static long GetEntryFileLength(
        string vaultDirectoryPath,
        Guid entryId)
    {
        return new FileInfo(
            Path.Combine(
                vaultDirectoryPath,
                EntryFileStore.EntriesDirectoryName,
                entryId.ToString("D") +
                EntryFileStore.EntryFileExtension))
            .Length;
    }

    private static long GetBlobFileLength(
        string vaultDirectoryPath,
        Guid blobId)
    {
        return new FileInfo(
            Path.Combine(
                vaultDirectoryPath,
                BlobFileStore.BlobsDirectoryName,
                blobId.ToString("D") +
                BlobFileStore.BlobFileExtension))
            .Length;
    }

    private static long AddWithoutOverflow(
        long left,
        long right)
    {
        return left > long.MaxValue - right
            ? long.MaxValue
            : left + right;
    }

    private static void ReportContentProgress(
        IProgress<VaultPasswordChangeProgress>? progress,
        RotationWorkEstimate estimate,
        long processedBytes,
        int processedEntries,
        int processedBlobs)
    {
        double completedFraction = estimate.TotalBytes > 0
            ? Math.Clamp(
                (double)processedBytes / estimate.TotalBytes,
                0,
                1)
            : 1;

        ReportProgress(
            progress,
            10 + (85 * completedFraction),
            VaultPasswordChangeStage.ReencryptingContent,
            estimate.TotalEntries,
            estimate.TotalBlobs,
            processedEntries,
            processedBlobs);
    }

    private static void ReportProgress(
        IProgress<VaultPasswordChangeProgress>? progress,
        double percentage,
        VaultPasswordChangeStage stage,
        int totalEntries,
        int totalBlobs,
        int processedEntries = 0,
        int processedBlobs = 0)
    {
        progress?.Report(
            new VaultPasswordChangeProgress(
                Math.Clamp(percentage, 0, 100),
                stage,
                processedEntries,
                totalEntries,
                processedBlobs,
                totalBlobs));
    }

    private sealed record RotationWorkEstimate(
        long TotalBytes,
        int TotalEntries,
        int TotalBlobs);

    private async Task ValidateStagedVaultAsync(
        string stagingVaultPath,
        VaultManifest expectedManifest,
        string newPassword,
        byte[] expectedRootKey,
        IReadOnlyDictionary<Guid, long> expectedBlobLengths,
        CancellationToken cancellationToken)
    {
        VaultFile storedVaultFile =
            await _vaultFileStore.ReadAsync(
                    stagingVaultPath,
                    cancellationToken)
                .ConfigureAwait(false);

        byte[] recoveredRootKey =
            new byte[VaultRootKeyGenerator.KeySize];

        try
        {
            VaultManifest storedManifest =
                _vaultFileCodec.Open(
                    storedVaultFile,
                    newPassword,
                    recoveredRootKey);

            if (!CryptographicOperations.FixedTimeEquals(
                    recoveredRootKey,
                    expectedRootKey))
            {
                throw new CryptographicException(
                    "The staged vault did not recover the rotated " +
                    "root key.");
            }

            EnsureManifestIdentityMatches(
                expectedManifest,
                storedManifest);

            Dictionary<Guid, long> validatedBlobLengths = [];

            foreach (EntryDescriptor descriptor in
                     storedManifest.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                EntryFile entryFile =
                    await _entryFileStore.ReadAsync(
                            stagingVaultPath,
                            descriptor.EntryId,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (entryFile.VaultId != storedManifest.VaultId)
                {
                    throw new InvalidDataException(
                        $"Staged entry '{descriptor.EntryId}' belongs " +
                        "to a different vault.");
                }

                VaultEntry entry =
                    _entryFileCodec.Open(
                        entryFile,
                        recoveredRootKey);

                if (entry.Revision != descriptor.Revision)
                {
                    throw new InvalidDataException(
                        $"Staged entry '{descriptor.EntryId}' has an " +
                        "unexpected revision.");
                }

                foreach (BlobFieldValue blob in
                         entry.Fields
                             .Select(field => field.Value)
                             .OfType<BlobFieldValue>())
                {
                    if (validatedBlobLengths.TryGetValue(
                            blob.BlobId,
                            out long existingLength))
                    {
                        if (existingLength != blob.Length)
                        {
                            throw new InvalidDataException(
                                $"Staged blob '{blob.BlobId}' is " +
                                "referenced with conflicting lengths.");
                        }

                        continue;
                    }

                    BlobFile blobFile =
                        await _blobFileStore.ReadAsync(
                                stagingVaultPath,
                                blob.BlobId,
                                cancellationToken)
                            .ConfigureAwait(false);

                    if (blobFile.VaultId != storedManifest.VaultId)
                    {
                        throw new InvalidDataException(
                            $"Staged blob '{blob.BlobId}' belongs " +
                            "to a different vault.");
                    }

                    byte[] plaintext =
                        _blobFileCodec.Open(
                            blobFile,
                            recoveredRootKey);

                    try
                    {
                        if (plaintext.LongLength != blob.Length)
                        {
                            throw new InvalidDataException(
                                $"Staged blob '{blob.BlobId}' has an " +
                                "unexpected length.");
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(
                            plaintext);
                    }

                    validatedBlobLengths.Add(
                        blob.BlobId,
                        blob.Length);
                }
            }

            if (validatedBlobLengths.Count !=
                    expectedBlobLengths.Count ||
                validatedBlobLengths.Any(pair =>
                    !expectedBlobLengths.TryGetValue(
                        pair.Key,
                        out long expectedLength) ||
                    expectedLength != pair.Value))
            {
                throw new InvalidDataException(
                    "The staged vault does not contain the expected blobs.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(
                recoveredRootKey);
        }
    }

    private static void EnsureManifestIdentityMatches(
        VaultManifest expected,
        VaultManifest actual)
    {
        if (actual.SchemaVersion != expected.SchemaVersion ||
            actual.VaultId != expected.VaultId ||
            actual.Generation != expected.Generation ||
            !FoldersMatch(expected.Folders, actual.Folders) ||
            !TagsMatch(expected.Tags, actual.Tags) ||
            !EntriesMatch(expected.Entries, actual.Entries) ||
            !SortPreferencesMatch(
                expected.SortPreferences,
                actual.SortPreferences))
        {
            throw new InvalidDataException(
                "The staged vault manifest does not match the " +
                "source vault.");
        }
    }

    private static bool SortPreferencesMatch(
        VaultSortPreferences expected,
        VaultSortPreferences actual)
    {
        return expected.AllEntriesSortMode ==
                   actual.AllEntriesSortMode &&
               expected.RootSortMode ==
                   actual.RootSortMode &&
               expected.FolderSortModes.Count ==
                   actual.FolderSortModes.Count &&
               expected.FolderSortModes.All(pair =>
                   actual.FolderSortModes.TryGetValue(
                       pair.Key,
                       out EntrySortMode actualMode) &&
                   actualMode == pair.Value);
    }

    private static bool FoldersMatch(
        IReadOnlyList<FolderDescriptor> expected,
        IReadOnlyList<FolderDescriptor> actual)
    {
        return expected.Count == actual.Count &&
               expected.Zip(actual).All(pair =>
                   pair.First.FolderId == pair.Second.FolderId &&
                   pair.First.ParentFolderId ==
                       pair.Second.ParentFolderId &&
                   string.Equals(
                       pair.First.Name,
                       pair.Second.Name,
                       StringComparison.Ordinal));
    }

    private static bool TagsMatch(
        IReadOnlyList<TagDescriptor> expected,
        IReadOnlyList<TagDescriptor> actual)
    {
        return expected.Count == actual.Count &&
               expected.Zip(actual).All(pair =>
                   pair.First.TagId == pair.Second.TagId &&
                   string.Equals(
                       pair.First.Name,
                       pair.Second.Name,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       pair.First.Color,
                       pair.Second.Color,
                       StringComparison.Ordinal));
    }

    private static bool EntriesMatch(
        IReadOnlyList<EntryDescriptor> expected,
        IReadOnlyList<EntryDescriptor> actual)
    {
        return expected.Count == actual.Count &&
               expected.Zip(actual).All(pair =>
                   pair.First.EntryId == pair.Second.EntryId &&
                   pair.First.FolderId == pair.Second.FolderId &&
                   pair.First.Revision == pair.Second.Revision &&
                   pair.First.CreatedUtc == pair.Second.CreatedUtc &&
                   pair.First.ModifiedUtc == pair.Second.ModifiedUtc &&
                   pair.First.TimelineDateOverride ==
                       pair.Second.TimelineDateOverride &&
                   string.Equals(
                       pair.First.Name,
                       pair.Second.Name,
                       StringComparison.Ordinal) &&
                   pair.First.TagIds.SequenceEqual(
                       pair.Second.TagIds));
    }

    private static void TryRestoreDirectory(
        string rollbackPath,
        string vaultDirectoryPath)
    {
        try
        {
            Directory.Move(
                rollbackPath,
                vaultDirectoryPath);
        }
        catch (IOException)
        {
            // Keep the rollback directory intact for manual recovery.
        }
        catch (UnauthorizedAccessException)
        {
            // Keep the rollback directory intact for manual recovery.
        }
    }

    private static void TryDeleteDirectory(string directoryPath)
    {
        try
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(
                    directoryPath,
                    recursive: true);
            }
        }
        catch (IOException)
        {
            // Do not hide the migration or publication result because
            // best-effort cleanup of a hidden temporary directory failed.
        }
        catch (UnauthorizedAccessException)
        {
            // Do not hide the original result.
        }
    }
}

internal sealed class VaultKeyRotationResult : IDisposable
{
    private byte[]? _rootKey;

    public VaultKeyRotationResult(
        VaultFile vaultFile,
        VaultManifest manifest,
        byte[] rootKey)
    {
        VaultFile = vaultFile;
        Manifest = manifest;
        _rootKey = rootKey;
    }

    public VaultFile VaultFile { get; }
    public VaultManifest Manifest { get; }

    public void CopyRootKeyTo(Span<byte> destination)
    {
        byte[] rootKey =
            _rootKey ??
            throw new ObjectDisposedException(
                nameof(VaultKeyRotationResult));

        if (destination.Length != rootKey.Length)
        {
            throw new ArgumentException(
                "The root-key destination has an invalid length.",
                nameof(destination));
        }

        CryptographicOperations.ZeroMemory(destination);
        rootKey.AsSpan().CopyTo(destination);
    }

    public void Dispose()
    {
        byte[]? rootKey =
            Interlocked.Exchange(
                ref _rootKey,
                null);

        if (rootKey is not null)
        {
            CryptographicOperations.ZeroMemory(rootKey);
        }
    }
}
