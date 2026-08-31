using System.Security.Cryptography;
using System.Text;
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
public sealed class VaultKeyRotationTests
{
    private const string OldPassword =
        "old correct horse battery staple";

    private const string NewPassword =
        "new correct horse battery staple";

    private string _testRoot = null!;
    private string _vaultDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _testRoot = Path.Combine(
            Path.GetTempPath(),
            "Cripty.Application.Tests",
            Guid.NewGuid().ToString("N"));

        _vaultDirectory =
            Path.Combine(_testRoot, "Vault");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(
                _testRoot,
                recursive: true);
        }
    }

    [TestMethod]
    public async Task ChangePasswordAsync_ReencryptsEverythingWithFreshRootKey()
    {
        byte[] blobPlaintext =
            Encoding.UTF8.GetBytes(
                "sensitive image bytes that must stay in memory");

        byte[] oldRootKey =
            new byte[VaultRootKeyGenerator.KeySize];

        byte[] newRootKey =
            new byte[VaultRootKeyGenerator.KeySize];

        Guid vaultId;
        Guid parentFolderId;
        Guid emptyFolderId;
        Guid unusedTagId;
        Guid textEntryId;
        Guid blobEntryId;
        Guid blobId = Guid.NewGuid();
        long generationAfterRotation;

        List<VaultPasswordChangeProgress> progressReports = [];

        VaultFileStore vaultFileStore = new();
        EntryFileStore entryFileStore = new();
        BlobFileStore blobFileStore = new();
        VaultFileCodec vaultFileCodec = new();
        EntryFileCodec entryFileCodec = new();
        BlobFileCodec blobFileCodec = new();

        try
        {
            await using (VaultSession session =
                         await VaultSession.CreateAsync(
                             _vaultDirectory,
                             OldPassword,
                             TestKdfParameters))
            {
                FolderDescriptor parentFolder =
                    session.CreateFolder("Accounts");

                FolderDescriptor emptyFolder =
                    session.CreateFolder(
                        "Unused empty folder",
                        parentFolder.FolderId);

                TagDescriptor usedTag =
                    session.CreateTag(
                        "Used tag",
                        "#112233");

                TagDescriptor unusedTag =
                    session.CreateTag(
                        "Unused tag",
                        "#abcdef");

                VaultEntry textEntry =
                    session.CreateEntry(
                        "Login",
                        parentFolder.FolderId,
                        [usedTag.TagId],
                        [
                            new EntryField(
                                Guid.NewGuid(),
                                "Password",
                                new TextFieldValue("top secret"))
                        ]);

                VaultEntry blobEntry =
                    session.CreateEntry("Passport scan");

                session.ReplaceEntryWithBlob(
                    new VaultEntry(
                        blobEntry.SchemaVersion,
                        blobEntry.EntryId,
                        blobEntry.Revision,
                        [
                            new EntryField(
                                Guid.NewGuid(),
                                "Image",
                                new BlobFieldValue(
                                    blobId,
                                    "passport.png",
                                    "image/png",
                                    blobPlaintext.LongLength))
                        ]),
                    blobId,
                    blobPlaintext);

                await session.SaveAsync();

                session.SetAllEntriesSortMode(
                    EntrySortMode.TimelineNewest);

                session.SetRootSortMode(
                    EntrySortMode.CreatedOldest);

                session.SetFolderSortMode(
                    parentFolder.FolderId,
                    EntrySortMode.NameAscending);

                await session.SaveAsync();

                vaultId = session.VaultId;
                parentFolderId = parentFolder.FolderId;
                emptyFolderId = emptyFolder.FolderId;
                unusedTagId = unusedTag.TagId;
                textEntryId = textEntry.EntryId;
                blobEntryId = blobEntry.EntryId;

                long generationBeforeRotation =
                    session.ManifestGeneration;

                VaultFile oldVaultFile =
                    await vaultFileStore.ReadAsync(
                        _vaultDirectory);

                _ = vaultFileCodec.Open(
                    oldVaultFile,
                    OldPassword,
                    oldRootKey);

                await session.ChangePasswordAsync(
                    NewPassword,
                    ChangedKdfParameters,
                    new InlineProgress<VaultPasswordChangeProgress>(
                        progressReports.Add));

                generationAfterRotation =
                    session.ManifestGeneration;

                AssertPasswordChangeProgress(progressReports);

                Assert.AreEqual(
                    generationBeforeRotation + 1,
                    generationAfterRotation);

                Assert.AreEqual(vaultId, session.VaultId);

                Assert.AreEqual(
                    EntrySortMode.TimelineNewest,
                    session.AllEntriesSortMode);

                Assert.AreEqual(
                    EntrySortMode.CreatedOldest,
                    session.RootSortMode);

                Assert.AreEqual(
                    EntrySortMode.NameAscending,
                    session.GetFolderSortMode(
                        parentFolderId));

                Assert.IsTrue(
                    session.Folders.Any(folder =>
                        folder.FolderId == emptyFolderId &&
                        folder.ParentFolderId == parentFolderId &&
                        folder.Name == "Unused empty folder"));

                Assert.IsTrue(
                    session.Tags.Any(tag =>
                        tag.TagId == unusedTagId &&
                        tag.Name == "Unused tag" &&
                        tag.Color == "#abcdef"));

                VaultEntry restoredTextEntry =
                    await session.GetEntryAsync(textEntryId);

                Assert.AreEqual(
                    "top secret",
                    ((TextFieldValue)
                        restoredTextEntry.Fields.Single().Value).Text);

                using (SensitiveBuffer restoredBlob =
                       await session.GetBlobAsync(
                           blobEntryId,
                           blobId,
                           blobPlaintext.LongLength))
                {
                    await AssertBufferEqualsAsync(
                        blobPlaintext,
                        restoredBlob);
                }

                VaultFile rotatedVaultFile =
                    await vaultFileStore.ReadAsync(
                        _vaultDirectory);

                _ = vaultFileCodec.Open(
                    rotatedVaultFile,
                    NewPassword,
                    newRootKey);

                Assert.IsFalse(
                    CryptographicOperations.FixedTimeEquals(
                        oldRootKey,
                        newRootKey));

                EntryFile rotatedExistingEntryFile =
                    await entryFileStore.ReadAsync(
                        _vaultDirectory,
                        textEntryId);

                Assert.ThrowsExactly<CryptographicException>(() =>
                    entryFileCodec.Open(
                        rotatedExistingEntryFile,
                        oldRootKey));

                BlobFile rotatedBlobFile =
                    await blobFileStore.ReadAsync(
                        _vaultDirectory,
                        blobId);

                Assert.ThrowsExactly<CryptographicException>(() =>
                    blobFileCodec.Open(
                        rotatedBlobFile,
                        oldRootKey));

                VaultEntry futureEntry =
                    session.CreateEntry(
                        "Created after password change",
                        fields:
                        [
                            new EntryField(
                                Guid.NewGuid(),
                                "Secret",
                                new TextFieldValue("future secret"))
                        ]);

                await session.SaveAsync();

                EntryFile futureEntryFile =
                    await entryFileStore.ReadAsync(
                        _vaultDirectory,
                        futureEntry.EntryId);

                Assert.ThrowsExactly<CryptographicException>(() =>
                    entryFileCodec.Open(
                        futureEntryFile,
                        oldRootKey));
            }

            await Assert.ThrowsExactlyAsync<CryptographicException>(
                () => VaultSession.OpenAsync(
                    _vaultDirectory,
                    OldPassword));

            await using VaultSession reopened =
                await VaultSession.OpenAsync(
                    _vaultDirectory,
                    NewPassword);

            Assert.AreEqual(vaultId, reopened.VaultId);

            Assert.AreEqual(
                EntrySortMode.TimelineNewest,
                reopened.AllEntriesSortMode);

            Assert.AreEqual(
                EntrySortMode.CreatedOldest,
                reopened.RootSortMode);

            Assert.AreEqual(
                EntrySortMode.NameAscending,
                reopened.GetFolderSortMode(
                    parentFolderId));
            Assert.IsTrue(
                reopened.ManifestGeneration >
                generationAfterRotation);

            Assert.IsTrue(
                reopened.Folders.Any(folder =>
                    folder.FolderId == emptyFolderId &&
                    folder.ParentFolderId == parentFolderId));

            Assert.IsTrue(
                reopened.Tags.Any(tag =>
                    tag.TagId == unusedTagId));

            Assert.IsTrue(
                reopened.Entries.Any(entry =>
                    entry.EntryId == textEntryId));

            using SensitiveBuffer reopenedBlob =
                await reopened.GetBlobAsync(
                    blobEntryId,
                    blobId,
                    blobPlaintext.LongLength);

            await AssertBufferEqualsAsync(
                blobPlaintext,
                reopenedBlob);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blobPlaintext);
            CryptographicOperations.ZeroMemory(oldRootKey);
            CryptographicOperations.ZeroMemory(newRootKey);
        }
    }

    private static Argon2idParameters TestKdfParameters =>
        new()
        {
            Version =
                Argon2idParameters.SupportedVersion,
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

    private static async Task AssertBufferEqualsAsync(
        byte[] expected,
        SensitiveBuffer actual)
    {
        byte[] restored = new byte[actual.Length];

        try
        {
            using Stream stream = actual.OpenReadStream();
            await stream.ReadExactlyAsync(restored);

            CollectionAssert.AreEqual(
                expected,
                restored);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(restored);
        }
    }

    private static void AssertPasswordChangeProgress(
        IReadOnlyList<VaultPasswordChangeProgress> reports)
    {
        Assert.IsTrue(reports.Count >= 5);

        Assert.AreEqual(
            0d,
            reports[0].Percentage);

        Assert.AreEqual(
            VaultPasswordChangeStage.GeneratingRootKey,
            reports[0].Stage);

        Assert.IsTrue(reports.Any(report =>
            report.Percentage == 10 &&
            report.Stage ==
                VaultPasswordChangeStage.PreparingVault));

        Assert.IsTrue(reports.Any(report =>
            report.Percentage > 10 &&
            report.Percentage < 95 &&
            report.Stage ==
                VaultPasswordChangeStage.ReencryptingContent));

        Assert.IsTrue(reports.Any(report =>
            report.Percentage == 95 &&
            report.Stage ==
                VaultPasswordChangeStage.Verifying));

        Assert.AreEqual(
            100d,
            reports[^1].Percentage);

        Assert.AreEqual(
            VaultPasswordChangeStage.Completed,
            reports[^1].Stage);

        for (int index = 1; index < reports.Count; index++)
        {
            Assert.IsTrue(
                reports[index].Percentage >=
                reports[index - 1].Percentage,
                "Password-change progress must not move backwards.");
        }
    }

    private sealed class InlineProgress<T>(
        Action<T> report) : IProgress<T>
    {
        public void Report(T value)
        {
            report(value);
        }
    }
}
