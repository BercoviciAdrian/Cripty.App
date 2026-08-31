using Cripty.Application.Vaults;
using Cripty.Core.Vaults;
using Cripty.Cryptography.Keys;
using Cripty.ViewModels;

namespace Cripty.Tests.ViewModels;

[TestClass]
[DoNotParallelize]
public sealed class MainVaultRenameTests
{
    private const string Password =
        "correct horse battery staple";

    private string _vaultDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _vaultDirectory = Path.Combine(
            Path.GetTempPath(),
            "Cripty.Tests",
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
    public async Task FolderHierarchyRename_RenamesEntryAndKeepsId()
    {
        await using VaultSession session =
            await CreateSessionAsync();

        Guid entryId =
            session.CreateEntry("Old entry").EntryId;

        await session.SaveAsync();

        MainVaultViewModel viewModel =
            CreateViewModel(session);

        VaultFolderEntryListItemViewModel entry =
            viewModel.FolderItems
                .OfType<VaultFolderEntryListItemViewModel>()
                .Single(item => item.EntryId == entryId);

        Assert.IsTrue(entry.RenameCommand.CanExecute(null));

        entry.RenameCommand.Execute(null);

        AssertRenameDialog(
            viewModel,
            "RENAME ENTRY",
            "Old entry");

        viewModel.DialogInput = "New entry";

        Assert.IsTrue(
            viewModel.ConfirmDialogCommand.CanExecute(null));

        await viewModel.ConfirmDialogCommand.ExecuteAsync(null);

        EntryDescriptor renamed =
            session.Entries.Single(item =>
                item.EntryId == entryId);

        Assert.AreEqual(entryId, renamed.EntryId);
        Assert.AreEqual("New entry", renamed.Name);
        Assert.AreEqual(1L, renamed.Revision);
        Assert.IsTrue(viewModel.HasUnsavedChanges);

        VaultFolderEntryListItemViewModel refreshed =
            viewModel.FolderItems
                .OfType<VaultFolderEntryListItemViewModel>()
                .Single(item => item.EntryId == entryId);

        Assert.AreEqual("New entry", refreshed.Name);
    }

    [TestMethod]
    public async Task FolderHierarchyRename_RenamesFolderAndKeepsId()
    {
        await using VaultSession session =
            await CreateSessionAsync();

        Guid folderId =
            session.CreateFolder("Old folder").FolderId;

        await session.SaveAsync();

        MainVaultViewModel viewModel =
            CreateViewModel(session);

        VaultFolderListItemViewModel folder =
            viewModel.FolderItems
                .OfType<VaultFolderListItemViewModel>()
                .Single(item => item.FolderId == folderId);

        Assert.IsTrue(folder.RenameCommand.CanExecute(null));

        folder.RenameCommand.Execute(null);

        AssertRenameDialog(
            viewModel,
            "RENAME FOLDER",
            "Old folder");

        viewModel.DialogInput = "New folder";

        await viewModel.ConfirmDialogCommand.ExecuteAsync(null);

        FolderDescriptor renamed =
            session.Folders.Single(item =>
                item.FolderId == folderId);

        Assert.AreEqual(folderId, renamed.FolderId);
        Assert.AreEqual("New folder", renamed.Name);
        Assert.IsTrue(viewModel.HasUnsavedChanges);

        VaultFolderListItemViewModel refreshed =
            viewModel.FolderItems
                .OfType<VaultFolderListItemViewModel>()
                .Single(item => item.FolderId == folderId);

        Assert.AreEqual("New folder", refreshed.Name);
    }

    [TestMethod]
    public async Task TagHierarchyRename_RenamesTagAndKeepsId()
    {
        await using VaultSession session =
            await CreateSessionAsync();

        Guid tagId =
            session.CreateTag("Old tag").TagId;

        Guid entryId =
            session.CreateEntry(
                    "Tagged entry",
                    tagIds: [tagId])
                .EntryId;

        await session.SaveAsync();

        MainVaultViewModel viewModel =
            CreateViewModel(session);

        VaultTagListItemViewModel tag =
            viewModel.TagItems.Single(item =>
                item.TagId == tagId);

        Assert.IsTrue(tag.RenameCommand.CanExecute(null));

        tag.RenameCommand.Execute(null);

        AssertRenameDialog(
            viewModel,
            "RENAME TAG",
            "Old tag");

        viewModel.DialogInput = "New tag";

        await viewModel.ConfirmDialogCommand.ExecuteAsync(null);

        TagDescriptor renamed =
            session.Tags.Single(item =>
                item.TagId == tagId);

        Assert.AreEqual(tagId, renamed.TagId);
        Assert.AreEqual("New tag", renamed.Name);

        EntryDescriptor taggedEntry =
            session.Entries.Single(item =>
                item.EntryId == entryId);

        CollectionAssert.AreEqual(
            new[] { tagId },
            taggedEntry.TagIds.ToArray());

        Assert.IsTrue(viewModel.HasUnsavedChanges);

        VaultTagListItemViewModel refreshed =
            viewModel.TagItems.Single(item =>
                item.TagId == tagId);

        Assert.AreEqual("New tag", refreshed.Name);
    }

    [TestMethod]
    public async Task RenameDialog_DisablesUnchangedOrBlankName()
    {
        await using VaultSession session =
            await CreateSessionAsync();

        Guid folderId =
            session.CreateFolder("Folder").FolderId;

        await session.SaveAsync();

        MainVaultViewModel viewModel =
            CreateViewModel(session);

        VaultFolderListItemViewModel folder =
            viewModel.FolderItems
                .OfType<VaultFolderListItemViewModel>()
                .Single(item => item.FolderId == folderId);

        folder.RenameCommand.Execute(null);

        Assert.IsFalse(
            viewModel.ConfirmDialogCommand.CanExecute(null));

        viewModel.DialogInput = "   ";

        Assert.IsFalse(
            viewModel.ConfirmDialogCommand.CanExecute(null));

        viewModel.DialogInput = "folder";

        Assert.IsTrue(
            viewModel.ConfirmDialogCommand.CanExecute(null));
    }

    private async Task<VaultSession> CreateSessionAsync()
    {
        return await VaultSession.CreateAsync(
            _vaultDirectory,
            Password,
            TestKdfParameters);
    }

    private static MainVaultViewModel CreateViewModel(
        VaultSession session)
    {
        return new MainVaultViewModel(
            "Vault",
            session,
            () => Task.CompletedTask);
    }

    private static void AssertRenameDialog(
        MainVaultViewModel viewModel,
        string expectedTitle,
        string expectedInput)
    {
        Assert.IsTrue(viewModel.IsDialogOpen);
        Assert.IsTrue(viewModel.IsDialogInputVisible);
        Assert.AreEqual(expectedTitle, viewModel.DialogTitle);
        Assert.AreEqual(expectedInput, viewModel.DialogInput);

        Assert.IsFalse(
            viewModel.ConfirmDialogCommand.CanExecute(null));
    }

    private static Argon2idParameters TestKdfParameters =>
        new()
        {
            Version = Argon2idParameters.SupportedVersion,
            MemorySizeKiB = 19 * 1024,
            Iterations = 2,
            DegreeOfParallelism = 1
        };
}
