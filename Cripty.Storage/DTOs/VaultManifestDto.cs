namespace Cripty.Storage.DTOs;

public sealed class VaultManifestDto
{
    public required int SchemaVersion { get; init; }
    public required Guid VaultId { get; init; }
    public required long Generation { get; init; }

    public required List<FolderDescriptorDto> Folders
    {
        get;
        init;
    }

    public required List<TagDescriptorDto> Tags
    {
        get;
        init;
    }

    public required List<EntryDescriptorDto> Entries
    {
        get;
        init;
    }

    // Optional when reading manifest schemas 1 and 2.
    public VaultSortPreferencesDto? SortPreferences
    {
        get;
        init;
    }
}
