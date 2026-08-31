using Cripty.Core.Entries;
using Cripty.Core.Vaults;
using Cripty.Cryptography.Keys;
using Cripty.Cryptography.Models;

namespace Cripty.Storage.Tests;

internal static class CodecTestData
{
    public const int CurrentEntrySchemaVersion = 1;
    public const int CurrentManifestSchemaVersion = 2;

    /*
     * These are the smallest parameters currently accepted by
     * Argon2idParameters.Validate().
     *
     * They keep the tests faster without changing the production
     * defaults used by the application.
     */
    public static Argon2idParameters TestKdfParameters => new()
    {
        Version = Argon2idParameters.SupportedVersion,
        MemorySizeKiB = 19 * 1024,
        Iterations = 2,
        DegreeOfParallelism = 1
    };

    public static byte[] CreateRootKey()
    {
        byte[] rootKey =
            new byte[VaultRootKeyGenerator.KeySize];

        VaultRootKeyGenerator.Generate(rootKey);

        return rootKey;
    }

    public static VaultEntry CreateMixedEntry(
        int schemaVersion = CurrentEntrySchemaVersion,
        Guid? entryId = null)
    {
        return new VaultEntry(
            schemaVersion,
            entryId ?? Guid.NewGuid(),
            revision: 7,
            [
                new EntryField(
                    Guid.NewGuid(),
                    "Notes",
                    new TextFieldValue(
                        "Confidential text: ăîșț 🔐")),

                new EntryField(
                    Guid.NewGuid(),
                    "Attachment",
                    new BlobFieldValue(
                        Guid.NewGuid(),
                        "diagram.png",
                        "image/png",
                        128_419))
            ]);
    }

    public static VaultManifest CreateManifest(
        Guid vaultId,
        long generation = 4,
        int schemaVersion = CurrentManifestSchemaVersion,
        string entryName = "Primary account")
    {
        Guid rootFolderId = Guid.NewGuid();
        Guid childFolderId = Guid.NewGuid();
        Guid tagId = Guid.NewGuid();

        DateTimeOffset createdUtc =
            new(
                2026,
                7,
                20,
                12,
                30,
                0,
                TimeSpan.Zero);

        DateTimeOffset modifiedUtc =
            new(
                2026,
                7,
                27,
                18,
                45,
                0,
                TimeSpan.Zero);

        return new VaultManifest(
            schemaVersion,
            vaultId,
            generation,
            [
                new FolderDescriptor(
                    rootFolderId,
                    "Accounts",
                    parentFolderId: null),

                new FolderDescriptor(
                    childFolderId,
                    "Email",
                    rootFolderId)
            ],
            [
                new TagDescriptor(
                    tagId,
                    "Important",
                    "#ff0000")
            ],
            [
                new EntryDescriptor(
                    Guid.NewGuid(),
                    entryName,
                    childFolderId,
                    [tagId],
                    revision: 7,
                    createdUtc,
                    modifiedUtc,
                    timelineDateOverride:
                        schemaVersion >= 2
                            ? new DateOnly(2026, 7, 22)
                            : null)
            ]);
    }

    public static CbcHmacEnvelope CloneEnvelope(
        CbcHmacEnvelope envelope)
    {
        return new CbcHmacEnvelope
        {
            Iv = envelope.Iv.ToArray(),
            Ciphertext = envelope.Ciphertext.ToArray(),
            Mac = envelope.Mac.ToArray()
        };
    }

    public static void AssertEntriesEqual(
        VaultEntry expected,
        VaultEntry actual)
    {
        Assert.AreEqual(
            expected.SchemaVersion,
            actual.SchemaVersion);

        Assert.AreEqual(
            expected.EntryId,
            actual.EntryId);

        Assert.AreEqual(
            expected.Revision,
            actual.Revision);

        Assert.AreEqual(
            expected.Fields.Count,
            actual.Fields.Count);

        for (int i = 0; i < expected.Fields.Count; i++)
        {
            EntryField expectedField = expected.Fields[i];
            EntryField actualField = actual.Fields[i];

            Assert.AreEqual(
                expectedField.FieldId,
                actualField.FieldId);

            Assert.AreEqual(
                expectedField.Name,
                actualField.Name);

            /*
             * TextFieldValue and BlobFieldValue are records,
             * so their value-based equality compares their contents.
             */
            Assert.AreEqual(
                expectedField.Value,
                actualField.Value);
        }
    }

    public static void AssertManifestsEqual(
        VaultManifest expected,
        VaultManifest actual)
    {
        Assert.AreEqual(
            expected.SchemaVersion,
            actual.SchemaVersion);

        Assert.AreEqual(
            expected.VaultId,
            actual.VaultId);

        Assert.AreEqual(
            expected.Generation,
            actual.Generation);

        Assert.AreEqual(
            expected.Folders.Count,
            actual.Folders.Count);

        for (int i = 0; i < expected.Folders.Count; i++)
        {
            FolderDescriptor expectedFolder =
                expected.Folders[i];

            FolderDescriptor actualFolder =
                actual.Folders[i];

            Assert.AreEqual(
                expectedFolder.FolderId,
                actualFolder.FolderId);

            Assert.AreEqual(
                expectedFolder.Name,
                actualFolder.Name);

            Assert.AreEqual(
                expectedFolder.ParentFolderId,
                actualFolder.ParentFolderId);
        }

        Assert.AreEqual(
            expected.Tags.Count,
            actual.Tags.Count);

        for (int i = 0; i < expected.Tags.Count; i++)
        {
            TagDescriptor expectedTag = expected.Tags[i];
            TagDescriptor actualTag = actual.Tags[i];

            Assert.AreEqual(
                expectedTag.TagId,
                actualTag.TagId);

            Assert.AreEqual(
                expectedTag.Name,
                actualTag.Name);

            Assert.AreEqual(
                expectedTag.Color,
                actualTag.Color);
        }

        Assert.AreEqual(
            expected.Entries.Count,
            actual.Entries.Count);

        for (int i = 0; i < expected.Entries.Count; i++)
        {
            EntryDescriptor expectedEntry =
                expected.Entries[i];

            EntryDescriptor actualEntry =
                actual.Entries[i];

            Assert.AreEqual(
                expectedEntry.EntryId,
                actualEntry.EntryId);

            Assert.AreEqual(
                expectedEntry.Name,
                actualEntry.Name);

            Assert.AreEqual(
                expectedEntry.FolderId,
                actualEntry.FolderId);

            Assert.AreEqual(
                expectedEntry.Revision,
                actualEntry.Revision);

            Assert.AreEqual(
                expectedEntry.CreatedUtc,
                actualEntry.CreatedUtc);

            Assert.AreEqual(
                expectedEntry.ModifiedUtc,
                actualEntry.ModifiedUtc);

            Assert.AreEqual(
                expectedEntry.TimelineDateOverride,
                actualEntry.TimelineDateOverride);

            CollectionAssert.AreEqual(
                expectedEntry.TagIds.ToList(),
                actualEntry.TagIds.ToList());
        }
    }

    public static void AssertEnvelopesEqual(
        CbcHmacEnvelope expected,
        CbcHmacEnvelope actual)
    {
        CollectionAssert.AreEqual(
            expected.Iv,
            actual.Iv);

        CollectionAssert.AreEqual(
            expected.Ciphertext,
            actual.Ciphertext);

        CollectionAssert.AreEqual(
            expected.Mac,
            actual.Mac);
    }
}
