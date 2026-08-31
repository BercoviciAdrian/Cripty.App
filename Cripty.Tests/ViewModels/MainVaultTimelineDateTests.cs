using Cripty.Application.Vaults;
using Cripty.Core.Vaults;
using Cripty.Cryptography.Keys;
using Cripty.ViewModels;

namespace Cripty.Tests.ViewModels;

[TestClass]
[DoNotParallelize]
public sealed class MainVaultTimelineDateTests
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
    public async Task TimelineDate_ContextActionsSetAndClearMetadataOnly()
    {
        await using VaultSession session =
            await CreateSessionAsync();

        Guid entryId =
            session.CreateEntry("Historical note").EntryId;

        await session.SaveAsync();

        EntryDescriptor original =
            session.Entries.Single(entry =>
                entry.EntryId == entryId);

        DateTimeOffset createdUtc = original.CreatedUtc;
        DateTimeOffset modifiedUtc = original.ModifiedUtc;
        long revision = original.Revision;

        MainVaultViewModel viewModel =
            CreateViewModel(session);

        VaultFolderEntryListItemViewModel sidebarEntry =
            viewModel.FolderItems
                .OfType<VaultFolderEntryListItemViewModel>()
                .Single(entry => entry.EntryId == entryId);

        Assert.IsTrue(
            sidebarEntry.SetTimelineDateCommand.CanExecute(null));

        sidebarEntry.SetTimelineDateCommand.Execute(null);

        Assert.IsTrue(viewModel.IsTimelineDateDialogOpen);
        Assert.IsFalse(viewModel.HasTimelineDateOverride);
        Assert.IsFalse(
            viewModel.ApplyTimelineDateCommand.CanExecute(null));

        DateOnly timelineDate =
            original.EffectiveTimelineDate.AddDays(-30);

        viewModel.TimelineDateSelection =
            new DateTimeOffset(
                timelineDate.Year,
                timelineDate.Month,
                timelineDate.Day,
                0,
                0,
                0,
                TimeSpan.Zero);

        Assert.IsTrue(
            viewModel.ApplyTimelineDateCommand.CanExecute(null));

        viewModel.ApplyTimelineDateCommand.Execute(null);

        EntryDescriptor updated =
            session.Entries.Single(entry =>
                entry.EntryId == entryId);

        Assert.AreEqual(entryId, updated.EntryId);
        Assert.AreEqual(createdUtc, updated.CreatedUtc);
        Assert.AreEqual(modifiedUtc, updated.ModifiedUtc);
        Assert.AreEqual(revision, updated.Revision);
        Assert.AreEqual(
            timelineDate,
            updated.TimelineDateOverride);
        Assert.IsTrue(viewModel.HasUnsavedChanges);

        VaultEntryListItemViewModel refreshed =
            viewModel.EntryItems.Single(entry =>
                entry.EntryId == entryId);

        Assert.IsTrue(refreshed.HasTimelineDateOverride);
        Assert.AreEqual(
            $"TIMELINE {timelineDate:yyyy-MM-dd}",
            refreshed.TimelineText);

        refreshed.SetTimelineDateCommand.Execute(null);

        Assert.IsTrue(viewModel.HasTimelineDateOverride);
        Assert.IsTrue(
            viewModel.ClearTimelineDateCommand.CanExecute(null));

        viewModel.ClearTimelineDateCommand.Execute(null);

        EntryDescriptor cleared =
            session.Entries.Single(entry =>
                entry.EntryId == entryId);

        Assert.IsNull(cleared.TimelineDateOverride);
        Assert.AreEqual(
            DateOnly.FromDateTime(createdUtc.UtcDateTime),
            cleared.EffectiveTimelineDate);

        Assert.IsFalse(
            viewModel.EntryItems.Single(entry =>
                    entry.EntryId == entryId)
                .HasTimelineDateOverride);
    }

    [TestMethod]
    public async Task TimelineSort_UsesOverrideThenCreationAsFallback()
    {
        await using VaultSession session =
            await CreateSessionAsync();

        Guid earlierId =
            session.CreateEntry("Earlier").EntryId;

        Guid fallbackId =
            session.CreateEntry("Creation fallback").EntryId;

        Guid laterId =
            session.CreateEntry("Later").EntryId;

        DateOnly fallbackDate =
            session.Entries.Single(entry =>
                    entry.EntryId == fallbackId)
                .EffectiveTimelineDate;

        session.SetEntryTimelineDate(
            earlierId,
            fallbackDate.AddDays(-2));

        session.SetEntryTimelineDate(
            laterId,
            fallbackDate.AddDays(2));

        MainVaultViewModel viewModel =
            CreateViewModel(session);

        viewModel.SelectedSortOption =
            VaultEntrySortOptionViewModel.TimelineOldest;

        CollectionAssert.AreEqual(
            new[] { earlierId, fallbackId, laterId },
            viewModel.EntryItems
                .Select(entry => entry.EntryId)
                .ToArray());

        viewModel.SelectedSortOption =
            VaultEntrySortOptionViewModel.TimelineNewest;

        CollectionAssert.AreEqual(
            new[] { laterId, fallbackId, earlierId },
            viewModel.EntryItems
                .Select(entry => entry.EntryId)
                .ToArray());
    }

    [TestMethod]
    public async Task PendingDeletion_DisablesTimelineDateAction()
    {
        await using VaultSession session =
            await CreateSessionAsync();

        Guid entryId =
            session.CreateEntry("Delete me").EntryId;

        await session.SaveAsync();
        session.MarkEntryForDeletion(entryId);

        MainVaultViewModel viewModel =
            CreateViewModel(session);

        VaultEntryListItemViewModel entry =
            viewModel.EntryItems.Single(item =>
                item.EntryId == entryId);

        Assert.IsFalse(
            entry.SetTimelineDateCommand.CanExecute(null));
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

    private static Argon2idParameters TestKdfParameters =>
        new()
        {
            Version = Argon2idParameters.SupportedVersion,
            MemorySizeKiB = 19 * 1024,
            Iterations = 2,
            DegreeOfParallelism = 1
        };
}
