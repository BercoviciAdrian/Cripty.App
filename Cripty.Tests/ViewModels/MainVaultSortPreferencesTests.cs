using Cripty.Application.Vaults;
using Cripty.Core.Vaults;
using Cripty.Cryptography.Keys;
using Cripty.ViewModels;

namespace Cripty.Tests.ViewModels;

[TestClass]
[DoNotParallelize]
public sealed class MainVaultSortPreferencesTests
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
    public async Task SelectedSort_PersistsPerFolderAndVirtualView()
    {
        Guid firstFolderId;
        Guid secondFolderId;

        await using (VaultSession session =
                     await CreateSessionAsync())
        {
            firstFolderId =
                session.CreateFolder("First").FolderId;

            secondFolderId =
                session.CreateFolder("Second").FolderId;

            Guid tagId =
                session.CreateTag("Reference").TagId;

            await session.SaveAsync();

            MainVaultViewModel viewModel =
                CreateViewModel(session);

            SelectFolder(viewModel, firstFolderId);
            viewModel.SelectedSortOption =
                VaultEntrySortOptionViewModel.TimelineOldest;

            viewModel.TagItems.Single(item =>
                    item.TagId == tagId)
                .SelectCommand.Execute(null);

            Assert.AreSame(
                VaultEntrySortOptionViewModel.TimelineOldest,
                viewModel.SelectedSortOption);

            SelectFolder(viewModel, secondFolderId);
            Assert.AreSame(
                VaultEntrySortOptionViewModel.ModifiedNewest,
                viewModel.SelectedSortOption);

            viewModel.SelectedSortOption =
                VaultEntrySortOptionViewModel.NameDescending;

            SelectFolder(
                viewModel,
                VaultFolderFilterKind.Root);
            viewModel.SelectedSortOption =
                VaultEntrySortOptionViewModel.CreatedOldest;

            SelectFolder(
                viewModel,
                VaultFolderFilterKind.AllEntries);
            viewModel.SelectedSortOption =
                VaultEntrySortOptionViewModel.TimelineNewest;

            SelectFolder(viewModel, firstFolderId);
            Assert.AreSame(
                VaultEntrySortOptionViewModel.TimelineOldest,
                viewModel.SelectedSortOption);

            SelectFolder(viewModel, secondFolderId);
            Assert.AreSame(
                VaultEntrySortOptionViewModel.NameDescending,
                viewModel.SelectedSortOption);

            Assert.IsTrue(viewModel.HasUnsavedChanges);
            await session.SaveAsync();
        }

        await using VaultSession reopened =
            await VaultSession.OpenAsync(
                _vaultDirectory,
                Password);

        MainVaultViewModel restoredViewModel =
            CreateViewModel(reopened);

        Assert.AreSame(
            VaultEntrySortOptionViewModel.TimelineNewest,
            restoredViewModel.SelectedSortOption);

        SelectFolder(
            restoredViewModel,
            VaultFolderFilterKind.Root);
        Assert.AreSame(
            VaultEntrySortOptionViewModel.CreatedOldest,
            restoredViewModel.SelectedSortOption);

        SelectFolder(restoredViewModel, firstFolderId);
        Assert.AreSame(
            VaultEntrySortOptionViewModel.TimelineOldest,
            restoredViewModel.SelectedSortOption);

        SelectFolder(restoredViewModel, secondFolderId);
        Assert.AreSame(
            VaultEntrySortOptionViewModel.NameDescending,
            restoredViewModel.SelectedSortOption);
    }

    private Task<VaultSession> CreateSessionAsync()
    {
        return VaultSession.CreateAsync(
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

    private static void SelectFolder(
        MainVaultViewModel viewModel,
        Guid folderId)
    {
        viewModel.FolderItems
            .OfType<VaultFolderListItemViewModel>()
            .Single(item => item.FolderId == folderId)
            .SelectCommand.Execute(null);
    }

    private static void SelectFolder(
        MainVaultViewModel viewModel,
        VaultFolderFilterKind kind)
    {
        viewModel.FolderItems
            .OfType<VaultFolderListItemViewModel>()
            .Single(item => item.Kind == kind)
            .SelectCommand.Execute(null);
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
