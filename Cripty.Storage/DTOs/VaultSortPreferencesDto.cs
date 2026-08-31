using Cripty.Core.Vaults;

namespace Cripty.Storage.DTOs;

public sealed class VaultSortPreferencesDto
{
    public required EntrySortMode AllEntriesSortMode
    {
        get;
        init;
    }

    public required EntrySortMode RootSortMode
    {
        get;
        init;
    }

    public required Dictionary<Guid, EntrySortMode>
        FolderSortModes
    {
        get;
        init;
    }
}
