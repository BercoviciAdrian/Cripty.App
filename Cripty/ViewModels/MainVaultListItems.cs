using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cripty.Application.Vaults;

namespace Cripty.ViewModels;

public enum VaultFolderFilterKind
{
    AllEntries,
    Root,
    Folder
}

public enum VaultEntrySortKind
{
    NameAscending,
    NameDescending,
    CreatedNewest,
    CreatedOldest,
    TimelineNewest,
    TimelineOldest,
    ModifiedNewest,
    ModifiedOldest
}

public sealed class VaultEntrySortOptionViewModel
{
    private VaultEntrySortOptionViewModel(
        VaultEntrySortKind kind,
        string name)
    {
        Kind = kind;
        Name = name;
    }

    public VaultEntrySortKind Kind { get; }

    public string Name { get; }

    public static VaultEntrySortOptionViewModel
        NameAscending
    { get; } =
        new(
            VaultEntrySortKind.NameAscending,
            "NAME · A–Z");

    public static VaultEntrySortOptionViewModel
        NameDescending
    { get; } =
        new(
            VaultEntrySortKind.NameDescending,
            "NAME · Z–A");

    public static VaultEntrySortOptionViewModel
        CreatedNewest
    { get; } =
        new(
            VaultEntrySortKind.CreatedNewest,
            "CREATED · NEWEST");

    public static VaultEntrySortOptionViewModel
        CreatedOldest
    { get; } =
        new(
            VaultEntrySortKind.CreatedOldest,
            "CREATED · OLDEST");

    public static VaultEntrySortOptionViewModel
        TimelineNewest
    { get; } =
        new(
            VaultEntrySortKind.TimelineNewest,
            "TIMELINE · NEWEST");

    public static VaultEntrySortOptionViewModel
        TimelineOldest
    { get; } =
        new(
            VaultEntrySortKind.TimelineOldest,
            "TIMELINE · OLDEST");

    public static VaultEntrySortOptionViewModel
        ModifiedNewest
    { get; } =
        new(
            VaultEntrySortKind.ModifiedNewest,
            "MODIFIED · NEWEST");

    public static VaultEntrySortOptionViewModel
        ModifiedOldest
    { get; } =
        new(
            VaultEntrySortKind.ModifiedOldest,
            "MODIFIED · OLDEST");

    public static IReadOnlyList<
        VaultEntrySortOptionViewModel> All
    { get; } =
        [
            NameAscending,
            NameDescending,
            CreatedNewest,
            CreatedOldest,
            TimelineNewest,
            TimelineOldest,
            ModifiedNewest,
            ModifiedOldest
        ];
}

public abstract class VaultFolderTreeItemViewModel :
    ViewModelBase
{
}

