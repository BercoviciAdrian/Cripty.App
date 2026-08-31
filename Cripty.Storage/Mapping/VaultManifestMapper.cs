using Cripty.Core.Vaults;
using Cripty.Storage.DTOs;

namespace Cripty.Storage.Mapping;

public static class VaultManifestMapper
{
    public static VaultManifestDto ToDto(
        VaultManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        return new VaultManifestDto
        {
            SchemaVersion = manifest.SchemaVersion,
            VaultId = manifest.VaultId,
            Generation = manifest.Generation,

            Folders = manifest.Folders
                .Select(ToDto)
                .ToList(),

            Tags = manifest.Tags
                .Select(ToDto)
                .ToList(),

            Entries = manifest.Entries
                .Select(ToDto)
                .ToList()
        };
    }

    public static VaultManifest ToDomain(
        VaultManifestDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        List<FolderDescriptorDto> folders =
            dto.Folders
            ?? throw new InvalidDataException(
                "The manifest folders collection is missing.");

        List<TagDescriptorDto> tags =
            dto.Tags
            ?? throw new InvalidDataException(
                "The manifest tags collection is missing.");

        List<EntryDescriptorDto> entries =
            dto.Entries
            ?? throw new InvalidDataException(
                "The manifest entries collection is missing.");

        return new VaultManifest(
            dto.SchemaVersion,
            dto.VaultId,
            dto.Generation,
            folders.Select(ToDomain),
            tags.Select(ToDomain),
            entries.Select(ToDomain));
    }

    private static FolderDescriptorDto ToDto(
        FolderDescriptor folder)
    {
        return new FolderDescriptorDto
        {
            FolderId = folder.FolderId,
            Name = folder.Name,
            ParentFolderId = folder.ParentFolderId
        };
    }

    private static FolderDescriptor ToDomain(
        FolderDescriptorDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        return new FolderDescriptor(
            dto.FolderId,
            dto.Name,
            dto.ParentFolderId);
    }

    private static TagDescriptorDto ToDto(
        TagDescriptor tag)
    {
        return new TagDescriptorDto
        {
            TagId = tag.TagId,
            Name = tag.Name,
            Color = tag.Color
        };
    }

    private static TagDescriptor ToDomain(
        TagDescriptorDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        return new TagDescriptor(
            dto.TagId,
            dto.Name,
            dto.Color);
    }

    private static EntryDescriptorDto ToDto(
        EntryDescriptor entry)
    {
        return new EntryDescriptorDto
        {
            EntryId = entry.EntryId,
            Name = entry.Name,
            FolderId = entry.FolderId,
            TagIds = entry.TagIds.ToList(),
            Revision = entry.Revision,
            CreatedUtc = entry.CreatedUtc,
            ModifiedUtc = entry.ModifiedUtc,
            TimelineDateOverride =
                entry.TimelineDateOverride
        };
    }

    private static EntryDescriptor ToDomain(
        EntryDescriptorDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        List<Guid> tagIds =
            dto.TagIds
            ?? throw new InvalidDataException(
                $"Entry '{dto.EntryId}' has no tag collection.");

        return new EntryDescriptor(
            dto.EntryId,
            dto.Name,
            dto.FolderId,
            tagIds,
            dto.Revision,
            dto.CreatedUtc,
            dto.ModifiedUtc,
            dto.TimelineDateOverride);
    }
}
