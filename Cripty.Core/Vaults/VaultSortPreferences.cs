using System.Collections.ObjectModel;

namespace Cripty.Core.Vaults;

public sealed class VaultSortPreferences
{
    public const EntrySortMode DefaultSortMode =
        EntrySortMode.ModifiedNewest;

    private readonly Dictionary<Guid, EntrySortMode>
        _folderSortModes;

    private readonly IReadOnlyDictionary<Guid, EntrySortMode>
        _folderSortModesView;

    public VaultSortPreferences(
        EntrySortMode allEntriesSortMode = DefaultSortMode,
        EntrySortMode rootSortMode = DefaultSortMode,
        IEnumerable<KeyValuePair<Guid, EntrySortMode>>?
            folderSortModes = null)
    {
        AllEntriesSortMode = allEntriesSortMode;
        RootSortMode = rootSortMode;

        _folderSortModes = folderSortModes is null
            ? []
            : new Dictionary<Guid, EntrySortMode>(
                folderSortModes);

        _folderSortModesView =
            new ReadOnlyDictionary<Guid, EntrySortMode>(
                _folderSortModes);
    }

    public EntrySortMode AllEntriesSortMode
    {
        get;
        private set;
    }

    public EntrySortMode RootSortMode
    {
        get;
        private set;
    }

    public IReadOnlyDictionary<Guid, EntrySortMode>
        FolderSortModes =>
            _folderSortModesView;

    public EntrySortMode GetFolderSortMode(
        Guid folderId)
    {
        return _folderSortModes.GetValueOrDefault(
            folderId,
            DefaultSortMode);
    }

    internal void SetAllEntriesSortMode(
        EntrySortMode sortMode)
    {
        RequireValidSortMode(sortMode);
        AllEntriesSortMode = sortMode;
    }

    internal void SetRootSortMode(
        EntrySortMode sortMode)
    {
        RequireValidSortMode(sortMode);
        RootSortMode = sortMode;
    }

    internal void SetFolderSortMode(
        Guid folderId,
        EntrySortMode sortMode)
    {
        RequireValidSortMode(sortMode);
        _folderSortModes[folderId] = sortMode;
    }

    internal void RemoveFolder(
        Guid folderId)
    {
        _folderSortModes.Remove(folderId);
    }

    private static void RequireValidSortMode(
        EntrySortMode sortMode)
    {
        if (!Enum.IsDefined(sortMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(sortMode),
                sortMode,
                "The entry sort mode is not supported.");
        }
    }
}