public partial class VaultFolderListItemViewModel :
    VaultFolderTreeItemViewModel
{
    private readonly Action<VaultFolderListItemViewModel>
        _select;

    private readonly Action<VaultFolderListItemViewModel>
        _toggleExpansion;

    private readonly Action<VaultFolderListItemViewModel>?
        _toggleCopySelection;

    private readonly Action<VaultFolderListItemViewModel>?
        _newEntry;

    private readonly Action<VaultFolderListItemViewModel>?
        _newFolder;

    private readonly Action<VaultFolderListItemViewModel>?
        _move;

    private readonly Action<VaultFolderListItemViewModel>?
        _delete;

    private readonly Action<VaultFolderListItemViewModel>?
        _rename;

    public VaultFolderListItemViewModel(
        VaultFolderFilterKind kind,
        Guid? folderId,
        Guid? parentFolderId,
        string name,
        int depth,
        int entryCount,
        bool isExpandable,
        bool isExpanded,
        Action<VaultFolderListItemViewModel> select,
        Action<VaultFolderListItemViewModel> toggleExpansion,
        bool isCopySelectionMode = false,
        bool isCopySelected = false,
        Action<VaultFolderListItemViewModel>?
            toggleCopySelection = null,
        Action<VaultFolderListItemViewModel>?
            newEntry = null,
        Action<VaultFolderListItemViewModel>?
            newFolder = null,
        Action<VaultFolderListItemViewModel>?
            move = null,
        Action<VaultFolderListItemViewModel>?
            delete = null,
        Action<VaultFolderListItemViewModel>?
            rename = null)
    {
        Kind = kind;
        FolderId = folderId;
        ParentFolderId = parentFolderId;
        Name = name;
        IndentWidth = Math.Max(0, depth) * 14;
        EntryCountText = FormatCount(entryCount);
        IsExpandable = isExpandable;
        IsExpanded = isExpanded;
        IsCopySelectionMode = isCopySelectionMode;
        IsCopySelected = isCopySelected;

        _select = select ??
            throw new ArgumentNullException(
                nameof(select));

        _toggleExpansion = toggleExpansion ??
            throw new ArgumentNullException(
                nameof(toggleExpansion));

        _toggleCopySelection =
            toggleCopySelection;

        _newEntry = newEntry;
        _newFolder = newFolder;
        _move = move;
        _delete = delete;
        _rename = rename;
    }

    public VaultFolderFilterKind Kind { get; }

    public Guid? FolderId { get; }

    public Guid? ParentFolderId { get; }

    public string Name { get; }

    public double IndentWidth { get; }

    public string EntryCountText { get; }

    public bool IsExpandable { get; }

    public bool IsExpanded { get; }

    public string ExpansionGlyph =>
        !IsExpandable
            ? string.Empty
            : IsExpanded
                ? "▾"
                : "▸";

    public bool IsFolder =>
        Kind == VaultFolderFilterKind.Folder;

    public bool IsCopySelectionMode { get; }

    public bool CanCopySelect =>
        IsCopySelectionMode &&
        IsFolder;

    public bool CanCreateInFolder =>
        !IsCopySelectionMode &&
        (Kind is VaultFolderFilterKind.Root or
            VaultFolderFilterKind.Folder) &&
        _newEntry is not null &&
        _newFolder is not null;

    public bool CanMoveOrDelete =>
        CanCreateInFolder &&
        IsFolder &&
        _move is not null &&
        _delete is not null;

    public bool CanRename =>
        CanCreateInFolder &&
        IsFolder &&
        _rename is not null;

    [ObservableProperty]
    public partial bool IsSelected
    {
        get;
        private set;
    }

    [ObservableProperty]
    public partial bool IsCopySelected
    {
        get;
        private set;
    }

    [RelayCommand]
    private void Select()
    {
        _select(this);
    }

    [RelayCommand]
    private void ToggleExpansion()
    {
        if (IsExpandable)
        {
            _toggleExpansion(this);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCopySelect))]
    private void ToggleCopySelection()
    {
        _toggleCopySelection?.Invoke(this);
    }

    [RelayCommand(CanExecute = nameof(CanCreateInFolder))]
    private void NewEntry()
    {
        _newEntry?.Invoke(this);
    }

    [RelayCommand(CanExecute = nameof(CanCreateInFolder))]
    private void NewFolder()
    {
        _newFolder?.Invoke(this);
    }

    [RelayCommand(CanExecute = nameof(CanMoveOrDelete))]
    private void Move()
    {
        _move?.Invoke(this);
    }

    [RelayCommand(CanExecute = nameof(CanMoveOrDelete))]
    private void Delete()
    {
        _delete?.Invoke(this);
    }

    [RelayCommand(CanExecute = nameof(CanRename))]
    private void Rename()
    {
        _rename?.Invoke(this);
    }

    internal void SetSelected(
        bool isSelected)
    {
        IsSelected = isSelected;
    }

    internal void SetCopySelected(
        bool isCopySelected)
    {
        IsCopySelected = isCopySelected;
    }

    private static string FormatCount(
        int entryCount)
    {
        return entryCount == 1
            ? "1 ENTRY"
            : $"{entryCount} ENTRIES";
    }
}

