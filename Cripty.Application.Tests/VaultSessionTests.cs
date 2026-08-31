using System.Security.Cryptography;
using Cripty.Application.Vaults;
using Cripty.Core.Entries;
using Cripty.Core.Vaults;
using Cripty.Cryptography.Keys;
using Cripty.Storage.Codecs;
using Cripty.Storage.FileSystem;
using Cripty.Storage.Formats;

namespace Cripty.Application.Tests;

[TestClass]
[DoNotParallelize]
public sealed class VaultSessionTests
{
    private const string Password =
        "correct horse battery staple";

    private const string NewPassword =
        "new correct horse battery staple";

    private string _vaultDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _vaultDirectory = Path.Combine(
            Path.GetTempPath(),
            "Cripty.Application.Tests",
            Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_vaultDirectory))
        {
            Directory.Delete(
                _vaultDirectory,
                recursive: true);
        }
    }

    [TestMethod]
    public async Task CreateAsync_NewVault_IsEmptyAndClean()
    {
        await using VaultSession session =
            await CreateSessionAsync();

        Assert.AreEqual(
            Path.GetFullPath(_vaultDirectory),
            session.VaultDirectoryPath);

        Assert.AreNotEqual(Guid.Empty, session.VaultId);
        Assert.AreEqual(0L, session.ManifestGeneration);

        AssertKdfParametersEqual(
            TestKdfParameters,
            session.PasswordKdfParameters);

        Assert.AreEqual(0, session.Folders.Count);
        Assert.AreEqual(0, session.Tags.Count);
        Assert.AreEqual(0, session.Entries.Count);

        Assert.IsFalse(session.IsManifestDirty);
        Assert.IsFalse(session.HasPendingEntryChanges);
        Assert.IsFalse(session.HasPendingEntryDeletions);
        Assert.IsFalse(session.HasPendingEntryFileDeletions);
        Assert.IsFalse(session.HasPendingBlobFileDeletions);
        Assert.IsFalse(session.RequiresSaveRetry);
        Assert.IsFalse(session.HasUnsavedChanges);

        Assert.IsTrue(
            File.Exists(GetVaultFilePath()));
    }

    [TestMethod]
    public async Task CreateAsync_ExistingVault_Throws()
    {
        await using VaultSession session =
            await CreateSessionAsync();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => VaultSession.CreateAsync(
                _vaultDirectory,
                Password,
                TestKdfParameters));
    }

    [TestMethod]
    public async Task OpenAsync_LegacyVault_AddsVisibleGenerationHint()
    {
        await using (VaultSession created =
                     await CreateSessionAsync())
        {
        }

        VaultFileStore store = new();
        VaultFile current =
            await store.ReadAsync(_vaultDirectory);

        VaultFile legacy = new()
        {
            FormatVersion = current.FormatVersion,
            VaultId = current.VaultId,
            ManifestGeneration = null,
            PasswordKeySlot = current.PasswordKeySlot,
            ManifestEnvelope = current.ManifestEnvelope
        };

        await store.WriteAsync(_vaultDirectory, legacy);

        await using VaultSession reopened =
            await VaultSession.OpenAsync(
                _vaultDirectory,
                Password);

        VaultFile upgraded =
            await store.ReadAsync(_vaultDirectory);

        Assert.AreEqual(
            reopened.ManifestGeneration,
            upgraded.ManifestGeneration);
    }

    [TestMethod]
    public async Task OpenAsync_SchemaOneVault_MigratesManifestOnce()
    {
        Directory.CreateDirectory(_vaultDirectory);

        Guid vaultId = Guid.NewGuid();
        const long originalGeneration = 7;

        VaultManifest legacyManifest = new(
            schemaVersion: 1,
            vaultId: vaultId,
            generation: originalGeneration,
            folders: [],
            tags: [],
            entries: []);

        byte[] rootKey =
            new byte[VaultRootKeyGenerator.KeySize];

        VaultFileStore store = new();

        try
        {
            VaultRootKeyGenerator.Generate(rootKey);

            VaultFile legacyFile =
                new VaultFileCodec().Create(
                    legacyManifest,
                    rootKey,
                    Password,
                    TestKdfParameters);

            await store.WriteAsync(
                _vaultDirectory,
                legacyFile);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rootKey);
        }

        await using (VaultSession opened =
                     await VaultSession.OpenAsync(
                         _vaultDirectory,
                         Password))
        {
            Assert.AreEqual(
                StorageSchemaVersions.CurrentManifest,
                opened.ManifestSchemaVersion);

            Assert.AreEqual(
                originalGeneration + 1,
                opened.ManifestGeneration);
        }

        VaultFile upgradedFile =
            await store.ReadAsync(_vaultDirectory);

        Assert.AreEqual(
            originalGeneration + 1,
            upgradedFile.ManifestGeneration);

        await using VaultSession reopened =
            await VaultSession.OpenAsync(
                _vaultDirectory,
                Password);

        Assert.AreEqual(
            originalGeneration + 1,
            reopened.ManifestGeneration);
    }

    [TestMethod]
    public async Task OpenAsync_SchemaTwoVault_AddsDefaultSortPreferences()
    {
        Directory.CreateDirectory(_vaultDirectory);

        Guid vaultId = Guid.NewGuid();
        Guid folderId = Guid.NewGuid();
        const long originalGeneration = 11;

        VaultManifest legacyManifest = new(
            schemaVersion: 2,
            vaultId: vaultId,
            generation: originalGeneration,
            folders:
            [
                new FolderDescriptor(
                    folderId,
                    "Journal",
                    parentFolderId: null)
            ],
            tags: [],
            entries: []);

        byte[] rootKey =
            new byte[VaultRootKeyGenerator.KeySize];

        try
        {
            VaultRootKeyGenerator.Generate(rootKey);

            VaultFile legacyFile =
                new VaultFileCodec().Create(
                    legacyManifest,
                    rootKey,
                    Password,
                    TestKdfParameters);

            await new VaultFileStore().WriteAsync(
                _vaultDirectory,
                legacyFile);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rootKey);
        }

        await using VaultSession opened =
            await VaultSession.OpenAsync(
                _vaultDirectory,
                Password);

        Assert.AreEqual(
            StorageSchemaVersions.CurrentManifest,
            opened.ManifestSchemaVersion);

        Assert.AreEqual(
            originalGeneration + 1,
            opened.ManifestGeneration);

        Assert.AreEqual(
            EntrySortMode.ModifiedNewest,
            opened.AllEntriesSortMode);

        Assert.AreEqual(
            EntrySortMode.ModifiedNewest,
            opened.RootSortMode);

        Assert.AreEqual(
            EntrySortMode.ModifiedNewest,
            opened.GetFolderSortMode(folderId));
    }

    [TestMethod]
    public async Task SortPreferences_SaveAndOpen_KeepEntryMetadataUntouched()
    {
        Guid folderId;
        Guid entryId;
        long revision;
        DateTimeOffset createdUtc;
        DateTimeOffset modifiedUtc;

        await using (VaultSession session =
                     await CreateSessionAsync())
        {
            FolderDescriptor folder =
                session.CreateFolder("Journal");

            VaultEntry entry =
                session.CreateEntry(
                    "Historical note",
                    folder.FolderId);

            await session.SaveAsync();

            EntryDescriptor before =
                session.Entries.Single(candidate =>
                    candidate.EntryId == entry.EntryId);

            folderId = folder.FolderId;
            entryId = entry.EntryId;
            revision = before.Revision;
            createdUtc = before.CreatedUtc;
            modifiedUtc = before.ModifiedUtc;

            session.SetAllEntriesSortMode(
                EntrySortMode.TimelineNewest);
            session.SetRootSortMode(
                EntrySortMode.CreatedOldest);
            session.SetFolderSortMode(
                folderId,
                EntrySortMode.TimelineOldest);

            Assert.IsTrue(session.HasUnsavedChanges);

            EntryDescriptor after =
                session.Entries.Single(candidate =>
                    candidate.EntryId == entryId);

            Assert.AreEqual(revision, after.Revision);
            Assert.AreEqual(createdUtc, after.CreatedUtc);
            Assert.AreEqual(modifiedUtc, after.ModifiedUtc);

            await session.SaveAsync();
        }

        await using VaultSession reopened =
            await VaultSession.OpenAsync(
                _vaultDirectory,
                Password);

        Assert.AreEqual(
            EntrySortMode.TimelineNewest,
            reopened.AllEntriesSortMode);
        Assert.AreEqual(
            EntrySortMode.CreatedOldest,
            reopened.RootSortMode);
        Assert.AreEqual(
            EntrySortMode.TimelineOldest,
            reopened.GetFolderSortMode(folderId));

        EntryDescriptor restored =
            reopened.Entries.Single(candidate =>
                candidate.EntryId == entryId);

        Assert.AreEqual(revision, restored.Revision);
        Assert.AreEqual(createdUtc, restored.CreatedUtc);
        Assert.AreEqual(modifiedUtc, restored.ModifiedUtc);
    }

    [TestMethod]
    public async Task SaveAndOpen_CompleteVault_RoundTrips()
    {
        Guid vaultId;
        Guid folderId;
        Guid tagId;
        Guid entryId;

        await using (VaultSession session =
                     await CreateSessionAsync())
        {
            FolderDescriptor folder =
                session.CreateFolder("Accounts");

            TagDescriptor tag =
                session.CreateTag(
                    "Important",
                    "#ff0000");

            VaultEntry entry =
                session.CreateEntry(
                    "Primary account",
                    folder.FolderId,
                    [tag.TagId],
                    [CreateTextField("secret text 🔐")]);

            vaultId = session.VaultId;
            folderId = folder.FolderId;
            tagId = tag.TagId;
            entryId = entry.EntryId;

            await session.SaveAsync();

            Assert.AreEqual(1L, session.ManifestGeneration);
            Assert.AreEqual(
                1L,
                session.Entries.Single().Revision);

            Assert.IsFalse(session.HasUnsavedChanges);
        }

        await using VaultSession reopened =
            await VaultSession.OpenAsync(
                _vaultDirectory,
                Password);

        Assert.AreEqual(vaultId, reopened.VaultId);
        Assert.AreEqual(1L, reopened.ManifestGeneration);

        FolderDescriptor restoredFolder =
            reopened.Folders.Single();

        TagDescriptor restoredTag =
            reopened.Tags.Single();

        EntryDescriptor restoredDescriptor =
            reopened.Entries.Single();

        Assert.AreEqual(folderId, restoredFolder.FolderId);
        Assert.AreEqual("Accounts", restoredFolder.Name);

        Assert.AreEqual(tagId, restoredTag.TagId);
        Assert.AreEqual("Important", restoredTag.Name);
        Assert.AreEqual("#ff0000", restoredTag.Color);

        Assert.AreEqual(entryId, restoredDescriptor.EntryId);
        Assert.AreEqual(
            "Primary account",
            restoredDescriptor.Name);

        Assert.AreEqual(
            folderId,
            restoredDescriptor.FolderId);

        Assert.AreEqual(1L, restoredDescriptor.Revision);

        CollectionAssert.AreEqual(
            new[] { tagId },
            restoredDescriptor.TagIds.ToArray());

        VaultEntry restoredEntry =
            await reopened.GetEntryAsync(entryId);

        Assert.AreEqual(1L, restoredEntry.Revision);
        AssertEntryText(restoredEntry, "secret text 🔐");

        Assert.AreEqual(
            entryId,
            reopened.Index
                .EntriesByFolderId[folderId]
                .Single()
                .EntryId);

        Assert.AreEqual(
            entryId,
            reopened.Index
                .EntriesByTagId[tagId]
                .Single()
                .EntryId);
    }

    [TestMethod]
    public async Task ReplaceEntry_NewEntry_RemainsNewUntilSaved()
    {
        await using VaultSession session =
            await CreateSessionAsync();

        VaultEntry original =
            session.CreateEntry(
                "Draft",
                fields:
                [
                    CreateTextField("v1")
                ]);

        VaultEntry replacement =
            WithText(original, "v2");

        session.ReplaceEntry(replacement);

        EntrySessionState state =
            session.GetEntrySessionState(
                original.EntryId);

        Assert.AreEqual(
            EntryChangeKind.New,
            state.ChangeKind);

        Assert.IsFalse(state.IsPendingDeletion);
        Assert.IsFalse(
            session.HasPendingEntryContentChanges(
                original.EntryId));

        await Assert.ThrowsExactlyAsync<
            InvalidOperationException>(
            () => session.GetPersistedEntryAsync(
                original.EntryId));

        Assert.IsTrue(session.HasPendingEntryChanges);
        Assert.IsTrue(session.HasUnsavedChanges);

        VaultEntry workingEntry =
            await session.GetEntryAsync(
                original.EntryId);

        AssertEntryText(workingEntry, "v2");

        await session.SaveAsync();

        VaultEntry committedEntry =
            await session.GetEntryAsync(
                original.EntryId);

        Assert.AreEqual(1L, committedEntry.Revision);
        AssertEntryText(committedEntry, "v2");

        Assert.AreEqual(
            EntryChangeKind.None,
            session.GetEntrySessionState(
                    original.EntryId)
                .ChangeKind);

        Assert.IsFalse(session.HasPendingEntryChanges);
        Assert.IsFalse(session.HasUnsavedChanges);
    }

    [TestMethod]
    public async Task DiscardEntryChanges_NewEntry_RemovesEntry()
    {
        await using VaultSession session =
            await CreateSessionAsync();

        VaultEntry entry =
            session.CreateEntry("Unsaved entry");

        session.MarkEntryForDeletion(
            entry.EntryId);

        session.DiscardEntryChanges(
            entry.EntryId);

        Assert.IsFalse(
            session.Entries.Any(
                descriptor =>
                    descriptor.EntryId == entry.EntryId));

        Assert.IsFalse(session.HasPendingEntryChanges);
        Assert.IsFalse(session.HasPendingEntryDeletions);

        Assert.ThrowsExactly<KeyNotFoundException>(
            () => session.GetEntrySessionState(
                entry.EntryId));

        Assert.IsFalse(
            File.Exists(
                GetEntryFilePath(entry.EntryId)));
    }

    [TestMethod]
    public async Task DiscardEntryChanges_ModifiedEntry_RestoresPersistedContent()
    {
        await using VaultSession session =
            await CreateSessionAsync();

        VaultEntry entry =
            session.CreateEntry(
                "Entry",
                fields:
                [
                    CreateTextField("persisted")
                ]);

        await session.SaveAsync();

        VaultEntry persisted =
            await session.GetEntryAsync(
                entry.EntryId);

        session.ReplaceEntry(
            WithText(
                persisted,
                "discard me"));

        Assert.AreEqual(
            EntryChangeKind.Modified,
            session.GetEntrySessionState(
                    entry.EntryId)
                .ChangeKind);

        session.DiscardEntryChanges(
            entry.EntryId);

        VaultEntry restored =
            await session.GetEntryAsync(
                entry.EntryId);

        Assert.AreEqual(
            EntryChangeKind.None,
            session.GetEntrySessionState(
                    entry.EntryId)
                .ChangeKind);

        AssertEntryText(restored, "persisted");

        Assert.IsFalse(session.HasPendingEntryChanges);
        Assert.IsFalse(session.HasUnsavedChanges);
    }

    [TestMethod]
    public async Task GetPersistedEntryAsync_PendingModification_ReturnsSavedCounterpart()
    {
        await using VaultSession session =
            await CreateSessionAsync();

        VaultEntry entry =
            session.CreateEntry(
                "Entry",
                fields:
                [
                    CreateTextField("persisted")
                ]);

        await session.SaveAsync();

        VaultEntry persisted =
            await session.GetEntryAsync(
                entry.EntryId);

        session.ReplaceEntry(
            WithText(
                persisted,
                "working copy"));

        Assert.IsTrue(
            session.HasPendingEntryContentChanges(
                entry.EntryId));

        VaultEntry workingCopy =
            await session.GetEntryAsync(
                entry.EntryId);

        VaultEntry savedCounterpart =
            await session.GetPersistedEntryAsync(
                entry.EntryId);

        AssertEntryText(
            workingCopy,
            "working copy");

        AssertEntryText(
            savedCounterpart,
            "persisted");

        Assert.AreEqual(
            persisted.Revision,
            savedCounterpart.Revision);

        session.DiscardEntryChanges(
            entry.EntryId);

        Assert.IsFalse(
            session.HasPendingEntryContentChanges(
                entry.EntryId));
    }

    [TestMethod]
    public async Task SaveAsync_ModifiedEntry_IncrementsRevisionOnce()
    {
        await using VaultSession session =
            await CreateSessionAsync();

        VaultEntry entry =
            session.CreateEntry(
                "Entry",
                fields:
                [
                    CreateTextField("v1")
                ]);

        await session.SaveAsync();

        VaultEntry persisted =
            await session.GetEntryAsync(
                entry.EntryId);

        session.ReplaceEntry(
            WithText(persisted, "v2"));

        await session.SaveAsync();

        VaultEntry updated =
            await session.GetEntryAsync(
                entry.EntryId);

        EntryDescriptor descriptor =
            session.Entries.Single(
                candidate =>
                    candidate.EntryId == entry.EntryId);

        Assert.AreEqual(2L, updated.Revision);
        Assert.AreEqual(2L, descriptor.Revision);
        Assert.AreEqual(2L, session.ManifestGeneration);

        AssertEntryText(updated, "v2");

        Assert.IsFalse(session.RequiresSaveRetry);
        Assert.IsFalse(session.HasUnsavedChanges);
    }

    [TestMethod]
    public async Task MarkAndUndoEntryDeletion_RestoresCleanState()
    {
        await using VaultSession session =
            await CreateSessionAsync();

        VaultEntry entry =
            session.CreateEntry("Entry");

        await session.SaveAsync();

        long generationBefore =
            session.ManifestGeneration;

        session.MarkEntryForDeletion(
            entry.EntryId);

        Assert.IsTrue(session.IsManifestDirty);
        Assert.IsTrue(session.HasUnsavedChanges);
        Assert.IsTrue(session.HasPendingEntryDeletions);

        Assert.IsTrue(
            session.GetEntrySessionState(
                    entry.EntryId)
                .IsPendingDeletion);

        Assert.ThrowsExactly<InvalidOperationException>(
            () => session.RenameEntry(
                entry.EntryId,
                "Blocked"));

        session.UndoEntryDeletion(
            entry.EntryId);

        Assert.IsFalse(session.IsManifestDirty);
        Assert.IsFalse(session.HasUnsavedChanges);
        Assert.IsFalse(session.HasPendingEntryDeletions);

        await session.SaveAsync();

        Assert.AreEqual(
            generationBefore,
            session.ManifestGeneration);
    }

    [TestMethod]
    public async Task SaveAsync_DeletedEntries_HandlesPersistedAndNewEntries()
    {
        await using VaultSession session =
            await CreateSessionAsync();

        VaultEntry persistedEntry =
            session.CreateEntry(
                "Persisted",
                fields:
                [
                    CreateTextField("persisted contents")
                ]);

        await session.SaveAsync();

        string persistedEntryPath =
            GetEntryFilePath(
                persistedEntry.EntryId);

        Assert.IsTrue(
            File.Exists(persistedEntryPath));

        VaultEntry persistedWorkingCopy =
            await session.GetEntryAsync(
                persistedEntry.EntryId);

        session.ReplaceEntry(
            WithText(
                persistedWorkingCopy,
                "must not be persisted"));

        session.MarkEntryForDeletion(
            persistedEntry.EntryId);

        VaultEntry newEntry =
            session.CreateEntry(
                "Never persisted");

        session.MarkEntryForDeletion(
            newEntry.EntryId);

        await session.SaveAsync();

        Assert.IsFalse(
            session.Entries.Any(
                entry =>
                    entry.EntryId ==
                    persistedEntry.EntryId));

        Assert.IsFalse(
            session.Entries.Any(
                entry =>
                    entry.EntryId ==
                    newEntry.EntryId));

        Assert.IsFalse(
            File.Exists(persistedEntryPath));

        Assert.IsFalse(
            File.Exists(
                GetEntryFilePath(
                    newEntry.EntryId)));

        Assert.IsFalse(session.HasPendingEntryChanges);
        Assert.IsFalse(session.HasPendingEntryDeletions);
        Assert.IsFalse(
            session.HasPendingEntryFileDeletions);

        Assert.IsFalse(session.HasUnsavedChanges);
    }

    [TestMethod]
    public async Task SaveAsync_MultipleEntries_CommitsEveryEntry()
    {
        await using VaultSession session =
            await CreateSessionAsync();

        VaultEntry first =
            session.CreateEntry(
                "First",
                fields:
                [
                    CreateTextField("one")
                ]);

        VaultEntry second =
            session.CreateEntry(
                "Second",
                fields:
                [
                    CreateTextField("two")
                ]);

        await session.SaveAsync();

        Assert.AreEqual(2, session.Entries.Count);

        Assert.IsTrue(
            session.Entries.All(
                descriptor =>
                    descriptor.Revision == 1));

        AssertEntryText(
            await session.GetEntryAsync(
                first.EntryId),
            "one");

        AssertEntryText(
            await session.GetEntryAsync(
                second.EntryId),
            "two");

        Assert.IsFalse(session.HasUnsavedChanges);
    }

    [TestMethod]
    public async Task SaveAndOpen_Blob_RoundTripsFromEncryptedFile()
    {
        Guid entryId;
        Guid blobId = Guid.NewGuid();
        byte[] plaintext = CreateBlobPlaintext(marker: 0x31);

        try
        {
            await using (VaultSession session =
                         await CreateSessionAsync())
            {
                VaultEntry entry =
                    session.CreateEntry("Image entry");

                entryId = entry.EntryId;

                session.ReplaceEntryWithBlob(
                    WithBlob(entry, blobId, plaintext.Length),
                    blobId,
                    plaintext);

                using (SensitiveBuffer staged =
                       await session.GetBlobAsync(
                           entryId,
                           blobId,
                           plaintext.Length))
                {
                    await AssertBufferEqualsAsync(
                        plaintext,
                        staged);
                }

                await session.SaveAsync();

                string blobPath = GetBlobFilePath(blobId);
                Assert.IsTrue(File.Exists(blobPath));

                byte[] storedBytes =
                    await File.ReadAllBytesAsync(blobPath);

                Assert.IsFalse(
                    storedBytes.AsSpan().IndexOf(plaintext) >= 0,
                    "The encrypted blob file contained plaintext bytes.");
            }

            await using VaultSession reopened =
                await VaultSession.OpenAsync(
                    _vaultDirectory,
                    Password);

            VaultEntry restored =
                await reopened.GetEntryAsync(entryId);

            BlobFieldValue restoredReference =
                (BlobFieldValue)restored.Fields.Single().Value;

            Assert.AreEqual(blobId, restoredReference.BlobId);
            Assert.AreEqual("image/png", restoredReference.ContentType);
            Assert.AreEqual(
                plaintext.LongLength,
                restoredReference.Length);

            using SensitiveBuffer restoredBlob =
                await reopened.GetBlobAsync(
                    entryId,
                    blobId,
                    plaintext.Length);

            await AssertBufferEqualsAsync(
                plaintext,
                restoredBlob);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    [TestMethod]
    public async Task SaveAsync_ReplacedBlob_DeletesOldEncryptedFile()
    {
        byte[] originalPlaintext =
            CreateBlobPlaintext(marker: 0x41);

        byte[] replacementPlaintext =
            CreateBlobPlaintext(marker: 0x61);

        Guid originalBlobId = Guid.NewGuid();
        Guid replacementBlobId = Guid.NewGuid();

        try
        {
            await using VaultSession session =
                await CreateSessionAsync();

            VaultEntry entry =
                session.CreateEntry("Image entry");

            session.ReplaceEntryWithBlob(
                WithBlob(
                    entry,
                    originalBlobId,
                    originalPlaintext.Length),
                originalBlobId,
                originalPlaintext);

            await session.SaveAsync();

            Assert.IsTrue(
                File.Exists(GetBlobFilePath(originalBlobId)));

            VaultEntry persisted =
                await session.GetEntryAsync(entry.EntryId);

            session.ReplaceEntryWithBlob(
                WithBlob(
                    persisted,
                    replacementBlobId,
                    replacementPlaintext.Length),
                replacementBlobId,
                replacementPlaintext);

            await session.SaveAsync();

            Assert.IsFalse(
                File.Exists(GetBlobFilePath(originalBlobId)));

            Assert.IsTrue(
                File.Exists(GetBlobFilePath(replacementBlobId)));

            using SensitiveBuffer restored =
                await session.GetBlobAsync(
                    entry.EntryId,
                    replacementBlobId,
                    replacementPlaintext.Length);

            await AssertBufferEqualsAsync(
                replacementPlaintext,
                restored);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(originalPlaintext);
            CryptographicOperations.ZeroMemory(replacementPlaintext);
        }
    }

    [TestMethod]
    public async Task DiscardEntryChanges_StagedBlob_RestoresSavedBlob()
    {
        byte[] savedPlaintext =
            CreateBlobPlaintext(marker: 0x51);

        byte[] stagedPlaintext =
            CreateBlobPlaintext(marker: 0x71);

        Guid savedBlobId = Guid.NewGuid();
        Guid stagedBlobId = Guid.NewGuid();

        try
        {
            await using VaultSession session =
                await CreateSessionAsync();

            VaultEntry entry =
                session.CreateEntry("Image entry");

            session.ReplaceEntryWithBlob(
                WithBlob(
                    entry,
                    savedBlobId,
                    savedPlaintext.Length),
                savedBlobId,
                savedPlaintext);

            await session.SaveAsync();

            VaultEntry persisted =
                await session.GetEntryAsync(entry.EntryId);

            session.ReplaceEntryWithBlob(
                WithBlob(
                    persisted,
                    stagedBlobId,
                    stagedPlaintext.Length),
                stagedBlobId,
                stagedPlaintext);

            session.DiscardEntryChanges(entry.EntryId);

            VaultEntry restoredEntry =
                await session.GetEntryAsync(entry.EntryId);

            BlobFieldValue restoredReference =
                (BlobFieldValue)
                    restoredEntry.Fields.Single().Value;

            Assert.AreEqual(savedBlobId, restoredReference.BlobId);
            Assert.IsFalse(File.Exists(GetBlobFilePath(stagedBlobId)));

            using SensitiveBuffer restoredBlob =
                await session.GetBlobAsync(
                    entry.EntryId,
                    savedBlobId,
                    savedPlaintext.Length);

            await AssertBufferEqualsAsync(
                savedPlaintext,
                restoredBlob);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(savedPlaintext);
            CryptographicOperations.ZeroMemory(stagedPlaintext);
        }
    }

    [TestMethod]
    public async Task SaveAsync_DeletedEntry_DeletesReferencedBlob()
    {
        byte[] plaintext = CreateBlobPlaintext(marker: 0x21);
        Guid blobId = Guid.NewGuid();

        try
        {
            await using VaultSession session =
                await CreateSessionAsync();

            VaultEntry entry =
                session.CreateEntry("Image entry");

            session.ReplaceEntryWithBlob(
                WithBlob(entry, blobId, plaintext.Length),
                blobId,
                plaintext);

            await session.SaveAsync();

            Assert.IsTrue(File.Exists(GetBlobFilePath(blobId)));

            session.MarkEntryForDeletion(entry.EntryId);
            await session.SaveAsync();

            Assert.IsFalse(File.Exists(GetBlobFilePath(blobId)));
            Assert.IsFalse(
                File.Exists(GetEntryFilePath(entry.EntryId)));
            Assert.IsFalse(session.HasPendingBlobFileDeletions);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    [TestMethod]
    public async Task MetadataChanges_RoundTripWithoutIncrementingEntryRevision()
    {
        Guid entryId;
        Guid destinationFolderId;
        Guid retainedTagId;
        DateTimeOffset createdUtc;
        DateTimeOffset modifiedUtc;
        DateOnly timelineDate = new(1998, 4, 16);

        await using (VaultSession session =
                     await CreateSessionAsync())
        {
            FolderDescriptor source =
                session.CreateFolder("Source");

            FolderDescriptor destination =
                session.CreateFolder("Destination");

            TagDescriptor removedTag =
                session.CreateTag("Remove me");

            TagDescriptor retainedTag =
                session.CreateTag("Old name");

            VaultEntry entry =
                session.CreateEntry(
                    "Old entry name",
                    source.FolderId,
                    [removedTag.TagId]);

            await session.SaveAsync();

            EntryDescriptor initiallySavedDescriptor =
                session.Entries.Single(descriptor =>
                    descriptor.EntryId == entry.EntryId);

            createdUtc = initiallySavedDescriptor.CreatedUtc;
            modifiedUtc = initiallySavedDescriptor.ModifiedUtc;

            session.RenameEntry(
                entry.EntryId,
                "New entry name");

            session.MoveEntry(
                entry.EntryId,
                destination.FolderId);

            Assert.AreEqual(
                EntryChangeKind.Modified,
                session.GetEntrySessionState(
                        entry.EntryId)
                    .ChangeKind);

            session.AddTagToEntry(
                entry.EntryId,
                retainedTag.TagId);

            session.RemoveTagFromEntry(
                entry.EntryId,
                removedTag.TagId);

            session.RenameFolder(
                destination.FolderId,
                "Renamed destination");

            session.RenameTag(
                retainedTag.TagId,
                "Retained");

            session.SetTagColor(
                retainedTag.TagId,
                "#123456");

            session.SetEntryTimelineDate(
                entry.EntryId,
                timelineDate);

            await session.SaveAsync();

            Assert.AreEqual(
                EntryChangeKind.None,
                session.GetEntrySessionState(
                        entry.EntryId)
                    .ChangeKind);

            entryId = entry.EntryId;
            destinationFolderId = destination.FolderId;
            retainedTagId = retainedTag.TagId;

            Assert.AreEqual(
                1L,
                session.Entries.Single(
                        descriptor =>
                            descriptor.EntryId == entryId)
                    .Revision);
        }

        await using VaultSession reopened =
            await VaultSession.OpenAsync(
                _vaultDirectory,
                Password);

        EntryDescriptor descriptor =
            reopened.Entries.Single(
                entry => entry.EntryId == entryId);

        Assert.AreEqual(
            "New entry name",
            descriptor.Name);

        Assert.AreEqual(
            destinationFolderId,
            descriptor.FolderId);

        // Metadata-only changes do not rewrite the entry file.
        Assert.AreEqual(
            1L,
            descriptor.Revision);

        Assert.AreEqual(createdUtc, descriptor.CreatedUtc);
        Assert.AreEqual(modifiedUtc, descriptor.ModifiedUtc);
        Assert.AreEqual(
            timelineDate,
            descriptor.TimelineDateOverride);
        Assert.AreEqual(
            timelineDate,
            descriptor.EffectiveTimelineDate);

        CollectionAssert.AreEqual(
            new[] { retainedTagId },
            descriptor.TagIds.ToArray());

        Assert.AreEqual(
            "Renamed destination",
            reopened.Folders.Single(
                    folder =>
                        folder.FolderId == destinationFolderId)
                .Name);

        TagDescriptor reopenedRetainedTag =
            reopened.Tags.Single(
                tag => tag.TagId == retainedTagId);

        Assert.AreEqual(
            "Retained",
            reopenedRetainedTag.Name);

        Assert.AreEqual(
            "#123456",
            reopenedRetainedTag.Color);

        Assert.AreEqual(
            entryId,
            reopened.Index
                .EntriesByFolderId[destinationFolderId]
                .Single()
                .EntryId);

        Assert.AreEqual(
            entryId,
            reopened.Index
                .EntriesByTagId[retainedTagId]
                .Single()
                .EntryId);
    }

    [TestMethod]
    public async Task EntryFieldsAndTagChanges_RoundTripInDisplayedOrder()
    {
        Guid entryId;
        Guid tagId;
        Guid usernameFieldId = Guid.NewGuid();
        Guid notesFieldId = Guid.NewGuid();

        await using (VaultSession session =
                     await CreateSessionAsync())
        {
            TagDescriptor tag =
                session.CreateTag("Login");

            VaultEntry entry =
                session.CreateEntry(
                    "Account",
                    fields:
                    [
                        new EntryField(
                            notesFieldId,
                            "Notes",
                            new TextFieldValue(
                                "first")),

                        new EntryField(
                            usernameFieldId,
                            "Username",
                            new TextFieldValue(
                                "adrian"))
                    ]);

            await session.SaveAsync();

            VaultEntry persisted =
                await session.GetEntryAsync(
                    entry.EntryId);

            session.ReplaceEntry(
                new VaultEntry(
                    persisted.SchemaVersion,
                    persisted.EntryId,
                    persisted.Revision,
                    [
                        new EntryField(
                            usernameFieldId,
                            "Username",
                            new TextFieldValue(
                                "updated user")),

                        new EntryField(
                            notesFieldId,
                            "Recovery notes",
                            new TextFieldValue(
                                "an arbitrary amount of text"))
                    ]));

            session.AddTagToEntry(
                entry.EntryId,
                tag.TagId);

            Assert.AreEqual(
                EntryChangeKind.Modified,
                session.GetEntrySessionState(
                        entry.EntryId)
                    .ChangeKind);

            await session.SaveAsync();

            entryId = entry.EntryId;
            tagId = tag.TagId;
        }

        await using VaultSession reopened =
            await VaultSession.OpenAsync(
                _vaultDirectory,
                Password);

        EntryDescriptor descriptor =
            reopened.Entries.Single(
                entry => entry.EntryId == entryId);

        CollectionAssert.AreEqual(
            new[] { tagId },
            descriptor.TagIds.ToArray());

        VaultEntry restored =
            await reopened.GetEntryAsync(
                entryId);

        CollectionAssert.AreEqual(
            new[]
            {
                usernameFieldId,
                notesFieldId
            },
            restored.Fields
                .Select(field => field.FieldId)
                .ToArray());

        Assert.AreEqual(
            "Username",
            restored.Fields[0].Name);

        Assert.AreEqual(
            "updated user",
            ((TextFieldValue)
                restored.Fields[0].Value).Text);

        Assert.AreEqual(
            "Recovery notes",
            restored.Fields[1].Name);

        Assert.AreEqual(
            "an arbitrary amount of text",
            ((TextFieldValue)
                restored.Fields[1].Value).Text);
    }

    [TestMethod]
    public async Task SaveAsync_NoChanges_DoesNotAdvanceGeneration()
    {
        await using VaultSession session =
            await CreateSessionAsync();

        await session.SaveAsync();
        await session.SaveAsync();

        Assert.AreEqual(
            0L,
            session.ManifestGeneration);

        Assert.IsFalse(session.HasUnsavedChanges);
    }

    [TestMethod]
    public async Task ChangePasswordAsync_RequiresCleanSessionAndRotatesVault()
    {
        Guid vaultId;
        Guid entryId;
        DateOnly timelineDate = new(1973, 11, 5);

        await using (VaultSession session =
                     await CreateSessionAsync())
        {
            VaultEntry entry =
                session.CreateEntry("Entry");

            vaultId = session.VaultId;
            entryId = entry.EntryId;

            session.SetEntryTimelineDate(
                entryId,
                timelineDate);

            await Assert.ThrowsExactlyAsync<
                InvalidOperationException>(
                () => session.ChangePasswordAsync(
                    NewPassword,
                    ChangedKdfParameters));

            await session.SaveAsync();

            long generationBefore =
                session.ManifestGeneration;

            await session.ChangePasswordAsync(
                NewPassword,
                ChangedKdfParameters);

            AssertKdfParametersEqual(
                ChangedKdfParameters,
                session.PasswordKdfParameters);

            Assert.AreEqual(
                generationBefore + 1,
                session.ManifestGeneration);
        }

        await Assert.ThrowsExactlyAsync<CryptographicException>(
            () => VaultSession.OpenAsync(
                _vaultDirectory,
                Password));

        await using VaultSession reopened =
            await VaultSession.OpenAsync(
                _vaultDirectory,
                NewPassword);

        Assert.AreEqual(vaultId, reopened.VaultId);

        AssertKdfParametersEqual(
            ChangedKdfParameters,
            reopened.PasswordKdfParameters);

        Assert.IsTrue(
            reopened.Entries.Any(
                descriptor =>
                    descriptor.EntryId == entryId &&
                    descriptor.TimelineDateOverride ==
                        timelineDate));
    }

    [TestMethod]
    public async Task ExtendedLatinPasswords_CreateOpenAndChange_RoundTripExactly()
    {
        const string OriginalPassword =
            "Pădure-Șarpe-Ärger-ß";

        const string ChangedPassword =
            "Țară-Über-Œuvre-Łódź";

        await using (VaultSession created =
                     await VaultSession.CreateAsync(
                         _vaultDirectory,
                         OriginalPassword,
                         TestKdfParameters))
        {
            Assert.AreNotEqual(
                Guid.Empty,
                created.VaultId);
        }

        await using (VaultSession opened =
                     await VaultSession.OpenAsync(
                         _vaultDirectory,
                         OriginalPassword))
        {
            await opened.ChangePasswordAsync(
                ChangedPassword,
                ChangedKdfParameters);
        }

        await Assert.ThrowsExactlyAsync<
            CryptographicException>(
                () => VaultSession.OpenAsync(
                    _vaultDirectory,
                    OriginalPassword));

        await using VaultSession reopened =
            await VaultSession.OpenAsync(
                _vaultDirectory,
                ChangedPassword);

        AssertKdfParametersEqual(
            ChangedKdfParameters,
            reopened.PasswordKdfParameters);
    }

    [TestMethod]
    public async Task DisposeAsync_IsIdempotentAndRejectsFurtherUse()
    {
        VaultSession session =
            await CreateSessionAsync();

        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.ThrowsExactly<ObjectDisposedException>(
            () => session.CreateFolder("Blocked"));

        Assert.ThrowsExactly<ObjectDisposedException>(
            () =>
            {
                _ = session.HasUnsavedChanges;
            });

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            () => session.SaveAsync());
    }

    private Task<VaultSession> CreateSessionAsync()
    {
        return VaultSession.CreateAsync(
            _vaultDirectory,
            Password,
            TestKdfParameters);
    }

    private static Argon2idParameters TestKdfParameters =>
        new()
        {
            Version =
                Argon2idParameters.SupportedVersion,

            // Smallest parameters currently accepted.
            MemorySizeKiB = 19 * 1024,
            Iterations = 2,
            DegreeOfParallelism = 1
        };

    private static Argon2idParameters ChangedKdfParameters =>
        new()
        {
            Version =
                Argon2idParameters.SupportedVersion,

            MemorySizeKiB = 20 * 1024,
            Iterations = 3,
            DegreeOfParallelism = 2
        };

    private static void AssertKdfParametersEqual(
        Argon2idParameters expected,
        Argon2idParameters actual)
    {
        Assert.AreEqual(
            expected.Version,
            actual.Version);

        Assert.AreEqual(
            expected.MemorySizeKiB,
            actual.MemorySizeKiB);

        Assert.AreEqual(
            expected.Iterations,
            actual.Iterations);

        Assert.AreEqual(
            expected.DegreeOfParallelism,
            actual.DegreeOfParallelism);
    }

    private static EntryField CreateTextField(
        string text)
    {
        return new EntryField(
            Guid.NewGuid(),
            "Text",
            new TextFieldValue(text));
    }

    private static VaultEntry WithText(
        VaultEntry entry,
        string text)
    {
        return new VaultEntry(
            entry.SchemaVersion,
            entry.EntryId,
            entry.Revision,
            [CreateTextField(text)]);
    }

    private static VaultEntry WithBlob(
        VaultEntry entry,
        Guid blobId,
        int length)
    {
        return new VaultEntry(
            entry.SchemaVersion,
            entry.EntryId,
            entry.Revision,
            [
                new EntryField(
                    Guid.NewGuid(),
                    "Image",
                    new BlobFieldValue(
                        blobId,
                        "image.png",
                        "image/png",
                        length))
            ]);
    }

    private static byte[] CreateBlobPlaintext(byte marker)
    {
        byte[] plaintext = new byte[257];

        for (int index = 0; index < plaintext.Length; index++)
        {
            plaintext[index] = (byte)(marker + index);
        }

        return plaintext;
    }

    private static async Task AssertBufferEqualsAsync(
        byte[] expected,
        SensitiveBuffer actual)
    {
        Assert.AreEqual(expected.Length, actual.Length);

        byte[] restored = new byte[actual.Length];

        try
        {
            using Stream stream = actual.OpenReadStream();
            await stream.ReadExactlyAsync(restored);
            CollectionAssert.AreEqual(expected, restored);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(restored);
        }
    }

    private static void AssertEntryText(
        VaultEntry entry,
        string expectedText)
    {
        EntryField field =
            entry.Fields.Single();

        TextFieldValue value =
            (TextFieldValue)field.Value;

        Assert.AreEqual(
            expectedText,
            value.Text);
    }

    private string GetVaultFilePath()
    {
        return Path.Combine(
            _vaultDirectory,
            VaultFileStore.VaultFileName);
    }

    private string GetEntryFilePath(
        Guid entryId)
    {
        return Path.Combine(
            _vaultDirectory,
            EntryFileStore.EntriesDirectoryName,
            entryId.ToString("D") +
            EntryFileStore.EntryFileExtension);
    }

    private string GetBlobFilePath(Guid blobId)
    {
        return Path.Combine(
            _vaultDirectory,
            BlobFileStore.BlobsDirectoryName,
            blobId.ToString("D") +
            BlobFileStore.BlobFileExtension);
    }
}
