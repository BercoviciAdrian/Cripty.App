using System.Security.Cryptography;
using Cripty.Core.Vaults;
using Cripty.Cryptography.Models;
using Cripty.Storage.Codecs;
using Cripty.Storage.Formats;

namespace Cripty.Storage.Tests;

[TestClass]
public sealed class VaultFileCodecTests
{
    private const string Password =
        "correct horse battery staple";

    [TestMethod]
    public void CreateAndOpen_ManifestAndRootKey_RoundTrip()
    {
        Guid vaultId = Guid.NewGuid();

        byte[] rootKey = CodecTestData.CreateRootKey();
        byte[] restoredRootKey =
            new byte[rootKey.Length];

        VaultManifest original =
            CodecTestData.CreateManifest(vaultId);

        VaultFileCodec codec = new();

        try
        {
            VaultFile file =
                codec.Create(
                    original,
                    rootKey,
                    Password,
                    CodecTestData.TestKdfParameters);

            VaultManifest restored =
                codec.Open(
                    file,
                    Password,
                    restoredRootKey);

            Assert.AreEqual(
                VaultFileCodec.CurrentFormatVersion,
                file.FormatVersion);

            Assert.AreEqual(vaultId, file.VaultId);

            Assert.AreEqual(
                original.Generation,
                file.ManifestGeneration);

            CollectionAssert.AreEqual(
                rootKey,
                restoredRootKey);

            CodecTestData.AssertManifestsEqual(
                original,
                restored);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rootKey);

            CryptographicOperations.ZeroMemory(
                restoredRootKey);
        }
    }

    [TestMethod]
    public void Open_WrongPassword_ThrowsAndZerosDestination()
    {
        Guid vaultId = Guid.NewGuid();
        byte[] rootKey = CodecTestData.CreateRootKey();

        byte[] destination =
            Enumerable.Repeat(
                    (byte)0xA5,
                    rootKey.Length)
                .ToArray();

        VaultManifest manifest =
            CodecTestData.CreateManifest(vaultId);

        VaultFileCodec codec = new();

        try
        {
            VaultFile file =
                codec.Create(
                    manifest,
                    rootKey,
                    Password,
                    CodecTestData.TestKdfParameters);

            Assert.ThrowsExactly<CryptographicException>(
                () => codec.Open(
                    file,
                    "incorrect password",
                    destination));

            CollectionAssert.AreEqual(
                new byte[rootKey.Length],
                destination);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rootKey);
            CryptographicOperations.ZeroMemory(destination);
        }
    }

    [TestMethod]
    public void Open_TamperedRootKeyEnvelope_Throws()
    {
        Guid vaultId = Guid.NewGuid();
        byte[] rootKey = CodecTestData.CreateRootKey();

        byte[] destination =
            new byte[rootKey.Length];

        VaultManifest manifest =
            CodecTestData.CreateManifest(vaultId);

        VaultFileCodec codec = new();

        try
        {
            VaultFile original =
                codec.Create(
                    manifest,
                    rootKey,
                    Password,
                    CodecTestData.TestKdfParameters);

            CbcHmacEnvelope tamperedRootEnvelope =
                CodecTestData.CloneEnvelope(
                    original.PasswordKeySlot
                        .RootKeyEnvelope);

            tamperedRootEnvelope.Mac[0] ^= 0x01;

            VaultFile tamperedFile = new()
            {
                FormatVersion = original.FormatVersion,
                VaultId = original.VaultId,
                ManifestGeneration = original.ManifestGeneration,

                PasswordKeySlot = new PasswordKeySlot
                {
                    KdfParameters =
                        original.PasswordKeySlot
                            .KdfParameters,

                    Salt =
                        original.PasswordKeySlot
                            .Salt.ToArray(),

                    RootKeyEnvelope =
                        tamperedRootEnvelope
                },

                ManifestEnvelope =
                    original.ManifestEnvelope
            };

            Assert.ThrowsExactly<CryptographicException>(
                () => codec.Open(
                    tamperedFile,
                    Password,
                    destination));

            CollectionAssert.AreEqual(
                new byte[rootKey.Length],
                destination);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rootKey);
            CryptographicOperations.ZeroMemory(destination);
        }
    }

    [TestMethod]
    public void Open_TamperedManifestEnvelope_Throws()
    {
        Guid vaultId = Guid.NewGuid();
        byte[] rootKey = CodecTestData.CreateRootKey();

        byte[] destination =
            new byte[rootKey.Length];

        VaultManifest manifest =
            CodecTestData.CreateManifest(vaultId);

        VaultFileCodec codec = new();

        try
        {
            VaultFile original =
                codec.Create(
                    manifest,
                    rootKey,
                    Password,
                    CodecTestData.TestKdfParameters);

            CbcHmacEnvelope tamperedManifestEnvelope =
                CodecTestData.CloneEnvelope(
                    original.ManifestEnvelope);

            tamperedManifestEnvelope.Ciphertext[0] ^= 0x01;

            VaultFile tamperedFile = new()
            {
                FormatVersion = original.FormatVersion,
                VaultId = original.VaultId,
                ManifestGeneration = original.ManifestGeneration,

                PasswordKeySlot =
                    original.PasswordKeySlot,

                ManifestEnvelope =
                    tamperedManifestEnvelope
            };

            Assert.ThrowsExactly<CryptographicException>(
                () => codec.Open(
                    tamperedFile,
                    Password,
                    destination));

            CollectionAssert.AreEqual(
                new byte[rootKey.Length],
                destination);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rootKey);
            CryptographicOperations.ZeroMemory(destination);
        }
    }

    [TestMethod]
    public void Open_TamperedVaultId_Throws()
    {
        Guid vaultId = Guid.NewGuid();
        byte[] rootKey = CodecTestData.CreateRootKey();

        byte[] destination =
            new byte[rootKey.Length];

        VaultManifest manifest =
            CodecTestData.CreateManifest(vaultId);

        VaultFileCodec codec = new();

        try
        {
            VaultFile original =
                codec.Create(
                    manifest,
                    rootKey,
                    Password,
                    CodecTestData.TestKdfParameters);

            VaultFile tamperedFile = new()
            {
                FormatVersion = original.FormatVersion,
                VaultId = Guid.NewGuid(),
                ManifestGeneration = original.ManifestGeneration,

                PasswordKeySlot =
                    original.PasswordKeySlot,

                ManifestEnvelope =
                    original.ManifestEnvelope
            };

            Assert.ThrowsExactly<CryptographicException>(
                () => codec.Open(
                    tamperedFile,
                    Password,
                    destination));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rootKey);
            CryptographicOperations.ZeroMemory(destination);
        }
    }

    [TestMethod]
    public void Open_TamperedVisibleGeneration_Throws()
    {
        Guid vaultId = Guid.NewGuid();
        byte[] rootKey = CodecTestData.CreateRootKey();
        byte[] destination = new byte[rootKey.Length];

        VaultManifest manifest =
            CodecTestData.CreateManifest(
                vaultId,
                generation: 4);

        VaultFileCodec codec = new();

        try
        {
            VaultFile original = codec.Create(
                manifest,
                rootKey,
                Password,
                CodecTestData.TestKdfParameters);

            VaultFile tampered = new()
            {
                FormatVersion = original.FormatVersion,
                VaultId = original.VaultId,
                ManifestGeneration = 5,
                PasswordKeySlot = original.PasswordKeySlot,
                ManifestEnvelope = original.ManifestEnvelope
            };

            Assert.ThrowsExactly<InvalidDataException>(
                () => codec.Open(
                    tampered,
                    Password,
                    destination));

            CollectionAssert.AreEqual(
                new byte[rootKey.Length],
                destination);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rootKey);
            CryptographicOperations.ZeroMemory(destination);
        }
    }

    [TestMethod]
    public void Open_UnsupportedFormatVersion_Throws()
    {
        Guid vaultId = Guid.NewGuid();
        byte[] rootKey = CodecTestData.CreateRootKey();

        byte[] destination =
            new byte[rootKey.Length];

        VaultManifest manifest =
            CodecTestData.CreateManifest(vaultId);

        VaultFileCodec codec = new();

        try
        {
            VaultFile original =
                codec.Create(
                    manifest,
                    rootKey,
                    Password,
                    CodecTestData.TestKdfParameters);

            VaultFile unsupportedFile = new()
            {
                FormatVersion =
                    VaultFileCodec.CurrentFormatVersion + 1,

                VaultId = original.VaultId,
                ManifestGeneration = original.ManifestGeneration,

                PasswordKeySlot =
                    original.PasswordKeySlot,

                ManifestEnvelope =
                    original.ManifestEnvelope
            };

            Assert.ThrowsExactly<NotSupportedException>(
                () => codec.Open(
                    unsupportedFile,
                    Password,
                    destination));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rootKey);
            CryptographicOperations.ZeroMemory(destination);
        }
    }

    [TestMethod]
    public void UpdateManifest_PreservesPasswordSlotAndRemainsOpenable()
    {
        Guid vaultId = Guid.NewGuid();

        byte[] rootKey = CodecTestData.CreateRootKey();
        byte[] restoredRootKey =
            new byte[rootKey.Length];

        VaultManifest originalManifest =
            CodecTestData.CreateManifest(
                vaultId,
                generation: 4,
                entryName: "Old name");

        VaultManifest modifiedManifest =
            CodecTestData.CreateManifest(
                vaultId,
                generation: 5,
                entryName: "Updated name");

        VaultFileCodec codec = new();

        try
        {
            VaultFile originalFile =
                codec.Create(
                    originalManifest,
                    rootKey,
                    Password,
                    CodecTestData.TestKdfParameters);

            VaultFile updatedFile =
                codec.UpdateManifest(
                    originalFile,
                    modifiedManifest,
                    rootKey);

            Assert.AreEqual(
                originalFile.FormatVersion,
                updatedFile.FormatVersion);

            Assert.AreEqual(
                originalFile.VaultId,
                updatedFile.VaultId);

            Assert.AreEqual(
                modifiedManifest.Generation,
                updatedFile.ManifestGeneration);

            Assert.AreEqual(
                originalFile.PasswordKeySlot
                    .KdfParameters.Version,

                updatedFile.PasswordKeySlot
                    .KdfParameters.Version);

            Assert.AreEqual(
                originalFile.PasswordKeySlot
                    .KdfParameters.MemorySizeKiB,

                updatedFile.PasswordKeySlot
                    .KdfParameters.MemorySizeKiB);

            Assert.AreEqual(
                originalFile.PasswordKeySlot
                    .KdfParameters.Iterations,

                updatedFile.PasswordKeySlot
                    .KdfParameters.Iterations);

            Assert.AreEqual(
                originalFile.PasswordKeySlot
                    .KdfParameters.DegreeOfParallelism,

                updatedFile.PasswordKeySlot
                    .KdfParameters.DegreeOfParallelism);

            CollectionAssert.AreEqual(
                originalFile.PasswordKeySlot.Salt,
                updatedFile.PasswordKeySlot.Salt);

            CodecTestData.AssertEnvelopesEqual(
                originalFile.PasswordKeySlot
                    .RootKeyEnvelope,

                updatedFile.PasswordKeySlot
                    .RootKeyEnvelope);

            VaultManifest restored =
                codec.Open(
                    updatedFile,
                    Password,
                    restoredRootKey);

            CollectionAssert.AreEqual(
                rootKey,
                restoredRootKey);

            CodecTestData.AssertManifestsEqual(
                modifiedManifest,
                restored);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rootKey);

            CryptographicOperations.ZeroMemory(
                restoredRootKey);
        }
    }

    [TestMethod]
    public void UpdateManifest_WrongRootKey_Throws()
    {
        Guid vaultId = Guid.NewGuid();

        byte[] rootKey = CodecTestData.CreateRootKey();
        byte[] wrongRootKey = rootKey.ToArray();

        wrongRootKey[0] ^= 0x80;

        VaultManifest originalManifest =
            CodecTestData.CreateManifest(
                vaultId,
                generation: 4);

        VaultManifest modifiedManifest =
            CodecTestData.CreateManifest(
                vaultId,
                generation: 5);

        VaultFileCodec codec = new();

        try
        {
            VaultFile existingFile =
                codec.Create(
                    originalManifest,
                    rootKey,
                    Password,
                    CodecTestData.TestKdfParameters);

            Assert.ThrowsExactly<CryptographicException>(
                () => codec.UpdateManifest(
                    existingFile,
                    modifiedManifest,
                    wrongRootKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rootKey);
            CryptographicOperations.ZeroMemory(wrongRootKey);
        }
    }

    [TestMethod]
    public void UpdateManifest_CorruptedExistingManifest_Throws()
    {
        Guid vaultId = Guid.NewGuid();
        byte[] rootKey = CodecTestData.CreateRootKey();

        VaultManifest originalManifest =
            CodecTestData.CreateManifest(
                vaultId,
                generation: 4);

        VaultManifest modifiedManifest =
            CodecTestData.CreateManifest(
                vaultId,
                generation: 5);

        VaultFileCodec codec = new();

        try
        {
            VaultFile originalFile =
                codec.Create(
                    originalManifest,
                    rootKey,
                    Password,
                    CodecTestData.TestKdfParameters);

            CbcHmacEnvelope corruptedEnvelope =
                CodecTestData.CloneEnvelope(
                    originalFile.ManifestEnvelope);

            corruptedEnvelope.Mac[0] ^= 0x01;

            VaultFile corruptedFile = new()
            {
                FormatVersion =
                    originalFile.FormatVersion,

                VaultId =
                    originalFile.VaultId,

                ManifestGeneration =
                    originalFile.ManifestGeneration,

                PasswordKeySlot =
                    originalFile.PasswordKeySlot,

                ManifestEnvelope =
                    corruptedEnvelope
            };

            Assert.ThrowsExactly<CryptographicException>(
                () => codec.UpdateManifest(
                    corruptedFile,
                    modifiedManifest,
                    rootKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rootKey);
        }
    }

    /*
     * Intentionally red until supported manifest-schema
     * validation is implemented.
     */
    [TestMethod]
    public void CreateOrOpen_UnsupportedSchemaVersion_Throws()
    {
        Guid vaultId = Guid.NewGuid();
        byte[] rootKey = CodecTestData.CreateRootKey();

        byte[] destination =
            new byte[rootKey.Length];

        VaultManifest unsupportedManifest =
            CodecTestData.CreateManifest(
                vaultId,
                schemaVersion:
                    CodecTestData.CurrentManifestSchemaVersion + 1);

        VaultFileCodec codec = new();

        try
        {
            Assert.ThrowsExactly<NotSupportedException>(
                () =>
                {
                    VaultFile file =
                        codec.Create(
                            unsupportedManifest,
                            rootKey,
                            Password,
                            CodecTestData.TestKdfParameters);

                    codec.Open(
                        file,
                        Password,
                        destination);
                });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rootKey);
            CryptographicOperations.ZeroMemory(destination);
        }
    }

    /*
     * Intentionally red until restored-domain validation
     * detects cycles in the persisted folder graph.
     */
    [TestMethod]
    public void CreateOrOpen_CyclicFolderHierarchy_Throws()
    {
        Guid vaultId = Guid.NewGuid();

        Guid firstFolderId = Guid.NewGuid();
        Guid secondFolderId = Guid.NewGuid();

        byte[] rootKey = CodecTestData.CreateRootKey();

        byte[] destination =
            new byte[rootKey.Length];

        VaultManifest invalidManifest = new(
            CodecTestData.CurrentManifestSchemaVersion,
            vaultId,
            generation: 1,
            [
                new FolderDescriptor(
                    firstFolderId,
                    "First",
                    secondFolderId),

                new FolderDescriptor(
                    secondFolderId,
                    "Second",
                    firstFolderId)
            ],
            tags: [],
            entries: []);

        VaultFileCodec codec = new();

        try
        {
            Assert.ThrowsExactly<InvalidDataException>(
                () =>
                {
                    VaultFile file =
                        codec.Create(
                            invalidManifest,
                            rootKey,
                            Password,
                            CodecTestData.TestKdfParameters);

                    codec.Open(
                        file,
                        Password,
                        destination);
                });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rootKey);
            CryptographicOperations.ZeroMemory(destination);
        }
    }

    [TestMethod]
    public void Create_FolderSortPreferenceForMissingFolder_Throws()
    {
        Guid vaultId = Guid.NewGuid();
        byte[] rootKey = CodecTestData.CreateRootKey();

        VaultManifest invalidManifest = new(
            CodecTestData.CurrentManifestSchemaVersion,
            vaultId,
            generation: 1,
            folders: [],
            tags: [],
            entries: [],
            sortPreferences: new VaultSortPreferences(
                folderSortModes:
                    new Dictionary<Guid, EntrySortMode>
                    {
                        [Guid.NewGuid()] =
                            EntrySortMode.NameAscending
                    }));

        try
        {
            Assert.ThrowsExactly<InvalidDataException>(() =>
                new VaultFileCodec().Create(
                    invalidManifest,
                    rootKey,
                    Password,
                    CodecTestData.TestKdfParameters));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rootKey);
        }
    }
}