public partial class VaultFolderEntryListItemViewModel :
    VaultFolderTreeItemViewModel
{
    private readonly Action<
        VaultFolderEntryListItemViewModel> _select;

    private readonly Action<
        VaultFolderEntryListItemViewModel>? _rename;

    private readonly Action<
        VaultFolderEntryListItemViewModel>? _setTimelineDate;

    public VaultFolderEntryListItemViewModel(
        Guid entryId,
        Guid? folderId,
        string name,
        int depth,
        EntrySessionState sessionState,
        Action<VaultFolderEntryListItemViewModel> select,
        bool isCopySelectionMode = false,
        bool isCopySelected = false,
        Action<VaultFolderEntryListItemViewModel>?
            rename = null,
        Action<VaultFolderEntryListItemViewModel>?
            setTimelineDate = null)
    {
        EntryId = entryId;
        FolderId = folderId;
        Name = name;
        IndentWidth = Math.Max(0, depth) * 14;

        IsPendingDeletion =
            sessionState.IsPendingDeletion;

        IsNewEntry =
            !IsPendingDeletion &&
            sessionState.ChangeKind ==
            EntryChangeKind.New;

        IsModifiedEntry =
            !IsPendingDeletion &&
            sessionState.ChangeKind ==
            EntryChangeKind.Modified;

        IsCopySelectionMode = isCopySelectionMode;
        IsCopySelected = isCopySelected;

        _select = select ??
            throw new ArgumentNullException(
                nameof(select));

        _rename = rename;
        _setTimelineDate = setTimelineDate;
    }

    public Guid EntryId { get; }

    public Guid? FolderId { get; }

    public string Name { get; }

    public double IndentWidth { get; }

    public bool IsPendingDeletion { get; }

    public bool IsNewEntry { get; }

    public bool IsModifiedEntry { get; }

    public bool IsCopySelectionMode { get; }

    public bool IsCopySelectable =>
        IsCopySelectionMode &&
        !IsPendingDeletion;

    public bool CanRename =>
        !IsCopySelectionMode &&
        !IsPendingDeletion &&
        _rename is not null;

    public bool CanSetTimelineDate =>
        !IsCopySelectionMode &&
        !IsPendingDeletion &&
        _setTimelineDate is not null;

    [ObservableProperty]
    public partial bool IsSelected
    {
        get;
        private set;
    }

    [ObservableProperty]
    public partial bool IsCopySelected
    {
        get;
        private set;
    }

    [RelayCommand]
    private void Select()
    {
        _select(this);
    }

    [RelayCommand(CanExecute = nameof(CanRename))]
    private void Rename()
    {
        _rename?.Invoke(this);
    }

    [RelayCommand(CanExecute = nameof(CanSetTimelineDate))]
    private void SetTimelineDate()
    {
        _setTimelineDate?.Invoke(this);
    }

    internal void SetSelected(
        bool isSelected)
    {
        IsSelected = isSelected;
    }

    internal void SetCopySelected(
        bool isCopySelected)
    {
        IsCopySelected = isCopySelected;
    }
}

public partial class VaultTagListItemViewModel :
    ViewModelBase
{
    private readonly Action<VaultTagListItemViewModel>
        _select;

    private readonly Action<VaultTagListItemViewModel>?
        _rename;

    public VaultTagListItemViewModel(
        Guid? tagId,
        string name,
        int entryCount,
        Action<VaultTagListItemViewModel> select,
        Action<VaultTagListItemViewModel>?
            rename = null)
    {
        TagId = tagId;
        Name = name;
        EntryCountText = entryCount == 1
            ? "1 ENTRY"
            : $"{entryCount} ENTRIES";

        _select = select ??
            throw new ArgumentNullException(
                nameof(select));

        _rename = rename;
    }

    public Guid? TagId { get; }

    public string Name { get; }

    public string EntryCountText { get; }

    public bool IsTag =>
        TagId.HasValue;

    public bool CanRename =>
        IsTag &&
        _rename is not null;

    [ObservableProperty]
    public partial bool IsSelected
    {
        get;
        private set;
    }

    [RelayCommand]
    private void Select()
    {
        _select(this);
    }

    [RelayCommand(CanExecute = nameof(CanRename))]
    private void Rename()
    {
        _rename?.Invoke(this);
    }

    internal void SetSelected(
        bool isSelected)
    {
        IsSelected = isSelected;
    }
}

