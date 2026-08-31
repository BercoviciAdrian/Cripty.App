using System.Security.Cryptography;
using Cripty.Core.Entries;
using Cripty.Cryptography.Models;
using Cripty.Storage.Codecs;
using Cripty.Storage.Formats;

namespace Cripty.Storage.Tests;

[TestClass]
public sealed class EntryFileCodecTests
{
    [TestMethod]
    public void CreateAndOpen_MixedTextAndBlobEntry_RoundTrips()
    {
        Guid vaultId = Guid.NewGuid();
        byte[] rootKey = CodecTestData.CreateRootKey();

        VaultEntry original =
            CodecTestData.CreateMixedEntry();

        EntryFileCodec codec = new();

        try
        {
            EntryFile file =
                codec.Create(
                    vaultId,
                    original,
                    rootKey);

            VaultEntry restored =
                codec.Open(
                    file,
                    rootKey);

            Assert.AreEqual(
                EntryFileCodec.CurrentFormatVersion,
                file.FormatVersion);

            Assert.AreEqual(vaultId, file.VaultId);
            Assert.AreEqual(original.EntryId, file.EntryId);

            CodecTestData.AssertEntriesEqual(
                original,
                restored);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rootKey);
        }
    }

    [TestMethod]
    public void Open_WrongRootKey_Throws()
    {
        Guid vaultId = Guid.NewGuid();

        byte[] rootKey = CodecTestData.CreateRootKey();
        byte[] wrongRootKey = rootKey.ToArray();

        wrongRootKey[0] ^= 0x80;

        VaultEntry entry =
            CodecTestData.CreateMixedEntry();

        EntryFileCodec codec = new();

        try
        {
            EntryFile file =
                codec.Create(
                    vaultId,
                    entry,
                    rootKey);

            Assert.ThrowsExactly<CryptographicException>(
                () => codec.Open(
                    file,
                    wrongRootKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rootKey);
            CryptographicOperations.ZeroMemory(wrongRootKey);
        }
    }

    [TestMethod]
    [DataRow("iv")]
    [DataRow("ciphertext")]
    [DataRow("mac")]
    public void Open_TamperedEnvelope_Throws(
        string component)
    {
        Guid vaultId = Guid.NewGuid();
        byte[] rootKey = CodecTestData.CreateRootKey();

        VaultEntry entry =
            CodecTestData.CreateMixedEntry();

        EntryFileCodec codec = new();

        try
        {
            EntryFile original =
                codec.Create(
                    vaultId,
                    entry,
                    rootKey);

            CbcHmacEnvelope tamperedEnvelope =
                CodecTestData.CloneEnvelope(
                    original.Envelope);

            byte[] componentBytes = component switch
            {
                "iv" => tamperedEnvelope.Iv,
                "ciphertext" =>
                    tamperedEnvelope.Ciphertext,
                "mac" => tamperedEnvelope.Mac,

                _ => throw new ArgumentOutOfRangeException(
                    nameof(component))
            };

            componentBytes[0] ^= 0x01;

            EntryFile tamperedFile = new()
            {
                FormatVersion = original.FormatVersion,
                VaultId = original.VaultId,
                EntryId = original.EntryId,
                Envelope = tamperedEnvelope
            };

            Assert.ThrowsExactly<CryptographicException>(
                () => codec.Open(
                    tamperedFile,
                    rootKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rootKey);
        }
    }

    [TestMethod]
    [DataRow("vaultId")]
    [DataRow("entryId")]
    public void Open_TamperedOuterIdentifier_Throws(
        string identifier)
    {
        Guid vaultId = Guid.NewGuid();
        byte[] rootKey = CodecTestData.CreateRootKey();

        VaultEntry entry =
            CodecTestData.CreateMixedEntry();

        EntryFileCodec codec = new();

        try
        {
            EntryFile original =
                codec.Create(
                    vaultId,
                    entry,
                    rootKey);

            EntryFile tamperedFile = new()
            {
                FormatVersion = original.FormatVersion,

                VaultId = identifier == "vaultId"
                    ? Guid.NewGuid()
                    : original.VaultId,

                EntryId = identifier == "entryId"
                    ? Guid.NewGuid()
                    : original.EntryId,

                Envelope = original.Envelope
            };

            Assert.ThrowsExactly<CryptographicException>(
                () => codec.Open(
                    tamperedFile,
                    rootKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rootKey);
        }
    }

    [TestMethod]
    public void Open_UnsupportedFormatVersion_Throws()
    {
        Guid vaultId = Guid.NewGuid();
        byte[] rootKey = CodecTestData.CreateRootKey();

        VaultEntry entry =
            CodecTestData.CreateMixedEntry();

        EntryFileCodec codec = new();

        try
        {
            EntryFile original =
                codec.Create(
                    vaultId,
                    entry,
                    rootKey);

            EntryFile unsupportedFile = new()
            {
                FormatVersion =
                    EntryFileCodec.CurrentFormatVersion + 1,

                VaultId = original.VaultId,
                EntryId = original.EntryId,
                Envelope = original.Envelope
            };

            Assert.ThrowsExactly<NotSupportedException>(
                () => codec.Open(
                    unsupportedFile,
                    rootKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rootKey);
        }
    }

    /*
     * This test is intentionally red until supported
     * entry-schema validation is added.
     */
    [TestMethod]
    public void CreateOrOpen_UnsupportedSchemaVersion_Throws()
    {
        Guid vaultId = Guid.NewGuid();
        byte[] rootKey = CodecTestData.CreateRootKey();

        VaultEntry entry =
            CodecTestData.CreateMixedEntry(
                schemaVersion:
                    CodecTestData.CurrentEntrySchemaVersion + 1);

        EntryFileCodec codec = new();

        try
        {
            Assert.ThrowsExactly<NotSupportedException>(
                () =>
                {
                    EntryFile file =
                        codec.Create(
                            vaultId,
                            entry,
                            rootKey);

                    codec.Open(file, rootKey);
                });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rootKey);
        }
    }

    /*
     * This is also intentionally red until restored-domain
     * validation rejects duplicate field IDs.
     */
    [TestMethod]
    public void CreateOrOpen_DuplicateFieldIds_Throws()
    {
        Guid vaultId = Guid.NewGuid();
        Guid duplicateFieldId = Guid.NewGuid();

        byte[] rootKey = CodecTestData.CreateRootKey();

        VaultEntry invalidEntry = new(
            CodecTestData.CurrentEntrySchemaVersion,
            Guid.NewGuid(),
            revision: 1,
            [
                new EntryField(
                    duplicateFieldId,
                    "First",
                    new TextFieldValue("one")),

                new EntryField(
                    duplicateFieldId,
                    "Second",
                    new TextFieldValue("two"))
            ]);

        EntryFileCodec codec = new();

        try
        {
            Assert.ThrowsExactly<InvalidDataException>(
                () =>
                {
                    EntryFile file =
                        codec.Create(
                            vaultId,
                            invalidEntry,
                            rootKey);

                    codec.Open(file, rootKey);
                });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rootKey);
        }
    }
}
