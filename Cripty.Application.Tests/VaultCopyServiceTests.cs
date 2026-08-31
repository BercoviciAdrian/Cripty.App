using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Cripty.Application.Vaults;
using Cripty.Core.Entries;
using Cripty.Core.Vaults;
using Cripty.Cryptography.Keys;

namespace Cripty.Application.Tests;

[TestClass]
[DoNotParallelize]
public sealed class VaultCopyServiceTests
{
    private const string Password =
        "correct horse battery staple";

    private string _testDirectory = null!;
    private string _sourceDirectory = null!;
    private string _destinationDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _testDirectory = Path.Combine(
            Path.GetTempPath(),
            "Cripty.Application.Tests",
            Guid.NewGuid().ToString("N"));

        _sourceDirectory =
            Path.Combine(_testDirectory, "Source");

        _destinationDirectory =
            Path.Combine(_testDirectory, "Destination");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(
                _testDirectory,
                recursive: true);
        }
    }

    [TestMethod]
    public async Task CopyAsync_MixedSelection_CopiesOneBulkGeneration()
    {
        byte[] imageBytes =
            CreateBlobPlaintext();

        try
        {
            Guid sourceRootEntryId;
            Guid sourceImageEntryId;
            Guid sourceImageFieldId;
            Guid sourceBlobId;
            Guid selectedFolderId;
            DateTimeOffset sourceCreatedUtc;
            DateTimeOffset sourceModifiedUtc;
            DateOnly sourceTimelineDate = new(1987, 9, 3);
            int sourceEntryCount;

            await using VaultSession source =
                await CreateVaultAsync(_sourceDirectory);

            FolderDescriptor parent =
                source.CreateFolder("Accounts");

            FolderDescriptor child =
                source.CreateFolder(
                    "Personal",
                    parent.FolderId);

            selectedFolderId = child.FolderId;

            TagDescriptor sharedTag =
                source.CreateTag(
                    "Shared",
                    "#112233");

            TagDescriptor imageTag =
                source.CreateTag(
                    "Images",
                    "#abcdef");

            VaultEntry rootEntry =
                source.CreateEntry(
                    "Root secret",
                    tagIds: [sharedTag.TagId],
                    fields: [CreateTextField("root text")]);

            sourceRootEntryId = rootEntry.EntryId;

            sourceBlobId = Guid.NewGuid();
            sourceImageFieldId = Guid.NewGuid();

            VaultEntry imageEntry =
                source.CreateEntry(
                    "Passport",
                    child.FolderId,
                    [sharedTag.TagId, imageTag.TagId]);

            sourceImageEntryId = imageEntry.EntryId;

            source.SetEntryTimelineDate(
                sourceImageEntryId,
                sourceTimelineDate);

            source.ReplaceEntryWithBlob(
                new VaultEntry(
                    imageEntry.SchemaVersion,
                    imageEntry.EntryId,
                    imageEntry.Revision,
                    [
                        new EntryField(
                            sourceImageFieldId,
                            "Scan",
                            new BlobFieldValue(
                                sourceBlobId,
                                "passport.png",
                                "image/png",
                                imageBytes.LongLength))
                    ]),
                sourceBlobId,
                imageBytes);

            VaultEntry pendingDeletion =
                source.CreateEntry(
                    "Do not copy",
                    child.FolderId,
                    fields: [CreateTextField("deleted")]);

            await source.SaveAsync();

            source.MarkEntryForDeletion(
                pendingDeletion.EntryId);

            await source.SaveAsync();

            EntryDescriptor sourceImageDescriptor =
                source.Entries.Single(entry =>
                    entry.EntryId == sourceImageEntryId);

            sourceCreatedUtc =
                sourceImageDescriptor.CreatedUtc;

            sourceModifiedUtc =
                sourceImageDescriptor.ModifiedUtc;

            sourceEntryCount = source.Entries.Count;
            long sourceGeneration =
                source.ManifestGeneration;

            Guid existingDestinationEntryId;
            Guid existingSharedTagId;
            Guid existingParentFolderId;

            await using (VaultSession destination =
                         await CreateVaultAsync(
                             _destinationDirectory))
            {
                FolderDescriptor existingParent =
                    destination.CreateFolder("Accounts");

                existingParentFolderId =
                    existingParent.FolderId;

                TagDescriptor existingSharedTag =
                    destination.CreateTag(
                        "shared",
                        "#destination");

                existingSharedTagId =
                    existingSharedTag.TagId;

                VaultEntry duplicate =
                    destination.CreateEntry(
                        "Root secret",
                        tagIds: [existingSharedTag.TagId],
                        fields: [CreateTextField("existing")]);

                existingDestinationEntryId =
                    duplicate.EntryId;

                await destination.SaveAsync();
                Assert.AreEqual(
                    1L,
                    destination.ManifestGeneration);
            }

            VaultCopyResult result =
                await new VaultCopyService().CopyAsync(
                    source,
                    _destinationDirectory,
                    Password,
                    selectedEntryIds:
                    [
                        sourceRootEntryId,
                        // Deliberately duplicated by the folder selection.
                        sourceImageEntryId
                    ],
                    selectedFolderIds: [selectedFolderId]);

            Assert.AreEqual(2, result.EntryCount);
            Assert.AreEqual(1, result.BlobCount);
            Assert.AreEqual(1, result.CreatedFolderCount);
            Assert.AreEqual(1, result.CreatedTagCount);

            Assert.AreEqual(sourceGeneration, source.ManifestGeneration);
            Assert.HasCount(sourceEntryCount, source.Entries);
            Assert.IsFalse(source.HasPendingEntryDeletions);

            await using VaultSession copied =
                await VaultSession.OpenAsync(
                    _destinationDirectory,
                    Password);

            // The pre-existing destination save was generation one;
            // the entire copy is published as generation two.
            Assert.AreEqual(2L, copied.ManifestGeneration);
            Assert.HasCount(3, copied.Entries);

            EntryDescriptor renamedRoot =
                copied.Entries.Single(entry =>
                    entry.EntryId != existingDestinationEntryId &&
                    entry.Name.StartsWith(
                        "Root secret ",
                        StringComparison.Ordinal));

            Assert.IsTrue(
                Regex.IsMatch(
                    renamedRoot.Name,
                    "^Root secret [A-Za-z0-9+/]{8}$"));

            Assert.IsNull(renamedRoot.FolderId);

            FolderDescriptor copiedParent =
                copied.Folders.Single(folder =>
                    folder.Name == "Accounts");

            Assert.AreEqual(
                existingParentFolderId,
                copiedParent.FolderId);

            FolderDescriptor copiedChild =
                copied.Folders.Single(folder =>
                    folder.Name == "Personal");

            Assert.AreEqual(
                copiedParent.FolderId,
                copiedChild.ParentFolderId);

            EntryDescriptor copiedImageDescriptor =
                copied.Entries.Single(entry =>
                    entry.Name == "Passport");

            Assert.AreNotEqual(
                sourceImageEntryId,
                copiedImageDescriptor.EntryId);

            Assert.AreEqual(
                copiedChild.FolderId,
                copiedImageDescriptor.FolderId);

            Assert.AreEqual(
                sourceCreatedUtc,
                copiedImageDescriptor.CreatedUtc);

            Assert.AreEqual(
                sourceModifiedUtc,
                copiedImageDescriptor.ModifiedUtc);

            Assert.AreEqual(
                sourceTimelineDate,
                copiedImageDescriptor.TimelineDateOverride);

            TagDescriptor copiedSharedTag =
                copied.Tags.Single(tag =>
                    string.Equals(
                        tag.Name,
                        "Shared",
                        StringComparison.OrdinalIgnoreCase));

            Assert.AreEqual(
                existingSharedTagId,
                copiedSharedTag.TagId);

            Assert.AreEqual(
                "#destination",
                copiedSharedTag.Color);

            TagDescriptor copiedImageTag =
                copied.Tags.Single(tag =>
                    tag.Name == "Images");

            Assert.AreEqual(
                "#abcdef",
                copiedImageTag.Color);

            CollectionAssert.AreEquivalent(
                new[]
                {
                    copiedSharedTag.TagId,
                    copiedImageTag.TagId
                },
                copiedImageDescriptor.TagIds.ToArray());

            VaultEntry copiedImage =
                await copied.GetEntryAsync(
                    copiedImageDescriptor.EntryId);

            EntryField copiedField =
                copiedImage.Fields.Single();

            Assert.AreNotEqual(
                sourceImageFieldId,
                copiedField.FieldId);

            BlobFieldValue copiedBlob =
                (BlobFieldValue)copiedField.Value;

            Assert.AreNotEqual(sourceBlobId, copiedBlob.BlobId);
            Assert.AreEqual("passport.png", copiedBlob.FileName);
            Assert.AreEqual("image/png", copiedBlob.ContentType);

            using SensitiveBuffer copiedPlaintext =
                await copied.GetBlobAsync(
                    copiedImage.EntryId,
                    copiedBlob.BlobId,
                    copiedBlob.Length);

            await AssertBufferEqualsAsync(
                imageBytes,
                copiedPlaintext);

            Assert.IsFalse(
                copied.Entries.Any(entry =>
                    entry.Name == "Do not copy"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(imageBytes);
        }
    }

    [TestMethod]
    public async Task CopyAsync_SourceManifestDirty_IsRejected()
    {
        await using VaultSession source =
            await CreateVaultAsync(_sourceDirectory);

        VaultEntry entry =
            source.CreateEntry("Unsaved entry");

        await using (VaultSession destination =
                     await CreateVaultAsync(
                         _destinationDirectory))
        {
            Assert.AreEqual(
                0L,
                destination.ManifestGeneration);
        }

        InvalidOperationException exception =
            await Assert.ThrowsExactlyAsync<
                InvalidOperationException>(
                () => new VaultCopyService().CopyAsync(
                    source,
                    _destinationDirectory,
                    Password,
                    selectedEntryIds: [entry.EntryId],
                    selectedFolderIds: []));

        StringAssert.Contains(
            exception.Message,
            "Save the source vault's manifest changes");

        await using VaultSession reopened =
            await VaultSession.OpenAsync(
                _destinationDirectory,
                Password);

        Assert.AreEqual(0L, reopened.ManifestGeneration);
        Assert.IsEmpty(reopened.Entries);
    }

    [TestMethod]
    public async Task CopyAsync_EmptySelectedFolder_DoesNotChangeDestination()
    {
        await using VaultSession source =
            await CreateVaultAsync(_sourceDirectory);

        FolderDescriptor emptyFolder =
            source.CreateFolder("Empty");

        await source.SaveAsync();

        await using (VaultSession destination =
                     await CreateVaultAsync(
                         _destinationDirectory))
        {
            Assert.AreEqual(
                0L,
                destination.ManifestGeneration);
        }

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => new VaultCopyService().CopyAsync(
                source,
                _destinationDirectory,
                Password,
                selectedEntryIds: [],
                selectedFolderIds: [emptyFolder.FolderId]));

        await using VaultSession reopened =
            await VaultSession.OpenAsync(
                _destinationDirectory,
                Password);

        Assert.AreEqual(0L, reopened.ManifestGeneration);
        Assert.IsEmpty(reopened.Entries);
        Assert.IsEmpty(reopened.Folders);
    }

    [TestMethod]
    public async Task CopyAsync_SourceVaultAsDestination_IsRejected()
    {
        await using VaultSession source =
            await CreateVaultAsync(_sourceDirectory);

        VaultEntry entry =
            source.CreateEntry("Entry");

        await source.SaveAsync();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => new VaultCopyService().CopyAsync(
                source,
                _sourceDirectory,
                Password,
                selectedEntryIds: [entry.EntryId],
                selectedFolderIds: []));

        Assert.HasCount(1, source.Entries);
        Assert.AreEqual(1L, source.ManifestGeneration);
    }

    private static Task<VaultSession> CreateVaultAsync(
        string path)
    {
        return VaultSession.CreateAsync(
            path,
            Password,
            TestKdfParameters);
    }

    private static Argon2idParameters TestKdfParameters =>
        new()
        {
            Version = Argon2idParameters.SupportedVersion,
            MemorySizeKiB = 19 * 1024,
            Iterations = 2,
            DegreeOfParallelism = 1
        };

    private static EntryField CreateTextField(
        string text)
    {
        return new EntryField(
            Guid.NewGuid(),
            "Text",
            new TextFieldValue(text));
    }

    private static byte[] CreateBlobPlaintext()
    {
        byte[] plaintext = new byte[1025];

        for (int index = 0; index < plaintext.Length; index++)
        {
            plaintext[index] =
                (byte)(0x31 + index);
        }

        return plaintext;
    }

    private static async Task AssertBufferEqualsAsync(
        byte[] expected,
        SensitiveBuffer actual)
    {
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
}