public partial class VaultEntryListItemViewModel :
    ViewModelBase
{
    private readonly Action<VaultEntryListItemViewModel>
        _select;

    private readonly Action<VaultEntryListItemViewModel>?
        _setTimelineDate;

    public VaultEntryListItemViewModel(
        Guid entryId,
        string name,
        string locationText,
        string tagSummary,
        long revision,
        DateTimeOffset createdUtc,
        DateTimeOffset modifiedUtc,
        DateOnly? timelineDateOverride,
        EntrySessionState sessionState,
        Action<VaultEntryListItemViewModel> select,
        bool isCopySelectionMode = false,
        bool isCopySelected = false,
        Action<VaultEntryListItemViewModel>?
            setTimelineDate = null)
    {
        EntryId = entryId;
        Name = name;
        LocationText = locationText;
        TagSummary = tagSummary;
        RevisionText = $"REVISION {revision}";

        IsPendingDeletion =
            sessionState.IsPendingDeletion;

        IsNewEntry =
            !IsPendingDeletion &&
            sessionState.ChangeKind ==
            EntryChangeKind.New;

        IsModifiedEntry =
            !IsPendingDeletion &&
            sessionState.ChangeKind ==
            EntryChangeKind.Modified;

        IsCopySelectionMode = isCopySelectionMode;
        IsCopySelected = isCopySelected;

        CreatedText =
            $"CREAT {createdUtc.ToLocalTime():yyyy-MM-dd HH:mm}";

        ModifiedText =
            $"MODIF {modifiedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";

        TimelineText = timelineDateOverride.HasValue
            ? $"TIMELINE {timelineDateOverride.Value:yyyy-MM-dd}"
            : string.Empty;

        HasTimelineDateOverride =
            timelineDateOverride.HasValue;

        _select = select ??
            throw new ArgumentNullException(
                nameof(select));

        _setTimelineDate = setTimelineDate;
    }

    public Guid EntryId { get; }

    public string Name { get; }

    public string LocationText { get; }

    public string TagSummary { get; }

    public string RevisionText { get; }

    public string CreatedText { get; }

    public string ModifiedText { get; }

    public string TimelineText { get; }

    public bool HasTimelineDateOverride { get; }

    public bool IsPendingDeletion { get; }

    public bool IsNewEntry { get; }

    public bool IsModifiedEntry { get; }

    public bool IsCopySelectionMode { get; }

    public bool IsCopySelectable =>
        IsCopySelectionMode &&
        !IsPendingDeletion;

    public bool CanSetTimelineDate =>
        !IsCopySelectionMode &&
        !IsPendingDeletion &&
        _setTimelineDate is not null;

    [ObservableProperty]
    public partial bool IsSelected
    {
        get;
        private set;
    }

    [ObservableProperty]
    public partial bool IsCopySelected
    {
        get;
        private set;
    }

    [RelayCommand]
    private void Select()
    {
        _select(this);
    }

    [RelayCommand(CanExecute = nameof(CanSetTimelineDate))]
    private void SetTimelineDate()
    {
        _setTimelineDate?.Invoke(this);
    }

    internal void SetSelected(
        bool isSelected)
    {
        IsSelected = isSelected;
    }

    internal void SetCopySelected(
        bool isCopySelected)
    {
        IsCopySelected = isCopySelected;
    }
}
