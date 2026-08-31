using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cripty.Application.Vaults;
using Cripty.Core.Entries;
using Cripty.Core.Vaults;
using Cripty.Cryptography.Keys;
using Cripty.Models;
using Cripty.Passwords;
using Cripty.Services;

namespace Cripty.ViewModels;

public partial class MainVaultViewModel :
    ViewModelBase
{
    private readonly VaultSession _session;
    private readonly Func<Task> _lockVault;

    private readonly VaultCopyService _vaultCopyService;
    private readonly VaultLocationService _vaultLocationService;
    private readonly VaultDiscoveryService _vaultDiscoveryService;

    private readonly HashSet<Guid>
        _expandedFolderIds = [];

    private readonly HashSet<Guid>
        _expandedMoveDestinationIds = [];

    private readonly HashSet<Guid>
        _copySelectedEntryIds = [];

    private readonly HashSet<Guid>
        _copySelectedFolderIds = [];

    private bool _isRootExpanded = true;
    private bool _isMoveRootExpanded = true;
    private bool _isLoadingSortPreference;

    private int _selectedPasswordKdfMemorySizeKiB;
    private int _selectedPasswordKdfIterations;
    private int _selectedPasswordKdfParallelism;
    private CancellationTokenSource?
        _passwordChangeCancellation;

    private VaultFolderListItemViewModel?
        _selectedFolder;

    private VaultTagListItemViewModel?
        _selectedTag;

    private VaultEntryListItemViewModel?
        _selectedEntry;

    private VaultMoveDestinationItemViewModel?
        _selectedMoveDestination;

    private VaultCopyTargetItemViewModel?
        _selectedCopyTarget;

    private MoveOperationKind _moveOperationKind;
    private Guid _moveItemId;
    private Guid? _moveCurrentParentFolderId;
    private string _moveItemName = string.Empty;

    private DialogAction _dialogAction;

    private Guid _timelineDateEntryId;
    private DateOnly? _originalTimelineDateOverride;
    private DateOnly _timelineDateFallback;

    public MainVaultViewModel(
        string vaultName,
        VaultSession session,
        Func<Task> lockVault,
        VaultCopyService? vaultCopyService = null,
        VaultLocationService? vaultLocationService = null,
        VaultDiscoveryService? vaultDiscoveryService = null)
    {
        if (string.IsNullOrWhiteSpace(
                vaultName))
        {
            throw new ArgumentException(
                "The vault name cannot be empty.",
                nameof(vaultName));
        }

        VaultName = vaultName;

        _session = session ??
            throw new ArgumentNullException(
                nameof(session));

        _lockVault = lockVault ??
            throw new ArgumentNullException(
                nameof(lockVault));

        _vaultCopyService =
            vaultCopyService ?? new VaultCopyService();

        _vaultLocationService =
            vaultLocationService ?? new VaultLocationService();

        _vaultDiscoveryService =
            vaultDiscoveryService ?? new VaultDiscoveryService();

        VaultDirectoryPath =
            _session.VaultDirectoryPath;

        VaultIdText =
            _session.VaultId.ToString("D");

        ManifestSchemaText =
            $"MANIFEST SCHEMA {_session.ManifestSchemaVersion}";

        RefreshBrowser();

        SaveStatusText =
            $"VAULT READY · GENERATION {_session.ManifestGeneration}";
    }

    public string VaultName { get; }

    public string VaultDirectoryPath { get; }

    public string VaultIdText { get; }

    public string ManifestSchemaText { get; }

    public ObservableCollection<
        VaultFolderTreeItemViewModel> FolderItems
    { get; } = [];

    public ObservableCollection<
        VaultTagListItemViewModel> TagItems
    { get; } = [];

    public ObservableCollection<
        VaultEntryListItemViewModel> EntryItems
    { get; } = [];

    [ObservableProperty]
    public partial EntryEditorViewModel? EntryEditor
    {
        get;
        private set;
    }

    public bool HasOpenEntry =>
        EntryEditor is not null;

    public bool IsEntryBrowserVisible =>
        EntryEditor is null;

    public ObservableCollection<
        VaultMoveDestinationItemViewModel>
        MoveDestinationItems
    { get; } = [];

    public ObservableCollection<
        VaultCopyTargetItemViewModel> CopyTargetVaults
    { get; } = [];

    public IReadOnlyList<
        VaultEntrySortOptionViewModel> SortOptions
    { get; } =
        VaultEntrySortOptionViewModel.All;

    [ObservableProperty]
    public partial bool IsBusy
    {
        get;
        private set;
    }

    [ObservableProperty]
    public partial bool IsSaving
    {
        get;
        private set;
    }

    [ObservableProperty]
    public partial bool HasUnsavedChanges
    {
        get;
        private set;
    }

    [ObservableProperty]
    public partial bool HasSaveWork
    {
        get;
        private set;
    }

    [ObservableProperty]
    public partial bool HasEntryEditorValidationError
    {
        get;
        private set;
    }

    [ObservableProperty]
    public partial string SaveStatusText
    {
        get;
        private set;
    } = "VAULT READY";

    [ObservableProperty]
    public partial string ManifestGenerationText
    {
        get;
        private set;
    } = string.Empty;

    [ObservableProperty]
    public partial string CurrentFilterTitle
    {
        get;
        private set;
    } = "ALL ENTRIES";

    [ObservableProperty]
    public partial string CurrentFilterDescription
    {
        get;
        private set;
    } = "NO TAG FILTER";

    [ObservableProperty]
    public partial string EntryCountText
    {
        get;
        private set;
    } = "0 ENTRIES";

    [ObservableProperty]
    public partial string SearchText
    {
        get;
        set;
    } = string.Empty;

    [ObservableProperty]
    public partial VaultEntrySortOptionViewModel?
        SelectedSortOption
    {
        get;
        set;
    } = VaultEntrySortOptionViewModel.ModifiedNewest;

    [ObservableProperty]
    public partial bool HasEntries
    {
        get;
        private set;
    }

    public bool HasNoEntries =>
        !HasEntries;

    [ObservableProperty]
    public partial string? ErrorMessage
    {
        get;
        private set;
    }

    public bool HasError =>
        !string.IsNullOrWhiteSpace(
            ErrorMessage);

    [ObservableProperty]
    public partial bool IsMoreOptionsOpen
    {
        get;
        private set;
    }

    [ObservableProperty]
    public partial bool IsPasswordChangeOpen
    {
        get;
        private set;
    }

    [ObservableProperty]
    public partial bool IsChangingPassword
    {
        get;
        private set;
    }

    [ObservableProperty]
    public partial double PasswordChangeProgressPercentage
    {
        get;
        private set;
    }

    [ObservableProperty]
    public partial string PasswordChangeProgressPercentageText
    {
        get;
        private set;
    } = "0%";

    [ObservableProperty]
    public partial string PasswordChangeProgressStatusText
    {
        get;
        private set;
    } = "Preparing fresh root key...";

    [ObservableProperty]
    public partial string NewPassword
    {
        get;
        set;
    } = string.Empty;

    [ObservableProperty]
    public partial string ConfirmNewPassword
    {
        get;
        set;
    } = string.Empty;

    [ObservableProperty]
    public partial int NewPasswordCaretIndex
    {
        get;
        set;
    }

    [ObservableProperty]
    public partial int ConfirmNewPasswordCaretIndex
    {
        get;
        set;
    }

    [ObservableProperty]
    public partial bool IsNewPasswordVisible
    {
        get;
        private set;
    }

    [ObservableProperty]
    public partial bool IsConfirmNewPasswordVisible
    {
        get;
        private set;
    }

    [ObservableProperty]
    public partial string? PasswordChangeErrorMessage
    {
        get;
        private set;
    }

    [ObservableProperty]
    public partial bool IsPasswordKdfSettingsOpen
    {
        get;
        private set;
    }

    [ObservableProperty]
    public partial double PasswordKdfDraftMemorySizeMiB
    {
        get;
        set;
    }

    [ObservableProperty]
    public partial double PasswordKdfDraftIterations
    {
        get;
        set;
    }

    [ObservableProperty]
    public partial double PasswordKdfDraftParallelism
    {
        get;
        set;
    }

    [ObservableProperty]
    public partial bool IsMoveDialogOpen
    {
        get;
        private set;
    }

    [ObservableProperty]
    public partial string MoveDialogTitle
    {
        get;
        private set;
    } = string.Empty;

    [ObservableProperty]
    public partial string MoveDialogDescription
    {
        get;
        private set;
    } = string.Empty;

    [ObservableProperty]
    public partial string MoveItemName
    {
        get;
        private set;
    } = string.Empty;

    [ObservableProperty]
    public partial string MoveCurrentLocationText
    {
        get;
        private set;
    } = string.Empty;

    [ObservableProperty]
    public partial string MoveDestinationText
    {
        get;
        private set;
    } = "NO DESTINATION SELECTED";

    [ObservableProperty]
    public partial string? MoveDialogErrorMessage
    {
        get;
        private set;
    }

    [ObservableProperty]
    public partial bool IsCopySelectionMode
    {
        get;
        private set;
    }

    [ObservableProperty]
    public partial bool IsCopyDialogOpen
    {
        get;
        private set;
    }

    [ObservableProperty]
    public partial int CopySelectedEntryCount
    {
        get;
        private set;
    }

    [ObservableProperty]
    public partial bool IsDiscoveringCopyTargets
    {
        get;
        private set;
    }

    [ObservableProperty]
    public partial bool IsCopying
    {
        get;
        private set;
    }

    [ObservableProperty]
    public partial string CopyPassword
    {
        get;
        set;
    } = string.Empty;

    [ObservableProperty]
    public partial int CopyPasswordCaretIndex
    {
        get;
        set;
    }

    [ObservableProperty]
    public partial bool IsCopyPasswordVisible
    {
        get;
        private set;
    }

    [ObservableProperty]
    public partial string? CopyDialogErrorMessage
    {
        get;
        private set;
    }

    [ObservableProperty]
    public partial bool IsTimelineDateDialogOpen
    {
        get;
        private set;
    }

    [ObservableProperty]
    public partial DateTime? TimelineDateSelection
    {
        get;
        set;
    }

    [ObservableProperty]
    public partial string TimelineDateDialogDescription
    {
        get;
        private set;
    } = string.Empty;

    [ObservableProperty]
    public partial string TimelineDateFallbackText
    {
        get;
        private set;
    } = string.Empty;

    [ObservableProperty]
    public partial bool HasTimelineDateOverride
    {
        get;
        private set;
    }

    [ObservableProperty]
    public partial bool IsDialogOpen
    {
        get;
        private set;
    }

    [ObservableProperty]
    public partial string DialogTitle
    {
        get;
        private set;
    } = string.Empty;

    [ObservableProperty]
    public partial string DialogDescription
    {
        get;
        private set;
    } = string.Empty;

    [ObservableProperty]
    public partial string DialogPrimaryActionText
    {
        get;
        private set;
    } = string.Empty;

    [ObservableProperty]
    public partial bool IsDialogInputVisible
    {
        get;
        private set;
    }

    [ObservableProperty]
    public partial bool IsDialogDestructive
    {
        get;
        private set;
    }

    [ObservableProperty]
    public partial string DialogInput
    {
        get;
        set;
    } = string.Empty;

    [ObservableProperty]
    public partial string? DialogErrorMessage
    {
        get;
        private set;
    }

    public bool HasDialogError =>
        !string.IsNullOrWhiteSpace(
            DialogErrorMessage);

    public bool HasPasswordChangeError =>
        !string.IsNullOrWhiteSpace(
            PasswordChangeErrorMessage);

    public bool HasMoveDialogError =>
        !string.IsNullOrWhiteSpace(
            MoveDialogErrorMessage);

    public bool HasCopyDialogError =>
        !string.IsNullOrWhiteSpace(
            CopyDialogErrorMessage);

    public bool HasCopyTargets =>
        CopyTargetVaults.Count > 0;

    public bool HasNoCopyTargets =>
        !HasCopyTargets &&
        !IsDiscoveringCopyTargets;

    public bool HasCopySelection =>
        CopySelectedEntryCount > 0;

    public string CopySelectionSummaryText
    {
        get
        {
            int entryCount =
                CopySelectedEntryCount;

            int folderCount =
                _copySelectedFolderIds.Count;

            string entries =
                entryCount == 1
                    ? "1 ENTRY"
                    : $"{entryCount} ENTRIES";

            if (folderCount == 0)
            {
                return entries + " SELECTED";
            }

            string folders =
                folderCount == 1
                    ? "1 FOLDER"
                    : $"{folderCount} FOLDERS";

            return $"{entries} · {folders} SELECTED";
        }
    }

    public string SelectedCopyTargetText =>
        _selectedCopyTarget is null
            ? "NO DESTINATION VAULT SELECTED"
            : $"DESTINATION · {_selectedCopyTarget.Name}";

    public char CopyPasswordMaskCharacter =>
        PasswordTextInput.GetMaskCharacter(
            IsCopyPasswordVisible);

    public string CopyPasswordVisibilityActionText =>
        IsCopyPasswordVisible
            ? "HIDE"
            : "SHOW";

    public string CopyActionText =>
        IsCopying
            ? "COPYING..."
            : $"COPY {CopySelectedEntryCount} " +
              (CopySelectedEntryCount == 1
                  ? "ENTRY"
                  : "ENTRIES");

    public string MoveDialogActionText =>
        _moveOperationKind == MoveOperationKind.Folder
            ? "MOVE FOLDER HERE"
            : "MOVE ENTRY HERE";

    public string NewPasswordCharacterCountText =>
        FormatCharacterCount(
            NewPassword.Length);

    public string ConfirmNewPasswordCharacterCountText =>
        FormatCharacterCount(
            ConfirmNewPassword.Length);

    public char NewPasswordMaskCharacter =>
        PasswordTextInput.GetMaskCharacter(
            IsNewPasswordVisible);

    public char ConfirmNewPasswordMaskCharacter =>
        PasswordTextInput.GetMaskCharacter(
            IsConfirmNewPasswordVisible);

    public string NewPasswordVisibilityActionText =>
        IsNewPasswordVisible
            ? "HIDE"
            : "SHOW";

    public string ConfirmNewPasswordVisibilityActionText =>
        IsConfirmNewPasswordVisible
            ? "HIDE"
            : "SHOW";

    public string PasswordChangeActionText =>
        IsChangingPassword
            ? "ROTATING VAULT KEY..."
            : "CHANGE PASSWORD";

    public string PasswordChangeAvailabilityText =>
        HasSaveWork
            ? "Save all pending changes before changing the password."
            : "Generates a new root key and re-encrypts every entry and blob.";

    public int MinimumPasswordKdfMemorySizeMiB =>
        Argon2idParameters.MinimumMemorySizeKiB /
        1024;

    public int MaximumPasswordKdfMemorySizeMiB =>
        Argon2idParameters.MaximumMemorySizeKiB /
        1024;

    public int MinimumPasswordKdfIterations =>
        Argon2idParameters.MinimumIterations;

    public int MaximumPasswordKdfIterations =>
        Argon2idParameters.MaximumIterations;

    public int MinimumPasswordKdfParallelism =>
        Argon2idParameters.MinimumParallelism;

    public int MaximumPasswordKdfParallelism =>
        Argon2idParameters.MaximumParallelism;

    public string MinimumPasswordKdfMemorySizeText =>
        $"{MinimumPasswordKdfMemorySizeMiB} MiB";

    public string MaximumPasswordKdfMemorySizeText =>
        $"{MaximumPasswordKdfMemorySizeMiB} MiB";

    public string PasswordKdfSummaryText =>
        $"ARGON2ID · " +
        $"{_selectedPasswordKdfMemorySizeKiB / 1024} MiB · " +
        $"{FormatCount(_selectedPasswordKdfIterations, "iteration")} · " +
        FormatCount(
            _selectedPasswordKdfParallelism,
            "lane");

    public string PasswordKdfProfileText =>
        UsesRecommendedPasswordKdfParameters()
            ? "DEFAULT PARAMETERS"
            : "CUSTOM PARAMETERS";

    public string PasswordKdfMemoryValueText =>
        $"{ToWholeNumber(PasswordKdfDraftMemorySizeMiB)} MiB";

    public string PasswordKdfIterationsValueText =>
        FormatCount(
            ToWholeNumber(PasswordKdfDraftIterations),
            "iteration");

    public string PasswordKdfParallelismValueText =>
        FormatCount(
            ToWholeNumber(PasswordKdfDraftParallelism),
            "lane");

    public string SaveActionText =>
        IsSaving
            ? "SAVING..."
            : HasEntryEditorValidationError
                ? "FIX ENTRY FIELDS"
            : "SAVE";

    public string DeleteEntryActionText =>
        IsCopySelectionMode
            ? _copySelectedEntryIds.Count > 1
                ? $"DELETE {_copySelectedEntryIds.Count} ENTRIES"
                : "DELETE ENTRY"
            : _selectedEntry?.IsPendingDeletion == true
                ? "UNMARK DELETION"
                : "DELETE ENTRY";

    private bool CanMutateVault()
    {
        return !IsBusy &&
               !IsDialogOpen &&
               !IsPasswordChangeOpen &&
               !IsMoveDialogOpen &&
               !IsCopySelectionMode &&
               !IsCopyDialogOpen &&
               !IsTimelineDateDialogOpen;
    }

    private bool CanEnterCopySelection()
    {
        if (!CanMutateVault() ||
            EntryEditor is not null)
        {
            return false;
        }

        HashSet<Guid> pendingDeletionIds =
            _session.EntriesPendingDeletion.ToHashSet();

        return _session.Entries.Any(entry =>
            !pendingDeletionIds.Contains(entry.EntryId));
    }

    private bool CanChangeCopySelection()
    {
        return IsCopySelectionMode &&
               !IsCopyDialogOpen &&
               !IsBusy;
    }

    private bool CanOpenCopyDialog()
    {
        return CanChangeCopySelection() &&
               !_session.IsManifestDirty &&
               HasCopySelection;
    }

    private bool CanInteractWithCopyDialog()
    {
        return IsCopyDialogOpen &&
               !IsCopying &&
               !IsBusy;
    }

    private bool CanConfirmCopy()
    {
        return CanInteractWithCopyDialog() &&
               !IsDiscoveringCopyTargets &&
               !_session.IsManifestDirty &&
               _selectedCopyTarget is not null &&
               !string.IsNullOrEmpty(CopyPassword) &&
               HasCopySelection;
    }

    private bool CanMoveFolder()
    {
        return CanMutateVault() &&
               _selectedFolder?.IsFolder == true;
    }

    private bool CanMoveEntry()
    {
        return CanMutateVault() &&
               _selectedEntry is not null &&
               !_selectedEntry.IsPendingDeletion;
    }

    private bool CanOpenTimelineDate()
    {
        return CanMutateVault() &&
               _selectedEntry is not null &&
               !_selectedEntry.IsPendingDeletion;
    }

    private bool CanInteractWithTimelineDateDialog()
    {
        return IsTimelineDateDialogOpen &&
               !IsBusy;
    }

    private bool CanApplyTimelineDate()
    {
        if (!CanInteractWithTimelineDateDialog() ||
            TimelineDateSelection is not DateTime selection)
        {
            return false;
        }

        DateOnly selectedDate = new(
            selection.Year,
            selection.Month,
            selection.Day);

        DateOnly? newOverride =
            selectedDate == _timelineDateFallback
                ? null
                : selectedDate;

        return newOverride !=
            _originalTimelineDateOverride;
    }

    private bool CanClearTimelineDate()
    {
        return CanInteractWithTimelineDateDialog() &&
               HasTimelineDateOverride;
    }

    private bool CanOpenEntry()
    {
        return CanMutateVault() &&
               EntryEditor is null &&
               _selectedEntry is not null &&
               !_selectedEntry.IsPendingDeletion;
    }

    private bool CanDeleteFolder()
    {
        return CanMutateVault() &&
               _selectedFolder?.IsFolder == true;
    }

    private bool CanDeleteTag()
    {
        return CanMutateVault() &&
               _selectedTag?.IsTag == true;
    }

    private bool CanDeleteEntry()
    {
        if (IsCopySelectionMode)
        {
            return !IsBusy &&
                   !IsDialogOpen &&
                   !IsCopyDialogOpen &&
                   _copySelectedEntryIds.Count > 0;
        }

        return CanMutateVault() &&
               _selectedEntry is not null;
    }

    private bool CanSave()
    {
        return !IsBusy &&
               !IsCopySelectionMode &&
               !IsCopyDialogOpen &&
               !HasEntryEditorValidationError &&
               HasSaveWork;
    }

    private bool CanConfirmDialog()
    {
        if (!IsDialogOpen ||
            IsBusy)
        {
            return false;
        }

        if (!IsDialogInputVisible)
        {
            return true;
        }

        string input = DialogInput.Trim();

        if (input.Length == 0)
        {
            return false;
        }

        return _dialogAction switch
        {
            DialogAction.RenameEntry =>
                !string.Equals(
                    input,
                    _selectedEntry?.Name,
                    StringComparison.Ordinal),

            DialogAction.RenameFolder =>
                !string.Equals(
                    input,
                    _selectedFolder?.Name,
                    StringComparison.Ordinal),

            DialogAction.RenameTag =>
                !string.Equals(
                    input,
                    _selectedTag?.Name,
                    StringComparison.Ordinal),

            _ => true
        };
    }

    private bool CanConfirmMoveDialog()
    {
        return IsMoveDialogOpen &&
               !IsBusy &&
               _selectedMoveDestination?.IsSelectable == true;
    }

    private bool CanOpenPasswordChange()
    {
        return IsMoreOptionsOpen &&
               !IsBusy &&
               !HasSaveWork &&
               !IsDialogOpen &&
               !IsPasswordChangeOpen;
    }

    private bool CanConfirmPasswordChange()
    {
        return IsPasswordChangeOpen &&
               !IsPasswordKdfSettingsOpen &&
               !IsBusy &&
               !string.IsNullOrEmpty(
                   NewPassword) &&
               !string.IsNullOrEmpty(
                   ConfirmNewPassword);
    }

    private bool CanInteractWithPasswordChange()
    {
        return IsPasswordChangeOpen &&
               !IsPasswordKdfSettingsOpen &&
               !IsBusy;
    }

    private bool CanOpenPasswordKdfSettings()
    {
        return IsPasswordChangeOpen &&
               !IsPasswordKdfSettingsOpen &&
               !IsBusy;
    }

    private bool CanInteractWithPasswordKdfSettings()
    {
        return IsPasswordChangeOpen &&
               IsPasswordKdfSettingsOpen &&
               !IsBusy;
    }

    partial void OnIsBusyChanged(
        bool value)
    {
        NotifyCommandStates();
    }

    partial void OnIsSavingChanged(
        bool value)
    {
        OnPropertyChanged(
            nameof(SaveActionText));
    }

    partial void OnHasSaveWorkChanged(
        bool value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        OpenPasswordChangeCommand.NotifyCanExecuteChanged();

        OnPropertyChanged(
            nameof(PasswordChangeAvailabilityText));
    }

    partial void OnEntryEditorChanged(
        EntryEditorViewModel? value)
    {
        OnPropertyChanged(
            nameof(HasOpenEntry));

        OnPropertyChanged(
            nameof(IsEntryBrowserVisible));

        OpenEntryCommand.NotifyCanExecuteChanged();
        EnterCopySelectionCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasEntryEditorValidationErrorChanged(
        bool value)
    {
        SaveCommand.NotifyCanExecuteChanged();

        OnPropertyChanged(
            nameof(SaveActionText));
    }

    partial void OnHasEntriesChanged(
        bool value)
    {
        OnPropertyChanged(
            nameof(HasNoEntries));

        EnterCopySelectionCommand.NotifyCanExecuteChanged();
    }

    partial void OnSearchTextChanged(
        string value)
    {
        ApplyEntryFilter();
    }

    partial void OnSelectedSortOptionChanged(
        VaultEntrySortOptionViewModel? value)
    {
        if (_isLoadingSortPreference)
        {
            return;
        }

        if (value is not null &&
            _selectedFolder is not null &&
            value.Kind != GetSelectedSortMode())
        {
            SetSelectedSortMode(value.Kind);
            RecordUnsavedChange("SORT PREFERENCE UPDATED");
        }

        ApplyEntryFilter();
    }

    partial void OnErrorMessageChanged(
        string? value)
    {
        OnPropertyChanged(
            nameof(HasError));
    }

    partial void OnIsMoreOptionsOpenChanged(
        bool value)
    {
        OpenPasswordChangeCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsPasswordChangeOpenChanged(
        bool value)
    {
        NotifyCommandStates();
        ConfirmPasswordChangeCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsChangingPasswordChanged(
        bool value)
    {
        OnPropertyChanged(
            nameof(PasswordChangeActionText));

        ConfirmPasswordChangeCommand.NotifyCanExecuteChanged();
    }

    partial void OnNewPasswordChanged(
        string value)
    {
        NewPasswordCaretIndex = Math.Clamp(
            NewPasswordCaretIndex,
            0,
            value.Length);

        ClearPasswordChangeError();

        OnPropertyChanged(
            nameof(NewPasswordCharacterCountText));

        ConfirmPasswordChangeCommand.NotifyCanExecuteChanged();
    }

    partial void OnConfirmNewPasswordChanged(
        string value)
    {
        ConfirmNewPasswordCaretIndex = Math.Clamp(
            ConfirmNewPasswordCaretIndex,
            0,
            value.Length);

        ClearPasswordChangeError();

        OnPropertyChanged(
            nameof(ConfirmNewPasswordCharacterCountText));

        ConfirmPasswordChangeCommand.NotifyCanExecuteChanged();
    }

    public void InsertNewPasswordSpecialCharacter(
        string character)
    {
        PasswordTextInsertionResult insertion =
            PasswordTextInput.InsertAtCaret(
                NewPassword,
                NewPasswordCaretIndex,
                character);

        NewPassword = insertion.Text;
        NewPasswordCaretIndex = insertion.CaretIndex;
    }

    public void InsertConfirmNewPasswordSpecialCharacter(
        string character)
    {
        PasswordTextInsertionResult insertion =
            PasswordTextInput.InsertAtCaret(
                ConfirmNewPassword,
                ConfirmNewPasswordCaretIndex,
                character);

        ConfirmNewPassword = insertion.Text;
        ConfirmNewPasswordCaretIndex =
            insertion.CaretIndex;
    }

    partial void OnIsNewPasswordVisibleChanged(
        bool value)
    {
        OnPropertyChanged(
            nameof(NewPasswordMaskCharacter));

        OnPropertyChanged(
            nameof(NewPasswordVisibilityActionText));
    }

    partial void OnIsConfirmNewPasswordVisibleChanged(
        bool value)
    {
        OnPropertyChanged(
            nameof(ConfirmNewPasswordMaskCharacter));

        OnPropertyChanged(
            nameof(ConfirmNewPasswordVisibilityActionText));
    }

    partial void OnPasswordChangeErrorMessageChanged(
        string? value)
    {
        OnPropertyChanged(
            nameof(HasPasswordChangeError));
    }

    partial void OnIsPasswordKdfSettingsOpenChanged(
        bool value)
    {
        ConfirmPasswordChangeCommand.NotifyCanExecuteChanged();
        CancelPasswordChangeCommand.NotifyCanExecuteChanged();
        ToggleNewPasswordVisibilityCommand.NotifyCanExecuteChanged();
        ToggleConfirmNewPasswordVisibilityCommand.NotifyCanExecuteChanged();
        OpenPasswordKdfSettingsCommand.NotifyCanExecuteChanged();
        CancelPasswordKdfSettingsCommand.NotifyCanExecuteChanged();
        RestoreDefaultPasswordKdfSettingsCommand.NotifyCanExecuteChanged();
        ApplyPasswordKdfSettingsCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsMoveDialogOpenChanged(
        bool value)
    {
        NotifyCommandStates();
        ConfirmMoveDialogCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsTimelineDateDialogOpenChanged(
        bool value)
    {
        NotifyCommandStates();
        ApplyTimelineDateCommand.NotifyCanExecuteChanged();
        ClearTimelineDateCommand.NotifyCanExecuteChanged();
        CancelTimelineDateDialogCommand.NotifyCanExecuteChanged();
    }

    partial void OnTimelineDateSelectionChanged(
        DateTime? value)
    {
        ApplyTimelineDateCommand.NotifyCanExecuteChanged();
    }

    partial void OnMoveDialogErrorMessageChanged(
        string? value)
    {
        OnPropertyChanged(
            nameof(HasMoveDialogError));
    }

    partial void OnIsCopySelectionModeChanged(
        bool value)
    {
        NotifyCommandStates();
        OnPropertyChanged(nameof(CopySelectionSummaryText));
        OnPropertyChanged(nameof(DeleteEntryActionText));
    }

    partial void OnCopySelectedEntryCountChanged(
        int value)
    {
        OnPropertyChanged(nameof(HasCopySelection));
        OnPropertyChanged(nameof(CopySelectionSummaryText));
        OnPropertyChanged(nameof(CopyActionText));

        OpenCopyDialogCommand.NotifyCanExecuteChanged();
        ConfirmCopyCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsCopyDialogOpenChanged(
        bool value)
    {
        NotifyCommandStates();
        ConfirmCopyCommand.NotifyCanExecuteChanged();
        CancelCopyDialogCommand.NotifyCanExecuteChanged();
        ToggleCopyPasswordVisibilityCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsDiscoveringCopyTargetsChanged(
        bool value)
    {
        OnPropertyChanged(nameof(HasNoCopyTargets));
        ConfirmCopyCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsCopyingChanged(
        bool value)
    {
        OnPropertyChanged(nameof(CopyActionText));
        ConfirmCopyCommand.NotifyCanExecuteChanged();
    }

    partial void OnCopyPasswordChanged(
        string value)
    {
        CopyPasswordCaretIndex = Math.Clamp(
            CopyPasswordCaretIndex,
            0,
            value.Length);

        ClearCopyDialogError();
        ConfirmCopyCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsCopyPasswordVisibleChanged(
        bool value)
    {
        OnPropertyChanged(nameof(CopyPasswordMaskCharacter));
        OnPropertyChanged(nameof(CopyPasswordVisibilityActionText));
    }

    partial void OnCopyDialogErrorMessageChanged(
        string? value)
    {
        OnPropertyChanged(nameof(HasCopyDialogError));
    }

    partial void OnPasswordKdfDraftMemorySizeMiBChanged(
        double value)
    {
        OnPropertyChanged(
            nameof(PasswordKdfMemoryValueText));
    }

    partial void OnPasswordKdfDraftIterationsChanged(
        double value)
    {
        OnPropertyChanged(
            nameof(PasswordKdfIterationsValueText));
    }

    partial void OnPasswordKdfDraftParallelismChanged(
        double value)
    {
        OnPropertyChanged(
            nameof(PasswordKdfParallelismValueText));
    }

    partial void OnIsDialogOpenChanged(
        bool value)
    {
        NotifyCommandStates();
        ConfirmDialogCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsDialogInputVisibleChanged(
        bool value)
    {
        ConfirmDialogCommand.NotifyCanExecuteChanged();
    }

    partial void OnDialogInputChanged(
        string value)
    {
        ClearDialogError();
        ConfirmDialogCommand.NotifyCanExecuteChanged();
    }

    partial void OnDialogErrorMessageChanged(
        string? value)
    {
        OnPropertyChanged(
            nameof(HasDialogError));
    }

    [RelayCommand(CanExecute = nameof(CanMutateVault))]
    private void NewFolder()
    {
        Guid? parentFolderId =
            _selectedFolder?.IsFolder == true
                ? _selectedFolder.FolderId
                : null;

        string location =
            parentFolderId.HasValue
                ? $"inside '{_selectedFolder!.Name}'"
                : "at the vault root";

        OpenInputDialog(
            DialogAction.CreateFolder,
            "NEW FOLDER",
            $"Create a folder {location}.",
            "CREATE FOLDER");
    }

    [RelayCommand(CanExecute = nameof(CanMutateVault))]
    private void NewTag()
    {
        OpenInputDialog(
            DialogAction.CreateTag,
            "NEW TAG",
            "Create a vault-wide tag for organizing entries.",
            "CREATE TAG");
    }

    [RelayCommand(CanExecute = nameof(CanMutateVault))]
    private void NewEntry()
    {
        string location =
            _selectedFolder?.IsFolder == true
                ? $"inside '{_selectedFolder.Name}'"
                : "in ROOT";

        string tagAssignment =
            _selectedTag?.TagId.HasValue == true
                ? $" It will receive the '{_selectedTag.Name}' tag."
                : string.Empty;

        OpenInputDialog(
            DialogAction.CreateEntry,
            "NEW ENTRY",
            $"Create an empty entry {location}.{tagAssignment}",
            "CREATE ENTRY");
    }

    [RelayCommand(CanExecute = nameof(CanOpenEntry))]
    private async Task OpenEntryAsync()
    {
        VaultEntryListItemViewModel selectedEntry =
            _selectedEntry ??
            throw new InvalidOperationException(
                "Select an entry before opening it.");

        ClearError();
        IsBusy = true;

        try
        {
            EntryDescriptor descriptor =
                _session.Entries.Single(entry =>
                    entry.EntryId ==
                    selectedEntry.EntryId);

            VaultEntry entry =
                await _session.GetEntryAsync(
                    selectedEntry.EntryId);

            EntrySessionState entryState =
                _session.GetEntrySessionState(
                    selectedEntry.EntryId);

            VaultEntry? persistedEntry =
                entryState.ChangeKind ==
                EntryChangeKind.New
                    ? null
                    : _session
                        .HasPendingEntryContentChanges(
                            selectedEntry.EntryId)
                        ? await _session
                            .GetPersistedEntryAsync(
                                selectedEntry.EntryId)
                        : entry;

            string locationText =
                BuildFolderPath(
                    descriptor.FolderId,
                    _session.Folders.ToDictionary(
                        folder => folder.FolderId));

            EntryEditorViewModel editor =
                new EntryEditorViewModel(
                    _session,
                    descriptor,
                    entry,
                    persistedEntry,
                    locationText,
                    RecordUnsavedChange,
                    SetEntryEditorValidationState,
                    CloseEntryEditor);

            try
            {
                await editor.InitializeImagesAsync();
                EntryEditor = editor;
            }
            catch
            {
                editor.Dispose();
                throw;
            }
        }
        catch (Exception exception)
            when (IsExpectedOperationFailure(
                exception))
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal async Task OpenEntryFromDoubleTapAsync(
        VaultEntryListItemViewModel entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (IsCopySelectionMode ||
            !EntryItems.Contains(entry))
        {
            return;
        }

        SelectEntry(entry);

        if (CanOpenEntry())
        {
            await OpenEntryAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanMoveFolder))]
    private void OpenFolderMove()
    {
        VaultFolderListItemViewModel folder =
            _selectedFolder ??
            throw new InvalidOperationException(
                "Select a folder before moving it.");

        if (folder.FolderId is not Guid folderId)
        {
            throw new InvalidOperationException(
                "The selected item is not a folder.");
        }

        OpenMoveDialog(
            MoveOperationKind.Folder,
            folderId,
            folder.Name,
            folder.ParentFolderId);
    }

    [RelayCommand(CanExecute = nameof(CanMoveEntry))]
    private void OpenEntryMove()
    {
        VaultEntryListItemViewModel entry =
            _selectedEntry ??
            throw new InvalidOperationException(
                "Select an entry before moving it.");

        EntryDescriptor descriptor =
            _session.Entries.Single(item =>
                item.EntryId == entry.EntryId);

        OpenMoveDialog(
            MoveOperationKind.Entry,
            entry.EntryId,
            entry.Name,
            descriptor.FolderId);
    }

    [RelayCommand(CanExecute = nameof(CanOpenTimelineDate))]
    private void OpenTimelineDate()
    {
        VaultEntryListItemViewModel selectedEntry =
            _selectedEntry ??
            throw new InvalidOperationException(
                "Select an entry before setting its timeline date.");

        EntryDescriptor descriptor =
            _session.Entries.Single(entry =>
                entry.EntryId == selectedEntry.EntryId);

        _timelineDateEntryId = descriptor.EntryId;
        _originalTimelineDateOverride =
            descriptor.TimelineDateOverride;
        _timelineDateFallback =
            DateOnly.FromDateTime(
                descriptor.CreatedUtc.UtcDateTime);

        DateOnly initialDate =
            descriptor.EffectiveTimelineDate;

        TimelineDateSelection =
            new DateTime(
                initialDate.Year,
                initialDate.Month,
                initialDate.Day,
                0,
                0,
                0,
                DateTimeKind.Unspecified);

        TimelineDateDialogDescription =
            $"Choose where '{descriptor.Name}' belongs in timeline " +
            "sorting. Its real creation and modification times remain " +
            "unchanged.";

        TimelineDateFallbackText =
            $"WITHOUT OVERRIDE · CREATED {_timelineDateFallback:yyyy-MM-dd}";

        HasTimelineDateOverride =
            descriptor.TimelineDateOverride.HasValue;

        ClearError();
        IsTimelineDateDialogOpen = true;
    }

    [RelayCommand(
        CanExecute = nameof(CanInteractWithTimelineDateDialog))]
    private void CancelTimelineDateDialog()
    {
        CloseTimelineDateDialog();
    }

    [RelayCommand(CanExecute = nameof(CanApplyTimelineDate))]
    private void ApplyTimelineDate()
    {
        DateTime selection =
            TimelineDateSelection ??
            throw new InvalidOperationException(
                "Choose a timeline date before applying it.");

        DateOnly selectedDate = new(
            selection.Year,
            selection.Month,
            selection.Day);

        DateOnly? timelineDateOverride =
            selectedDate == _timelineDateFallback
                ? null
                : selectedDate;

        Guid entryId = _timelineDateEntryId;

        _session.SetEntryTimelineDate(
            entryId,
            timelineDateOverride);

        CloseTimelineDateDialog();
        RefreshBrowser(selectedEntryId: entryId);

        RecordUnsavedChange(
            timelineDateOverride.HasValue
                ? "TIMELINE DATE UPDATED"
                : "TIMELINE DATE RESET TO CREATION");
    }

    [RelayCommand(CanExecute = nameof(CanClearTimelineDate))]
    private void ClearTimelineDate()
    {
        Guid entryId = _timelineDateEntryId;

        _session.SetEntryTimelineDate(
            entryId,
            timelineDateOverride: null);

        CloseTimelineDateDialog();
        RefreshBrowser(selectedEntryId: entryId);
        RecordUnsavedChange(
            "TIMELINE DATE RESET TO CREATION");
    }

    [RelayCommand(CanExecute = nameof(CanDeleteFolder))]
    private void DeleteFolder()
    {
        OpenConfirmationDialog(
            DialogAction.DeleteFolder,
            "DELETE FOLDER?",
            $"'{_selectedFolder!.Name}' will be removed. " +
            "Its direct entries and child folders will move up " +
            "one level. Nothing is written until you press SAVE.",
            "DELETE FOLDER");
    }

    [RelayCommand(CanExecute = nameof(CanDeleteTag))]
    private void DeleteTag()
    {
        OpenConfirmationDialog(
            DialogAction.DeleteTag,
            "DELETE TAG?",
            $"'{_selectedTag!.Name}' will be removed from the " +
            "vault and from every entry that uses it. Nothing is " +
            "written until you press SAVE.",
            "DELETE TAG");
    }

    [RelayCommand(CanExecute = nameof(CanDeleteEntry))]
    private void DeleteEntry()
    {
        if (IsCopySelectionMode)
        {
            int entryCount =
                _copySelectedEntryIds.Count;

            if (entryCount == 0)
            {
                throw new InvalidOperationException(
                    "Select at least one entry before deleting.");
            }

            string entryText = entryCount == 1
                ? "The selected entry will"
                : $"The {entryCount} selected entries will";

            OpenConfirmationDialog(
                DialogAction.DeleteEntries,
                entryCount == 1
                    ? "DELETE SELECTED ENTRY?"
                    : $"DELETE {entryCount} SELECTED ENTRIES?",
                $"{entryText} be staged for permanent deletion. " +
                "Selected folders and entries included only through " +
                "those folders will not be affected. The encrypted " +
                "entry files are deleted when you press SAVE.",
                entryCount == 1
                    ? "DELETE ENTRY"
                    : $"DELETE {entryCount} ENTRIES");

            return;
        }

        if (_selectedEntry!.IsPendingDeletion)
        {
            UndoSelectedEntryDeletion();
            return;
        }

        OpenConfirmationDialog(
            DialogAction.DeleteEntry,
            "DELETE ENTRY?",
            $"'{_selectedEntry!.Name}' will be staged for permanent " +
            "deletion. Its encrypted entry file is deleted when " +
            "you press SAVE.",
            "DELETE ENTRY");
    }

    [RelayCommand]
    private void CancelMoveDialog()
    {
        if (!IsBusy)
        {
            CloseMoveDialog();
        }
    }

    [RelayCommand(CanExecute = nameof(CanConfirmMoveDialog))]
    private void ConfirmMoveDialog()
    {
        if (_selectedMoveDestination is null)
        {
            MoveDialogErrorMessage =
                "Select a destination folder.";

            return;
        }

        Guid? destinationFolderId =
            _selectedMoveDestination.FolderId;

        try
        {
            if (_moveOperationKind ==
                MoveOperationKind.Entry)
            {
                MoveSelectedEntry(
                    destinationFolderId);
            }
            else if (_moveOperationKind ==
                     MoveOperationKind.Folder)
            {
                MoveSelectedFolder(
                    destinationFolderId);
            }
            else
            {
                throw new InvalidOperationException(
                    "No move operation is active.");
            }
        }
        catch (Exception exception)
            when (IsExpectedOperationFailure(
                exception))
        {
            MoveDialogErrorMessage =
                exception.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEnterCopySelection))]
    private void EnterCopySelection()
    {
        _copySelectedEntryIds.Clear();
        _copySelectedFolderIds.Clear();
        IsCopySelectionMode = true;

        RefreshBrowser();
        RefreshCopySelectionState();
    }

    [RelayCommand(CanExecute = nameof(CanChangeCopySelection))]
    private void CancelCopySelection()
    {
        ExitCopySelection();
    }

    [RelayCommand(CanExecute = nameof(CanChangeCopySelection))]
    private void SelectAllVisibleForCopy()
    {
        foreach (VaultEntryListItemViewModel entry in EntryItems)
        {
            if (!entry.IsPendingDeletion)
            {
                _copySelectedEntryIds.Add(entry.EntryId);
            }
        }

        RefreshCopySelectionState();
    }

    [RelayCommand(CanExecute = nameof(CanChangeCopySelection))]
    private void ClearCopySelection()
    {
        _copySelectedEntryIds.Clear();
        _copySelectedFolderIds.Clear();
        RefreshCopySelectionState();
    }

    [RelayCommand(CanExecute = nameof(CanOpenCopyDialog))]
    private async Task OpenCopyDialogAsync()
    {
        CopyPassword = string.Empty;
        CopyPasswordCaretIndex = 0;
        IsCopyPasswordVisible = false;
        ClearCopyDialogError();

        CopyTargetVaults.Clear();
        _selectedCopyTarget = null;

        OnPropertyChanged(nameof(HasCopyTargets));
        OnPropertyChanged(nameof(HasNoCopyTargets));
        OnPropertyChanged(nameof(SelectedCopyTargetText));

        IsCopyDialogOpen = true;
        IsDiscoveringCopyTargets = true;

        try
        {
            string vaultRootPath =
                _vaultLocationService.LoadVaultRootPath();

            IReadOnlyList<VaultListItem> vaults =
                await _vaultDiscoveryService.DiscoverAsync(
                    vaultRootPath);

            foreach (VaultListItem vault in vaults)
            {
                if (!PathsEqual(
                        vault.DirectoryPath,
                        VaultDirectoryPath))
                {
                    CopyTargetVaults.Add(
                        new VaultCopyTargetItemViewModel(
                            vault,
                            SelectCopyTarget));
                }
            }

            if (CopyTargetVaults.Count == 1)
            {
                SelectCopyTarget(
                    CopyTargetVaults[0]);
            }
        }
        catch (Exception exception)
            when (exception is IOException or
                UnauthorizedAccessException or
                ArgumentException or
                NotSupportedException)
        {
            CopyDialogErrorMessage =
                "Existing vaults could not be discovered: " +
                exception.Message;
        }
        finally
        {
            IsDiscoveringCopyTargets = false;

            OnPropertyChanged(nameof(HasCopyTargets));
            OnPropertyChanged(nameof(HasNoCopyTargets));
        }
    }

    [RelayCommand(CanExecute = nameof(CanInteractWithCopyDialog))]
    private void CancelCopyDialog()
    {
        CloseCopyDialog();
    }

    [RelayCommand(CanExecute = nameof(CanInteractWithCopyDialog))]
    private void ToggleCopyPasswordVisibility()
    {
        IsCopyPasswordVisible =
            !IsCopyPasswordVisible;
    }

    [RelayCommand(CanExecute = nameof(CanConfirmCopy))]
    private async Task ConfirmCopyAsync()
    {
        VaultCopyTargetItemViewModel target =
            _selectedCopyTarget ??
            throw new InvalidOperationException(
                "No destination vault is selected.");

        Guid[] selectedEntryIds =
            [.. _copySelectedEntryIds];

        Guid[] selectedFolderIds =
            [.. _copySelectedFolderIds];

        string submittedPassword = CopyPassword;
        CopyPassword = string.Empty;

        ClearCopyDialogError();
        ClearError();

        IsBusy = true;
        IsCopying = true;

        try
        {
            VaultCopyResult result =
                await Task.Run(() =>
                    _vaultCopyService.CopyAsync(
                        _session,
                        target.DirectoryPath,
                        submittedPassword,
                        selectedEntryIds,
                        selectedFolderIds));

            CloseCopyDialog();
            ExitCopySelection();

            SaveStatusText =
                $"COPIED {result.EntryCount} " +
                (result.EntryCount == 1
                    ? "ENTRY"
                    : "ENTRIES") +
                $" TO {target.Name.ToUpperInvariant()}";
        }
        catch (CryptographicException)
        {
            CopyDialogErrorMessage =
                "The destination password is incorrect, or the " +
                "destination vault is damaged.";
        }
        catch (Exception exception)
            when (IsExpectedOperationFailure(exception))
        {
            CopyDialogErrorMessage = exception.Message;
        }
        finally
        {
            submittedPassword = string.Empty;
            IsCopying = false;
            IsBusy = false;
        }
    }

    public void SelectCopyTargetDirectory(
        string directoryPath)
    {
        if (!IsCopyDialogOpen ||
            IsCopying)
        {
            return;
        }

        try
        {
            string normalizedPath =
                Path.GetFullPath(directoryPath);

            if (PathsEqual(
                    normalizedPath,
                    VaultDirectoryPath))
            {
                CopyDialogErrorMessage =
                    "The source vault cannot also be the destination.";

                return;
            }

            if (!File.Exists(
                    Path.Combine(
                        normalizedPath,
                        "vault.cripty")))
            {
                CopyDialogErrorMessage =
                    "The selected folder does not contain an existing " +
                    "Cripty vault.";

                return;
            }

            VaultCopyTargetItemViewModel? existing =
                CopyTargetVaults.FirstOrDefault(item =>
                    PathsEqual(
                        item.DirectoryPath,
                        normalizedPath));

            if (existing is null)
            {
                existing =
                    new VaultCopyTargetItemViewModel(
                        new VaultListItem(
                            new DirectoryInfo(normalizedPath).Name,
                            normalizedPath),
                        SelectCopyTarget);

                CopyTargetVaults.Add(existing);
            }

            SelectCopyTarget(existing);

            OnPropertyChanged(nameof(HasCopyTargets));
            OnPropertyChanged(nameof(HasNoCopyTargets));
        }
        catch (Exception exception)
            when (exception is IOException or
                UnauthorizedAccessException or
                ArgumentException or
                NotSupportedException)
        {
            CopyDialogErrorMessage =
                "The selected destination could not be inspected: " +
                exception.Message;
        }
    }

    public void ReportCopyDialogError(
        string errorMessage)
    {
        if (IsCopyDialogOpen)
        {
            CopyDialogErrorMessage = errorMessage;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        Guid? openEntryId =
            EntryEditor?.EntryId;

        ClearError();
        IsBusy = true;
        IsSaving = true;
        SaveStatusText = "SAVING VAULT...";

        try
        {
            await _session.SaveAsync();

            RefreshBrowser(
                selectedEntryId:
                    openEntryId);

            if (EntryEditor is not null)
            {
                try
                {
                    await EntryEditor
                        .ReloadFromSessionAsync();
                }
                catch (Exception reloadException)
                    when (IsExpectedOperationFailure(
                        reloadException))
                {
                    ErrorMessage =
                        "The vault was saved, but the open entry " +
                        "could not be refreshed: " +
                        reloadException.Message;

                    CloseEntryEditorWithoutRefresh();
                }
            }

            SaveStatusText =
                $"SAVED {DateTime.Now:HH:mm:ss} · " +
                $"GENERATION {_session.ManifestGeneration}";
        }
        catch (Exception exception)
            when (IsExpectedOperationFailure(
                exception))
        {
            ErrorMessage = exception.Message;
            SaveStatusText = "SAVE FAILED · RETRY REQUIRED";

            RefreshBrowser(
                selectedEntryId:
                    openEntryId);

            if (EntryEditor is not null)
            {
                try
                {
                    await EntryEditor
                        .ReloadFromSessionAsync();
                }
                catch (Exception reloadException)
                    when (IsExpectedOperationFailure(
                        reloadException))
                {
                    ErrorMessage =
                        exception.Message +
                        " The open entry could not be refreshed: " +
                        reloadException.Message;
                }
            }
        }
        finally
        {
            IsSaving = false;
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanMutateVault))]
    private void MoreOptions()
    {
        IsMoreOptionsOpen = true;
    }

    [RelayCommand(CanExecute = nameof(CanOpenPasswordChange))]
    private void OpenPasswordChange()
    {
        ClearPasswordChangeInputs();
        ClearPasswordChangeError();
        LoadCurrentPasswordKdfParameters();
        IsPasswordChangeOpen = true;
    }

    [RelayCommand(
        CanExecute = nameof(CanInteractWithPasswordChange))]
    private void CancelPasswordChange()
    {
        if (!IsBusy)
        {
            ClosePasswordChange();
        }
    }

    [RelayCommand(
        CanExecute = nameof(CanInteractWithPasswordChange))]
    private void ToggleNewPasswordVisibility()
    {
        if (!IsBusy)
        {
            IsNewPasswordVisible =
                !IsNewPasswordVisible;
        }
    }

    [RelayCommand(
        CanExecute = nameof(CanInteractWithPasswordChange))]
    private void ToggleConfirmNewPasswordVisibility()
    {
        if (!IsBusy)
        {
            IsConfirmNewPasswordVisible =
                !IsConfirmNewPasswordVisible;
        }
    }

    [RelayCommand(
        CanExecute = nameof(CanOpenPasswordKdfSettings))]
    private void OpenPasswordKdfSettings()
    {
        CopySelectedPasswordKdfParametersToDraft();
        IsPasswordKdfSettingsOpen = true;
    }

    [RelayCommand(
        CanExecute = nameof(CanInteractWithPasswordKdfSettings))]
    private void CancelPasswordKdfSettings()
    {
        CopySelectedPasswordKdfParametersToDraft();
        IsPasswordKdfSettingsOpen = false;
    }

    [RelayCommand(
        CanExecute = nameof(CanInteractWithPasswordKdfSettings))]
    private void RestoreDefaultPasswordKdfSettings()
    {
        Argon2idParameters recommended =
            Argon2idParameters.Recommended;

        PasswordKdfDraftMemorySizeMiB =
            recommended.MemorySizeKiB /
            1024;

        PasswordKdfDraftIterations =
            recommended.Iterations;

        PasswordKdfDraftParallelism =
            recommended.DegreeOfParallelism;
    }

    [RelayCommand(
        CanExecute = nameof(CanInteractWithPasswordKdfSettings))]
    private void ApplyPasswordKdfSettings()
    {
        Argon2idParameters parameters =
            CreateDraftPasswordKdfParameters();

        parameters.Validate();

        _selectedPasswordKdfMemorySizeKiB =
            parameters.MemorySizeKiB;

        _selectedPasswordKdfIterations =
            parameters.Iterations;

        _selectedPasswordKdfParallelism =
            parameters.DegreeOfParallelism;

        OnPropertyChanged(
            nameof(PasswordKdfSummaryText));

        OnPropertyChanged(
            nameof(PasswordKdfProfileText));

        IsPasswordKdfSettingsOpen = false;
    }

    [RelayCommand(CanExecute = nameof(CanConfirmPasswordChange))]
    private async Task ConfirmPasswordChangeAsync()
    {
        ClearPasswordChangeError();

        if (!string.Equals(
                NewPassword,
                ConfirmNewPassword,
                StringComparison.Ordinal))
        {
            PasswordChangeErrorMessage =
                "The two passwords do not match.";

            return;
        }

        if (Encoding.UTF8.GetByteCount(
                NewPassword) >
            PasswordWrappingKeyDeriver
                .MaximumPasswordByteLength)
        {
            PasswordChangeErrorMessage =
                "The password is too large when encoded as UTF-8. " +
                "The maximum is " +
                $"{PasswordWrappingKeyDeriver.MaximumPasswordByteLength} bytes.";

            return;
        }

        string submittedPassword =
            NewPassword;

        Argon2idParameters submittedKdfParameters =
            CreateSelectedPasswordKdfParameters();

        ClearPasswordChangeInputs();
        ClearError();

        IsBusy = true;
        IsChangingPassword = true;
        PasswordChangeProgressPercentage = 0;
        PasswordChangeProgressPercentageText = "0%";
        PasswordChangeProgressStatusText =
            "Preparing fresh root key...";

        CancellationTokenSource cancellationSource = new();
        _passwordChangeCancellation = cancellationSource;

        Progress<VaultPasswordChangeProgress> progress =
            new(ApplyPasswordChangeProgress);

        try
        {
            await Task.Run(() =>
                _session.ChangePasswordAsync(
                    submittedPassword,
                    submittedKdfParameters,
                    progress,
                    cancellationSource.Token));

            ClosePasswordChange();
            IsMoreOptionsOpen = false;

            SaveStatusText =
                $"PASSWORD CHANGED {DateTime.Now:HH:mm:ss} · " +
                $"GENERATION {_session.ManifestGeneration}";
        }
        catch (OperationCanceledException)
            when (cancellationSource.IsCancellationRequested)
        {
            // Inactivity locking cancels a staged rotation so the
            // original vault can be discarded and closed promptly.
        }
        catch (Exception exception)
            when (IsExpectedOperationFailure(
                exception))
        {
            PasswordChangeErrorMessage =
                exception.Message;
        }
        finally
        {
            if (ReferenceEquals(
                    _passwordChangeCancellation,
                    cancellationSource))
            {
                _passwordChangeCancellation = null;
            }

            cancellationSource.Dispose();
            submittedPassword = string.Empty;
            IsChangingPassword = false;
            IsBusy = false;
        }
    }

    private void ApplyPasswordChangeProgress(
        VaultPasswordChangeProgress progress)
    {
        PasswordChangeProgressPercentage =
            progress.Percentage;

        PasswordChangeProgressPercentageText =
            $"{progress.Percentage:0}%";

        PasswordChangeProgressStatusText = progress.Stage switch
        {
            VaultPasswordChangeStage.GeneratingRootKey =>
                "Generating a fresh root key...",
            VaultPasswordChangeStage.PreparingVault =>
                "Protecting the fresh key with the new password...",
            VaultPasswordChangeStage.ReencryptingContent =>
                $"Re-encrypting {progress.ProcessedEntries}/" +
                $"{progress.TotalEntries} entries · " +
                $"{progress.ProcessedBlobs}/" +
                $"{progress.TotalBlobs} blobs",
            VaultPasswordChangeStage.Verifying =>
                "Verifying every rotated entry and blob...",
            VaultPasswordChangeStage.Publishing =>
                "Publishing the verified vault...",
            VaultPasswordChangeStage.Completed =>
                "Password change complete.",
            _ => "Changing vault password..."
        };
    }

    [RelayCommand]
    private void CloseMoreOptions()
    {
        if (!IsBusy)
        {
            ClosePasswordChange();
            IsMoreOptionsOpen = false;
        }
    }

    [RelayCommand]
    private async Task RequestLockVaultAsync()
    {
        if (IsBusy)
            return;

        IsMoreOptionsOpen = false;

        if (HasSaveWork)
        {
            string title = HasUnsavedChanges
                ? "DISCARD UNSAVED CHANGES?"
                : "LOCK WITH INCOMPLETE CLEANUP?";

            string description = HasUnsavedChanges
                ? "Locking now discards every folder, tag, and entry " +
                  "change made since the last successful save."
                : "The vault manifest was saved, but one or more " +
                  "obsolete encrypted entry files could not be removed. " +
                  "Locking prevents this session from retrying cleanup.";

            OpenConfirmationDialog(
                DialogAction.LockWithoutSaving,
                title,
                description,
                HasUnsavedChanges
                    ? "DISCARD AND LOCK"
                    : "LOCK ANYWAY");

            return;
        }

        await LockVaultCoreAsync();
    }

    [RelayCommand]
    private void CancelDialog()
    {
        if (!IsBusy)
        {
            CloseDialog();
        }
    }

    [RelayCommand(CanExecute = nameof(CanConfirmDialog))]
    private async Task ConfirmDialogAsync()
    {
        ClearDialogError();

        try
        {
            switch (_dialogAction)
            {
                case DialogAction.CreateEntry:
                    CreateEntryFromDialog();
                    break;

                case DialogAction.CreateFolder:
                    CreateFolderFromDialog();
                    break;

                case DialogAction.CreateTag:
                    CreateTagFromDialog();
                    break;

                case DialogAction.RenameEntry:
                    RenameEntryFromDialog();
                    break;

                case DialogAction.RenameFolder:
                    RenameFolderFromDialog();
                    break;

                case DialogAction.RenameTag:
                    RenameTagFromDialog();
                    break;

                case DialogAction.DeleteFolder:
                    DeleteSelectedFolder();
                    break;

                case DialogAction.DeleteTag:
                    DeleteSelectedTag();
                    break;

                case DialogAction.DeleteEntry:
                    DeleteSelectedEntry();
                    break;

                case DialogAction.DeleteEntries:
                    DeleteSelectedEntries();
                    break;

                case DialogAction.LockWithoutSaving:
                    CloseDialog();
                    await LockVaultCoreAsync();
                    return;

                default:
                    throw new InvalidOperationException(
                        "No vault dialog action is active.");
            }

            CloseDialog();
        }
        catch (Exception exception)
            when (IsExpectedOperationFailure(
                exception))
        {
            DialogErrorMessage =
                exception.Message;
        }
    }

    private void CreateFolderFromDialog()
    {
        Guid? parentFolderId =
            _selectedFolder?.IsFolder == true
                ? _selectedFolder.FolderId
                : null;

        FolderDescriptor folder =
            _session.CreateFolder(
                DialogInput.Trim(),
                parentFolderId);

        if (parentFolderId is Guid parentId)
        {
            _expandedFolderIds.Add(parentId);
        }

        RefreshBrowser(
            selectedFolderKind:
                VaultFolderFilterKind.Folder,
            selectedFolderId:
                folder.FolderId);

        RecordUnsavedChange(
            $"FOLDER '{folder.Name}' CREATED");
    }

    private void CreateEntryFromDialog()
    {
        string entryName =
            DialogInput.Trim();

        Guid? folderId =
            _selectedFolder?.IsFolder == true
                ? _selectedFolder.FolderId
                : null;

        IEnumerable<Guid>? tagIds =
            _selectedTag?.TagId is Guid tagId
                ? [tagId]
                : null;

        Guid entryId =
            _session.CreateEntry(
                    entryName,
                    folderId,
                    tagIds)
                .EntryId;

        RefreshBrowser(
            selectedEntryId: entryId);

        RecordUnsavedChange(
            $"ENTRY '{entryName}' CREATED");
    }

    private void CreateTagFromDialog()
    {
        TagDescriptor tag =
            _session.CreateTag(
                DialogInput.Trim());

        RefreshBrowser(
            selectedTagId:
                tag.TagId);

        RecordUnsavedChange(
            $"TAG '{tag.Name}' CREATED");
    }

    private void RenameEntryFromDialog()
    {
        VaultEntryListItemViewModel entry =
            _selectedEntry ??
            throw new InvalidOperationException(
                "Select an entry before renaming it.");

        string newName = DialogInput.Trim();

        _session.RenameEntry(
            entry.EntryId,
            newName);

        RefreshBrowser(
            selectedEntryId: entry.EntryId);

        RecordUnsavedChange(
            $"ENTRY '{entry.Name}' RENAMED TO '{newName}'");
    }

    private void RenameFolderFromDialog()
    {
        VaultFolderListItemViewModel folder =
            _selectedFolder ??
            throw new InvalidOperationException(
                "Select a folder before renaming it.");

        if (folder.FolderId is not Guid folderId)
        {
            throw new InvalidOperationException(
                "The selected item is not a folder.");
        }

        string newName = DialogInput.Trim();

        _session.RenameFolder(
            folderId,
            newName);

        RefreshBrowser(
            selectedFolderKind:
                VaultFolderFilterKind.Folder,
            selectedFolderId: folderId);

        RecordUnsavedChange(
            $"FOLDER '{folder.Name}' RENAMED TO '{newName}'");
    }

    private void RenameTagFromDialog()
    {
        VaultTagListItemViewModel tag =
            _selectedTag ??
            throw new InvalidOperationException(
                "Select a tag before renaming it.");

        if (tag.TagId is not Guid tagId)
        {
            throw new InvalidOperationException(
                "The selected item is not a tag.");
        }

        string newName = DialogInput.Trim();

        _session.RenameTag(
            tagId,
            newName);

        RefreshBrowser(
            selectedTagId: tagId);

        RecordUnsavedChange(
            $"TAG '{tag.Name}' RENAMED TO '{newName}'");
    }

    private void DeleteSelectedFolder()
    {
        VaultFolderListItemViewModel folder =
            _selectedFolder ??
            throw new InvalidOperationException(
                "Select a folder before deleting it.");

        if (folder.FolderId is not Guid folderId)
        {
            throw new InvalidOperationException(
                "The selected item is not a folder.");
        }

        _session.DeleteFolder(folderId);

        _expandedFolderIds.Remove(folderId);

        RefreshBrowser(
            selectedFolderKind:
                folder.ParentFolderId.HasValue
                    ? VaultFolderFilterKind.Folder
                    : VaultFolderFilterKind.Root,
            selectedFolderId:
                folder.ParentFolderId);

        RecordUnsavedChange(
            $"FOLDER '{folder.Name}' DELETED");
    }

    private void DeleteSelectedTag()
    {
        VaultTagListItemViewModel tag =
            _selectedTag ??
            throw new InvalidOperationException(
                "Select a tag before deleting it.");

        if (tag.TagId is not Guid tagId)
        {
            throw new InvalidOperationException(
                "The selected item is not a tag.");
        }

        _session.DeleteTag(tagId);

        RefreshBrowser(
            selectedTagId: null);

        RecordUnsavedChange(
            $"TAG '{tag.Name}' DELETED");
    }

    private void DeleteSelectedEntry()
    {
        VaultEntryListItemViewModel entry =
            _selectedEntry ??
            throw new InvalidOperationException(
                "Select an entry before deleting it.");

        _session.MarkEntryForDeletion(
            entry.EntryId);

        RefreshBrowser(
            selectedEntryId: entry.EntryId);

        RecordUnsavedChange(
            $"ENTRY '{entry.Name}' MARKED FOR DELETION");
    }

    private void DeleteSelectedEntries()
    {
        Guid[] entryIds =
            [.. _copySelectedEntryIds];

        if (entryIds.Length == 0)
        {
            throw new InvalidOperationException(
                "Select at least one entry before deleting.");
        }

        foreach (Guid entryId in entryIds)
        {
            _session.MarkEntryForDeletion(entryId);
        }

        ExitCopySelection();

        RecordUnsavedChange(
            entryIds.Length == 1
                ? "1 ENTRY MARKED FOR DELETION"
                : $"{entryIds.Length} ENTRIES MARKED FOR DELETION");
    }

    private void UndoSelectedEntryDeletion()
    {
        VaultEntryListItemViewModel entry =
            _selectedEntry ??
            throw new InvalidOperationException(
                "Select an entry before undoing its deletion.");

        _session.UndoEntryDeletion(
            entry.EntryId);

        RefreshBrowser(
            selectedEntryId: entry.EntryId);

        RefreshSessionFlags();

        SaveStatusText = HasSaveWork
            ? $"UNSAVED · ENTRY '{entry.Name}' DELETION UNMARKED"
            : $"NO UNSAVED CHANGES · ENTRY '{entry.Name}' DELETION UNMARKED";

        ClearError();
    }

    private void OpenMoveDialog(
        MoveOperationKind operationKind,
        Guid itemId,
        string itemName,
        Guid? currentParentFolderId)
    {
        _moveOperationKind = operationKind;
        _moveItemId = itemId;
        _moveItemName = itemName;
        _moveCurrentParentFolderId =
            currentParentFolderId;

        MoveDialogTitle = operationKind ==
            MoveOperationKind.Folder
                ? "MOVE FOLDER"
                : "MOVE ENTRY";

        MoveDialogDescription = operationKind ==
            MoveOperationKind.Folder
                ? "Choose the folder which will contain this folder. " +
                  "Invalid destinations are shown but cannot be selected."
                : "Choose the folder which will contain this entry. " +
                  "The move remains unsaved until you press SAVE.";

        MoveItemName = itemName;

        FolderDescriptor[] folders =
            [.. _session.Folders];

        Dictionary<Guid, FolderDescriptor> foldersById =
            folders.ToDictionary(
                folder => folder.FolderId);

        MoveCurrentLocationText =
            BuildFolderPath(
                currentParentFolderId,
                foldersById);

        MoveDestinationText =
            "NO DESTINATION SELECTED";

        MoveDialogErrorMessage = null;
        _selectedMoveDestination = null;
        _expandedMoveDestinationIds.Clear();
        _isMoveRootExpanded = true;

        ExpandMoveDestinationPath(
            currentParentFolderId,
            foldersById);

        RebuildMoveDestinationItems();

        OnPropertyChanged(
            nameof(MoveDialogActionText));

        IsMoveDialogOpen = true;
    }

    private void CloseMoveDialog()
    {
        IsMoveDialogOpen = false;
        _moveOperationKind = MoveOperationKind.None;
        _moveItemId = Guid.Empty;
        _moveItemName = string.Empty;
        _moveCurrentParentFolderId = null;
        _selectedMoveDestination = null;
        _expandedMoveDestinationIds.Clear();
        _isMoveRootExpanded = true;
        MoveDestinationItems.Clear();
        MoveDialogTitle = string.Empty;
        MoveDialogDescription = string.Empty;
        MoveItemName = string.Empty;
        MoveCurrentLocationText = string.Empty;
        MoveDestinationText =
            "NO DESTINATION SELECTED";
        MoveDialogErrorMessage = null;
    }

    private void SelectMoveDestination(
        VaultMoveDestinationItemViewModel destination)
    {
        if (!IsMoveDialogOpen ||
            IsBusy ||
            !destination.IsSelectable)
        {
            return;
        }

        _selectedMoveDestination = destination;

        foreach (VaultMoveDestinationItemViewModel item in
                 MoveDestinationItems)
        {
            item.SetSelected(
                ReferenceEquals(
                    item,
                    destination));
        }

        MoveDestinationText =
            $"DESTINATION · {destination.PathText}";

        MoveDialogErrorMessage = null;
        ConfirmMoveDialogCommand.NotifyCanExecuteChanged();
    }

    private void ToggleMoveDestinationExpansion(
        VaultMoveDestinationItemViewModel destination)
    {
        if (!IsMoveDialogOpen ||
            IsBusy ||
            !destination.IsExpandable)
        {
            return;
        }

        if (destination.FolderId is not Guid folderId)
        {
            _isMoveRootExpanded =
                !_isMoveRootExpanded;
        }
        else if (!_expandedMoveDestinationIds.Add(
                     folderId))
        {
            _expandedMoveDestinationIds.Remove(
                folderId);
        }

        RebuildMoveDestinationItems();
    }

    private void RebuildMoveDestinationItems()
    {
        bool hadSelection =
            _selectedMoveDestination is not null;

        Guid? selectedFolderId =
            _selectedMoveDestination?.FolderId;

        FolderDescriptor[] folders =
            [.. _session.Folders];

        Dictionary<Guid, FolderDescriptor> foldersById =
            folders.ToDictionary(
                folder => folder.FolderId);

        MoveDestinationItems.Clear();

        string? rootDisabledReason =
            GetMoveDestinationDisabledReason(
                destinationFolderId: null,
                folders,
                foldersById);

        MoveDestinationItems.Add(
            new VaultMoveDestinationItemViewModel(
                folderId: null,
                "ROOT",
                "ROOT",
                depth: 0,
                isExpandable: folders.Any(folder =>
                    folder.ParentFolderId is null),
                isExpanded: _isMoveRootExpanded,
                isSelectable:
                    rootDisabledReason is null,
                rootDisabledReason,
                SelectMoveDestination,
                ToggleMoveDestinationExpansion));

        if (_isMoveRootExpanded)
        {
            AddMoveDestinationChildren(
                parentFolderId: null,
                depth: 1,
                folders,
                foldersById,
                visited: []);
        }

        _selectedMoveDestination = hadSelection
            ? MoveDestinationItems.FirstOrDefault(item =>
                item.FolderId == selectedFolderId &&
                item.IsSelectable)
            : null;

        foreach (VaultMoveDestinationItemViewModel item in
                 MoveDestinationItems)
        {
            item.SetSelected(
                ReferenceEquals(
                    item,
                    _selectedMoveDestination));
        }

        if (_selectedMoveDestination is null)
        {
            MoveDestinationText =
                "NO DESTINATION SELECTED";
        }

        ConfirmMoveDialogCommand.NotifyCanExecuteChanged();
    }

    private void AddMoveDestinationChildren(
        Guid? parentFolderId,
        int depth,
        IReadOnlyCollection<FolderDescriptor> folders,
        IReadOnlyDictionary<Guid, FolderDescriptor> foldersById,
        HashSet<Guid> visited)
    {
        FolderDescriptor[] children =
            folders
                .Where(folder =>
                    folder.ParentFolderId ==
                    parentFolderId)
                .OrderBy(
                    folder => folder.Name,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();

        foreach (FolderDescriptor folder in children)
        {
            if (!visited.Add(
                    folder.FolderId))
            {
                continue;
            }

            bool isExpandable =
                folders.Any(child =>
                    child.ParentFolderId ==
                    folder.FolderId);

            bool isExpanded =
                _expandedMoveDestinationIds.Contains(
                    folder.FolderId);

            string? disabledReason =
                GetMoveDestinationDisabledReason(
                    folder.FolderId,
                    folders,
                    foldersById);

            MoveDestinationItems.Add(
                new VaultMoveDestinationItemViewModel(
                    folder.FolderId,
                    folder.Name,
                    BuildFolderPath(
                        folder.FolderId,
                        foldersById),
                    depth,
                    isExpandable,
                    isExpanded,
                    isSelectable:
                        disabledReason is null,
                    disabledReason,
                    SelectMoveDestination,
                    ToggleMoveDestinationExpansion));

            if (isExpanded)
            {
                AddMoveDestinationChildren(
                    folder.FolderId,
                    depth + 1,
                    folders,
                    foldersById,
                    visited);
            }
        }
    }

    private string? GetMoveDestinationDisabledReason(
        Guid? destinationFolderId,
        IReadOnlyCollection<FolderDescriptor> folders,
        IReadOnlyDictionary<Guid, FolderDescriptor> foldersById)
    {
        if (destinationFolderId ==
            _moveCurrentParentFolderId)
        {
            return "This is already the current location.";
        }

        if (_moveOperationKind ==
            MoveOperationKind.Entry)
        {
            return null;
        }

        if (destinationFolderId ==
            _moveItemId)
        {
            return "A folder cannot contain itself.";
        }

        if (IsFolderOrDescendant(
                destinationFolderId,
                _moveItemId,
                foldersById))
        {
            return "A folder cannot move into one of its descendants.";
        }

        bool hasNameConflict =
            folders.Any(folder =>
                folder.FolderId != _moveItemId &&
                folder.ParentFolderId ==
                destinationFolderId &&
                string.Equals(
                    folder.Name,
                    _moveItemName,
                    StringComparison.OrdinalIgnoreCase));

        return hasNameConflict
            ? "That destination already contains a folder " +
              "with this name."
            : null;
    }

    private void MoveSelectedEntry(
        Guid? destinationFolderId)
    {
        bool navigateToDestination =
            _selectedFolder?.Kind is
                VaultFolderFilterKind.Root or
                VaultFolderFilterKind.Folder;

        Guid entryId = _moveItemId;
        string entryName = _moveItemName;

        _session.MoveEntry(
            entryId,
            destinationFolderId);

        ExpandMainFolderPath(
            destinationFolderId);

        CloseMoveDialog();

        if (navigateToDestination)
        {
            RefreshBrowser(
                selectedFolderKind:
                    destinationFolderId.HasValue
                        ? VaultFolderFilterKind.Folder
                        : VaultFolderFilterKind.Root,
                selectedFolderId:
                    destinationFolderId,
                selectedEntryId:
                    entryId);
        }
        else
        {
            RefreshBrowser(
                selectedEntryId:
                    entryId);
        }

        RecordUnsavedChange(
            $"ENTRY '{entryName}' MOVED");
    }

    private void MoveSelectedFolder(
        Guid? destinationFolderId)
    {
        Guid folderId = _moveItemId;
        string folderName = _moveItemName;

        _session.MoveFolder(
            folderId,
            destinationFolderId);

        ExpandMainFolderPath(
            destinationFolderId);

        CloseMoveDialog();

        RefreshBrowser(
            selectedFolderKind:
                VaultFolderFilterKind.Folder,
            selectedFolderId:
                folderId);

        RecordUnsavedChange(
            $"FOLDER '{folderName}' MOVED");
    }

    private void ExpandMainFolderPath(
        Guid? folderId)
    {
        _isRootExpanded = true;

        Dictionary<Guid, FolderDescriptor> foldersById =
            _session.Folders.ToDictionary(
                folder => folder.FolderId);

        HashSet<Guid> visited = [];
        Guid? currentId = folderId;

        while (currentId is Guid id &&
               visited.Add(id) &&
               foldersById.TryGetValue(
                   id,
                   out FolderDescriptor? folder))
        {
            _expandedFolderIds.Add(id);
            currentId = folder.ParentFolderId;
        }
    }

    private void ExpandMoveDestinationPath(
        Guid? folderId,
        IReadOnlyDictionary<Guid, FolderDescriptor> foldersById)
    {
        HashSet<Guid> visited = [];
        Guid? currentId = folderId;

        while (currentId is Guid id &&
               visited.Add(id) &&
               foldersById.TryGetValue(
                   id,
                   out FolderDescriptor? folder))
        {
            _expandedMoveDestinationIds.Add(id);
            currentId = folder.ParentFolderId;
        }
    }

    private static bool IsFolderOrDescendant(
        Guid? possibleDescendantId,
        Guid folderId,
        IReadOnlyDictionary<Guid, FolderDescriptor> foldersById)
    {
        HashSet<Guid> visited = [];
        Guid? currentId = possibleDescendantId;

        while (currentId is Guid id &&
               visited.Add(id))
        {
            if (id == folderId)
            {
                return true;
            }

            if (!foldersById.TryGetValue(
                    id,
                    out FolderDescriptor? folder))
            {
                return false;
            }

            currentId = folder.ParentFolderId;
        }

        return false;
    }

    private static string BuildFolderPath(
        Guid? folderId,
        IReadOnlyDictionary<Guid, FolderDescriptor> foldersById)
    {
        if (folderId is null)
        {
            return "ROOT";
        }

        Stack<string> names = [];
        HashSet<Guid> visited = [];
        Guid? currentId = folderId;

        while (currentId is Guid id &&
               visited.Add(id))
        {
            if (!foldersById.TryGetValue(
                    id,
                    out FolderDescriptor? folder))
            {
                throw new InvalidOperationException(
                    $"Folder '{id}' does not exist.");
            }

            names.Push(folder.Name);
            currentId = folder.ParentFolderId;
        }

        if (currentId.HasValue)
        {
            throw new InvalidOperationException(
                "The folder hierarchy contains a cycle.");
        }

        return "ROOT / " +
               string.Join(
                   " / ",
                   names);
    }

    private async Task LockVaultCoreAsync()
    {
        ClearError();
        IsBusy = true;

        try
        {
            // Release decoded image surfaces before the session key
            // and pending plaintext buffers are destroyed.
            CloseEntryEditorWithoutRefresh();
            await _lockVault();
        }
        catch (Exception exception)
            when (IsExpectedOperationFailure(
                exception))
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal void PrepareForSessionDisposal()
    {
        _passwordChangeCancellation?.Cancel();

        // Detaching the editor closes image viewer windows and releases
        // decoded image surfaces before the session key is destroyed.
        CloseEntryEditorWithoutRefresh();

        ClearPasswordChangeInputs();
        CloseTimelineDateDialog();
        CopyPassword = string.Empty;
        DialogInput = string.Empty;
    }

    private void SelectFolder(
        VaultFolderListItemViewModel folder)
    {
        if (IsBusy ||
            IsDialogOpen ||
            IsMoveDialogOpen ||
            IsCopyDialogOpen ||
            IsTimelineDateDialogOpen)
        {
            return;
        }

        CloseEntryEditorWithoutRefresh();

        _selectedFolder = folder;

        foreach (VaultFolderListItemViewModel item
                 in FolderItems.OfType<
                     VaultFolderListItemViewModel>())
        {
            item.SetSelected(
                ReferenceEquals(item, folder));
        }

        LoadSelectedSortPreference();
        ApplyEntryFilter();
        DeleteFolderCommand.NotifyCanExecuteChanged();
        OpenFolderMoveCommand.NotifyCanExecuteChanged();
    }

    private bool CanUseFolderContextAction(
        VaultFolderListItemViewModel folder,
        bool requireRegularFolder = false)
    {
        return CanMutateVault() &&
            FolderItems.Contains(folder) &&
            folder.Kind is not
                VaultFolderFilterKind.AllEntries &&
            (!requireRegularFolder ||
             folder.IsFolder);
    }

    private void NewEntryInFolder(
        VaultFolderListItemViewModel folder)
    {
        if (!CanUseFolderContextAction(folder))
        {
            return;
        }

        SelectFolder(folder);
        NewEntry();
    }

    private void NewFolderInFolder(
        VaultFolderListItemViewModel folder)
    {
        if (!CanUseFolderContextAction(folder))
        {
            return;
        }

        SelectFolder(folder);
        NewFolder();
    }

    private void RenameFolderFromContextMenu(
        VaultFolderListItemViewModel folder)
    {
        if (!CanUseFolderContextAction(
                folder,
                requireRegularFolder: true))
        {
            return;
        }

        SelectFolder(folder);

        OpenInputDialog(
            DialogAction.RenameFolder,
            "RENAME FOLDER",
            $"Rename '{folder.Name}'. The folder ID and its contents " +
            "will remain unchanged.",
            "RENAME FOLDER",
            folder.Name);
    }

    private void MoveFolderFromContextMenu(
        VaultFolderListItemViewModel folder)
    {
        if (!CanUseFolderContextAction(
                folder,
                requireRegularFolder: true))
        {
            return;
        }

        SelectFolder(folder);
        OpenFolderMove();
    }

    private void DeleteFolderFromContextMenu(
        VaultFolderListItemViewModel folder)
    {
        if (!CanUseFolderContextAction(
                folder,
                requireRegularFolder: true))
        {
            return;
        }

        SelectFolder(folder);
        DeleteFolder();
    }

    private void RenameEntryFromContextMenu(
        VaultFolderEntryListItemViewModel entry)
    {
        if (!CanMutateVault() ||
            !FolderItems.Contains(entry) ||
            entry.IsPendingDeletion)
        {
            return;
        }

        SelectFolderEntry(entry);

        VaultEntryListItemViewModel selectedEntry =
            _selectedEntry ??
            throw new InvalidOperationException(
                "Select an entry before renaming it.");

        OpenInputDialog(
            DialogAction.RenameEntry,
            "RENAME ENTRY",
            $"Rename '{selectedEntry.Name}'. The entry ID, fields, " +
            "tags, and revision will remain unchanged.",
            "RENAME ENTRY",
            selectedEntry.Name);
    }

    private void SetTimelineDateFromContextMenu(
        VaultFolderEntryListItemViewModel entry)
    {
        if (!CanMutateVault() ||
            !FolderItems.Contains(entry) ||
            entry.IsPendingDeletion)
        {
            return;
        }

        SelectFolderEntry(entry);

        if (CanOpenTimelineDate())
        {
            OpenTimelineDate();
        }
    }

    private void SetTimelineDateFromEntryList(
        VaultEntryListItemViewModel entry)
    {
        if (!CanMutateVault() ||
            !EntryItems.Contains(entry) ||
            entry.IsPendingDeletion)
        {
            return;
        }

        SelectEntry(entry);

        if (CanOpenTimelineDate())
        {
            OpenTimelineDate();
        }
    }

    private void ToggleFolderExpansion(
        VaultFolderListItemViewModel folder)
    {
        if (IsBusy ||
            IsDialogOpen ||
            IsMoveDialogOpen ||
            IsTimelineDateDialogOpen ||
            !folder.IsExpandable)
        {
            return;
        }

        if (folder.Kind == VaultFolderFilterKind.Root)
        {
            _isRootExpanded = !_isRootExpanded;

            if (!_isRootExpanded &&
                _selectedFolder?.Kind ==
                VaultFolderFilterKind.Folder)
            {
                RefreshBrowser(
                    selectedFolderKind:
                        VaultFolderFilterKind.Root,
                    selectedFolderId: null);
            }
            else
            {
                RefreshBrowser();
            }

            return;
        }

        if (folder.FolderId is not Guid folderId)
        {
            return;
        }

        if (_expandedFolderIds.Add(folderId))
        {
            RefreshBrowser();
            return;
        }

        _expandedFolderIds.Remove(folderId);

        if (IsSelectedFolderDescendantOf(folderId))
        {
            RefreshBrowser(
                selectedFolderKind:
                    VaultFolderFilterKind.Folder,
                selectedFolderId: folderId);
        }
        else
        {
            RefreshBrowser();
        }
    }

    private void ToggleCopyFolderSelection(
        VaultFolderListItemViewModel folder)
    {
        if (!CanChangeCopySelection() ||
            folder.FolderId is not Guid folderId)
        {
            return;
        }

        if (!_copySelectedFolderIds.Add(folderId))
        {
            _copySelectedFolderIds.Remove(folderId);
        }

        RefreshCopySelectionState();
    }

    private bool IsSelectedFolderDescendantOf(
        Guid possibleAncestorId)
    {
        if (_selectedFolder?.FolderId is not Guid selectedId ||
            selectedId == possibleAncestorId)
        {
            return false;
        }

        Dictionary<Guid, Guid?> parents =
            _session.Folders.ToDictionary(
                folder => folder.FolderId,
                folder => folder.ParentFolderId);

        HashSet<Guid> visited = [];
        Guid? currentId = selectedId;

        while (currentId is Guid id &&
               visited.Add(id) &&
               parents.TryGetValue(
                   id,
                   out Guid? parentId))
        {
            if (parentId == possibleAncestorId)
            {
                return true;
            }

            currentId = parentId;
        }

        return false;
    }

    private void SelectTag(
        VaultTagListItemViewModel tag)
    {
        if (IsBusy ||
            IsDialogOpen ||
            IsMoveDialogOpen ||
            IsTimelineDateDialogOpen)
        {
            return;
        }

        CloseEntryEditorWithoutRefresh();

        _selectedTag = tag;

        foreach (VaultTagListItemViewModel item
                 in TagItems)
        {
            item.SetSelected(
                ReferenceEquals(item, tag));
        }

        ApplyEntryFilter();
        DeleteTagCommand.NotifyCanExecuteChanged();
    }

    private void RenameTagFromContextMenu(
        VaultTagListItemViewModel tag)
    {
        if (!CanMutateVault() ||
            !TagItems.Contains(tag) ||
            !tag.IsTag)
        {
            return;
        }

        SelectTag(tag);

        OpenInputDialog(
            DialogAction.RenameTag,
            "RENAME TAG",
            $"Rename '{tag.Name}'. The tag ID and all entry " +
            "assignments will remain unchanged.",
            "RENAME TAG",
            tag.Name);
    }

    private void SelectEntry(
        VaultEntryListItemViewModel entry)
    {
        if (IsCopySelectionMode)
        {
            ToggleCopyEntrySelection(
                entry.EntryId,
                entry.IsPendingDeletion);

            return;
        }

        if (IsBusy ||
            IsDialogOpen ||
            IsMoveDialogOpen ||
            IsCopyDialogOpen ||
            IsTimelineDateDialogOpen)
        {
            return;
        }

        _selectedEntry = entry;

        foreach (VaultEntryListItemViewModel item
                 in EntryItems)
        {
            item.SetSelected(
                ReferenceEquals(item, entry));
        }

        SetSelectedSidebarEntry(
            entry.EntryId);

        OnPropertyChanged(
            nameof(DeleteEntryActionText));

        DeleteEntryCommand.NotifyCanExecuteChanged();
        OpenEntryCommand.NotifyCanExecuteChanged();
        OpenEntryMoveCommand.NotifyCanExecuteChanged();
        OpenTimelineDateCommand.NotifyCanExecuteChanged();
    }

    private void SelectFolderEntry(
        VaultFolderEntryListItemViewModel entry)
    {
        if (IsCopySelectionMode)
        {
            ToggleCopyEntrySelection(
                entry.EntryId,
                entry.IsPendingDeletion);

            return;
        }

        if (IsBusy ||
            IsDialogOpen ||
            IsMoveDialogOpen ||
            IsCopyDialogOpen ||
            IsTimelineDateDialogOpen)
        {
            return;
        }

        CloseEntryEditorWithoutRefresh();

        // A sidebar entry is direct navigation. Clear filters
        // which could otherwise hide the selected entry.
        _selectedTag = null;

        if (!string.IsNullOrEmpty(
                SearchText))
        {
            SearchText = string.Empty;
        }

        RefreshBrowser(
            selectedFolderKind:
                entry.FolderId.HasValue
                    ? VaultFolderFilterKind.Folder
                    : VaultFolderFilterKind.Root,
            selectedFolderId:
                entry.FolderId,
            selectedEntryId:
                entry.EntryId);
    }

    private void ToggleCopyEntrySelection(
        Guid entryId,
        bool isPendingDeletion)
    {
        if (!CanChangeCopySelection() ||
            isPendingDeletion)
        {
            return;
        }

        if (!_copySelectedEntryIds.Add(entryId))
        {
            _copySelectedEntryIds.Remove(entryId);
        }

        RefreshCopySelectionState();
    }

    private void RefreshCopySelectionState()
    {
        foreach (VaultFolderListItemViewModel folder in
                 FolderItems.OfType<
                     VaultFolderListItemViewModel>())
        {
            folder.SetCopySelected(
                folder.FolderId is Guid folderId &&
                _copySelectedFolderIds.Contains(folderId));
        }

        foreach (VaultFolderEntryListItemViewModel entry in
                 FolderItems.OfType<
                     VaultFolderEntryListItemViewModel>())
        {
            entry.SetCopySelected(
                _copySelectedEntryIds.Contains(entry.EntryId));
        }

        foreach (VaultEntryListItemViewModel entry in EntryItems)
        {
            entry.SetCopySelected(
                _copySelectedEntryIds.Contains(entry.EntryId));
        }

        CopySelectedEntryCount =
            GetEffectiveCopyEntryIds().Count;

        OnPropertyChanged(nameof(CopySelectionSummaryText));
        OnPropertyChanged(nameof(CopyActionText));
        OnPropertyChanged(nameof(DeleteEntryActionText));

        DeleteEntryCommand.NotifyCanExecuteChanged();
        OpenCopyDialogCommand.NotifyCanExecuteChanged();
        ConfirmCopyCommand.NotifyCanExecuteChanged();
    }

    private HashSet<Guid> GetEffectiveCopyEntryIds()
    {
        HashSet<Guid> effective =
            new(_copySelectedEntryIds);

        if (_copySelectedFolderIds.Count == 0)
        {
            return effective;
        }

        FolderDescriptor[] folders =
            [.. _session.Folders];

        HashSet<Guid> includedFolderIds =
            new(_copySelectedFolderIds);

        bool added;

        do
        {
            added = false;

            foreach (FolderDescriptor folder in folders)
            {
                if (folder.ParentFolderId is Guid parentId &&
                    includedFolderIds.Contains(parentId))
                {
                    added |= includedFolderIds.Add(
                        folder.FolderId);
                }
            }
        }
        while (added);

        HashSet<Guid> pendingDeletionIds =
            _session.EntriesPendingDeletion.ToHashSet();

        effective.UnionWith(
            _session.Entries
                .Where(entry =>
                    entry.FolderId is Guid folderId &&
                    includedFolderIds.Contains(folderId) &&
                    !pendingDeletionIds.Contains(entry.EntryId))
                .Select(entry => entry.EntryId));

        return effective;
    }

    private void ExitCopySelection()
    {
        _copySelectedEntryIds.Clear();
        _copySelectedFolderIds.Clear();
        CopySelectedEntryCount = 0;
        IsCopySelectionMode = false;

        RefreshBrowser();
        RefreshCopySelectionState();
    }

    private void SelectCopyTarget(
        VaultCopyTargetItemViewModel target)
    {
        if (!IsCopyDialogOpen ||
            IsCopying)
        {
            return;
        }

        _selectedCopyTarget = target;

        foreach (VaultCopyTargetItemViewModel item in
                 CopyTargetVaults)
        {
            item.SetSelected(
                ReferenceEquals(item, target));
        }

        ClearCopyDialogError();
        OnPropertyChanged(nameof(SelectedCopyTargetText));
        ConfirmCopyCommand.NotifyCanExecuteChanged();
    }

    private void CloseCopyDialog()
    {
        CopyPassword = string.Empty;
        CopyPasswordCaretIndex = 0;
        IsCopyPasswordVisible = false;
        ClearCopyDialogError();

        _selectedCopyTarget = null;
        CopyTargetVaults.Clear();
        IsCopyDialogOpen = false;

        OnPropertyChanged(nameof(HasCopyTargets));
        OnPropertyChanged(nameof(HasNoCopyTargets));
        OnPropertyChanged(nameof(SelectedCopyTargetText));
    }

    private void RefreshBrowser(
        VaultFolderFilterKind? selectedFolderKind = null,
        Guid? selectedFolderId = null,
        Guid? selectedTagId = null,
        Guid? selectedEntryId = null)
    {
        VaultFolderFilterKind folderKind =
            selectedFolderKind ??
            _selectedFolder?.Kind ??
            VaultFolderFilterKind.AllEntries;

        Guid? folderId =
            selectedFolderKind.HasValue
                ? selectedFolderId
                : _selectedFolder?.FolderId;

        Guid? tagId =
            selectedTagId ??
            _selectedTag?.TagId;

        Guid? entryId =
            selectedEntryId ??
            _selectedEntry?.EntryId;

        FolderDescriptor[] folders =
            [.. _session.Folders];

        TagDescriptor[] tags =
            [.. _session.Tags];

        EntryDescriptor[] entries =
            [.. _session.Entries];

        IReadOnlyDictionary<Guid, EntrySessionState>
            entrySessionStates =
                BuildEntrySessionStates(entries);

        RebuildFolderItems(
            folders,
            entries,
            entrySessionStates);

        RebuildTagItems(
            tags,
            entries);

        _selectedFolder =
            FindFolderSelection(
                folderKind,
                folderId);

        _selectedTag =
            FindTagSelection(tagId);

        foreach (VaultFolderListItemViewModel item
                 in FolderItems.OfType<
                     VaultFolderListItemViewModel>())
        {
            item.SetSelected(
                ReferenceEquals(
                    item,
                    _selectedFolder));
        }

        foreach (VaultTagListItemViewModel item
                 in TagItems)
        {
            item.SetSelected(
                ReferenceEquals(
                    item,
                    _selectedTag));
        }

        LoadSelectedSortPreference();

        ApplyEntryFilter(
            entries,
            folders,
            tags,
            entrySessionStates,
            entryId);

        RefreshSessionFlags();
        NotifyCommandStates();
    }

    private void RebuildFolderItems(
        IReadOnlyCollection<FolderDescriptor> folders,
        IReadOnlyCollection<EntryDescriptor> entries,
        IReadOnlyDictionary<Guid, EntrySessionState>
            entrySessionStates)
    {
        FolderItems.Clear();

        FolderItems.Add(
            new VaultFolderListItemViewModel(
                VaultFolderFilterKind.AllEntries,
                folderId: null,
                parentFolderId: null,
                "ALL ENTRIES",
                depth: 0,
                entries.Count,
                isExpandable: false,
                isExpanded: false,
                SelectFolder,
                ToggleFolderExpansion,
                IsCopySelectionMode,
                isCopySelected: false,
                ToggleCopyFolderSelection,
                NewEntryInFolder,
                NewFolderInFolder,
                MoveFolderFromContextMenu,
                DeleteFolderFromContextMenu,
                RenameFolderFromContextMenu));

        EntryDescriptor[] rootEntries =
            entries
                .Where(entry =>
                    entry.FolderId is null)
                .OrderBy(
                    entry => entry.Name,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();

        bool rootHasFolders =
            folders.Any(folder =>
                folder.ParentFolderId is null);

        FolderItems.Add(
            new VaultFolderListItemViewModel(
                VaultFolderFilterKind.Root,
                folderId: null,
                parentFolderId: null,
                "ROOT",
                depth: 0,
                rootEntries.Length,
                isExpandable:
                    rootHasFolders ||
                    rootEntries.Length > 0,
                isExpanded: _isRootExpanded,
                SelectFolder,
                ToggleFolderExpansion,
                IsCopySelectionMode,
                isCopySelected: false,
                ToggleCopyFolderSelection,
                NewEntryInFolder,
                NewFolderInFolder,
                MoveFolderFromContextMenu,
                DeleteFolderFromContextMenu,
                RenameFolderFromContextMenu));

        HashSet<Guid> visited = [];

        if (_isRootExpanded)
        {
            AddFolderChildren(
                parentFolderId: null,
                depth: 1,
                folders,
                entries,
                entrySessionStates,
                visited);

            foreach (EntryDescriptor entry in rootEntries)
            {
                FolderItems.Add(
                    new VaultFolderEntryListItemViewModel(
                        entry.EntryId,
                        folderId: null,
                        entry.Name,
                        depth: 1,
                        entrySessionStates[
                            entry.EntryId],
                        SelectFolderEntry,
                        IsCopySelectionMode,
                        _copySelectedEntryIds.Contains(
                            entry.EntryId),
                        RenameEntryFromContextMenu,
                        SetTimelineDateFromContextMenu));
            }
        }
    }

    private void AddFolderChildren(
        Guid? parentFolderId,
        int depth,
        IReadOnlyCollection<FolderDescriptor> folders,
        IReadOnlyCollection<EntryDescriptor> entries,
        IReadOnlyDictionary<Guid, EntrySessionState>
            entrySessionStates,
        HashSet<Guid> visited)
    {
        FolderDescriptor[] children =
            folders
                .Where(folder =>
                    folder.ParentFolderId ==
                    parentFolderId)
                .OrderBy(
                    folder => folder.Name,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();

        foreach (FolderDescriptor folder in children)
        {
            if (!visited.Add(
                    folder.FolderId))
            {
                continue;
            }

            EntryDescriptor[] containedEntries =
                entries
                    .Where(entry =>
                        entry.FolderId ==
                        folder.FolderId)
                    .OrderBy(
                        entry => entry.Name,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray();

            VaultFolderEntryListItemViewModel[]
                containedEntryItems =
                    containedEntries
                        .Select(entry =>
                            new VaultFolderEntryListItemViewModel(
                                entry.EntryId,
                                folder.FolderId,
                                entry.Name,
                                depth + 1,
                                entrySessionStates[
                                    entry.EntryId],
                                SelectFolderEntry,
                                IsCopySelectionMode,
                                _copySelectedEntryIds.Contains(
                                    entry.EntryId),
                                RenameEntryFromContextMenu,
                                SetTimelineDateFromContextMenu))
                        .ToArray();

            bool hasChildFolders =
                folders.Any(child =>
                    child.ParentFolderId ==
                    folder.FolderId);

            FolderItems.Add(
                new VaultFolderListItemViewModel(
                    VaultFolderFilterKind.Folder,
                    folder.FolderId,
                    folder.ParentFolderId,
                    folder.Name,
                    depth,
                    entries.Count(entry =>
                        entry.FolderId ==
                        folder.FolderId),
                    isExpandable:
                        hasChildFolders ||
                        containedEntryItems.Length > 0,
                    isExpanded:
                        _expandedFolderIds.Contains(
                            folder.FolderId),
                    SelectFolder,
                    ToggleFolderExpansion,
                    IsCopySelectionMode,
                    _copySelectedFolderIds.Contains(
                        folder.FolderId),
                    ToggleCopyFolderSelection,
                    NewEntryInFolder,
                    NewFolderInFolder,
                    MoveFolderFromContextMenu,
                    DeleteFolderFromContextMenu,
                    RenameFolderFromContextMenu));

            if (_expandedFolderIds.Contains(
                    folder.FolderId))
            {
                AddFolderChildren(
                    folder.FolderId,
                    depth + 1,
                    folders,
                    entries,
                    entrySessionStates,
                    visited);

                foreach (VaultFolderEntryListItemViewModel entry
                         in containedEntryItems)
                {
                    FolderItems.Add(entry);
                }
            }
        }
    }

    private void RebuildTagItems(
        IReadOnlyCollection<TagDescriptor> tags,
        IReadOnlyCollection<EntryDescriptor> entries)
    {
        TagItems.Clear();

        TagItems.Add(
            new VaultTagListItemViewModel(
                tagId: null,
                "ALL TAGS",
                entries.Count,
                SelectTag,
                RenameTagFromContextMenu));

        foreach (TagDescriptor tag in tags.OrderBy(
                     tag => tag.Name,
                     StringComparer.OrdinalIgnoreCase))
        {
            TagItems.Add(
                new VaultTagListItemViewModel(
                    tag.TagId,
                    tag.Name,
                    entries.Count(entry =>
                        entry.TagIds.Contains(
                            tag.TagId)),
                    SelectTag,
                    RenameTagFromContextMenu));
        }
    }

    private VaultFolderListItemViewModel
        FindFolderSelection(
            VaultFolderFilterKind kind,
            Guid? folderId)
    {
        VaultFolderListItemViewModel? match =
            FolderItems
                .OfType<VaultFolderListItemViewModel>()
                .FirstOrDefault(item =>
                    item.Kind == kind &&
                    item.FolderId == folderId);

        return match ??
               FolderItems
                   .OfType<VaultFolderListItemViewModel>()
                   .First(item =>
                       item.Kind ==
                       VaultFolderFilterKind.AllEntries);
    }

    private VaultTagListItemViewModel
        FindTagSelection(
            Guid? tagId)
    {
        return TagItems.FirstOrDefault(item =>
                   item.TagId == tagId) ??
               TagItems.First();
    }

    private void ApplyEntryFilter()
    {
        EntryDescriptor[] entries =
            [.. _session.Entries];

        ApplyEntryFilter(
            entries,
            _session.Folders,
            _session.Tags,
            BuildEntrySessionStates(entries),
            selectedEntryId: null);
    }

    private void ApplyEntryFilter(
        IReadOnlyCollection<EntryDescriptor> entries,
        IReadOnlyCollection<FolderDescriptor> folders,
        IReadOnlyCollection<TagDescriptor> tags,
        IReadOnlyDictionary<Guid, EntrySessionState>
            entrySessionStates,
        Guid? selectedEntryId)
    {
        IEnumerable<EntryDescriptor> filtered =
            entries;

        if (_selectedFolder?.Kind ==
            VaultFolderFilterKind.Folder)
        {
            Guid? selectedFolderId =
                _selectedFolder.FolderId;

            filtered = filtered.Where(entry =>
                entry.FolderId ==
                selectedFolderId);
        }
        else if (_selectedFolder?.Kind ==
                 VaultFolderFilterKind.Root)
        {
            filtered = filtered.Where(entry =>
                entry.FolderId is null);
        }

        if (_selectedTag?.TagId is Guid selectedTagId)
        {
            filtered = filtered.Where(entry =>
                entry.TagIds.Contains(
                    selectedTagId));
        }

        if (!string.IsNullOrWhiteSpace(
                SearchText))
        {
            string searchText =
                SearchText.Trim();

            filtered = filtered.Where(entry =>
                entry.Name.Contains(
                    searchText,
                    StringComparison.OrdinalIgnoreCase));
        }

        Dictionary<Guid, string> folderNames =
            folders.ToDictionary(
                folder => folder.FolderId,
                folder => folder.Name);

        Dictionary<Guid, string> tagNames =
            tags.ToDictionary(
                tag => tag.TagId,
                tag => tag.Name);

        EntryItems.Clear();

        foreach (EntryDescriptor entry in
                 SortEntries(filtered))
        {
            string locationText =
                entry.FolderId is Guid folderId &&
                folderNames.TryGetValue(
                    folderId,
                    out string? folderName)
                    ? $"FOLDER · {folderName}"
                    : "FOLDER · ROOT";

            string[] assignedTagNames =
                entry.TagIds
                    .Where(tagNames.ContainsKey)
                    .Select(tagId =>
                        tagNames[tagId])
                    .OrderBy(
                        name => name,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray();

            string tagSummary =
                assignedTagNames.Length == 0
                    ? "TAGS · NONE"
                    : "TAGS · " +
                      string.Join(
                          " · ",
                          assignedTagNames);

            EntryItems.Add(
                new VaultEntryListItemViewModel(
                    entry.EntryId,
                    entry.Name,
                    locationText,
                    tagSummary,
                    entry.Revision,
                    entry.CreatedUtc,
                    entry.ModifiedUtc,
                    entry.TimelineDateOverride,
                    entrySessionStates[
                        entry.EntryId],
                    SelectEntry,
                    IsCopySelectionMode,
                    _copySelectedEntryIds.Contains(
                        entry.EntryId),
                    SetTimelineDateFromEntryList));
        }

        _selectedEntry =
            selectedEntryId.HasValue
                ? EntryItems.FirstOrDefault(item =>
                    item.EntryId ==
                    selectedEntryId)
                : null;

        foreach (VaultEntryListItemViewModel item
                 in EntryItems)
        {
            item.SetSelected(
                ReferenceEquals(
                    item,
                    _selectedEntry));
        }

        SetSelectedSidebarEntry(
            _selectedEntry?.EntryId);

        CurrentFilterTitle =
            _selectedFolder?.Name ??
            "ROOT";

        CurrentFilterDescription =
            _selectedTag?.TagId.HasValue == true
                ? $"TAG FILTER · {_selectedTag.Name}"
                : "NO TAG FILTER";

        EntryCountText = EntryItems.Count == 1
            ? "1 ENTRY"
            : $"{EntryItems.Count} ENTRIES";

        HasEntries = EntryItems.Count > 0;

        OnPropertyChanged(
            nameof(DeleteEntryActionText));

        DeleteEntryCommand.NotifyCanExecuteChanged();
        OpenEntryCommand.NotifyCanExecuteChanged();
        OpenEntryMoveCommand.NotifyCanExecuteChanged();
        OpenTimelineDateCommand.NotifyCanExecuteChanged();
    }

    private IReadOnlyDictionary<Guid, EntrySessionState>
        BuildEntrySessionStates(
            IEnumerable<EntryDescriptor> entries)
    {
        return entries.ToDictionary(
            entry => entry.EntryId,
            entry => _session.GetEntrySessionState(
                entry.EntryId));
    }

    private void SetSelectedSidebarEntry(
        Guid? selectedEntryId)
    {
        foreach (VaultFolderEntryListItemViewModel entry in
                 FolderItems.OfType<
                     VaultFolderEntryListItemViewModel>())
        {
            entry.SetSelected(
                entry.EntryId ==
                selectedEntryId);
        }
    }

    private IEnumerable<EntryDescriptor> SortEntries(
        IEnumerable<EntryDescriptor> entries)
    {
        EntrySortMode sortKind =
            SelectedSortOption?.Kind ??
            EntrySortMode.ModifiedNewest;

        return sortKind switch
        {
            EntrySortMode.NameAscending =>
                entries
                    .OrderBy(
                        entry => entry.Name,
                        StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(
                        entry => entry.ModifiedUtc),

            EntrySortMode.NameDescending =>
                entries
                    .OrderByDescending(
                        entry => entry.Name,
                        StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(
                        entry => entry.ModifiedUtc),

            EntrySortMode.CreatedNewest =>
                entries
                    .OrderByDescending(
                        entry => entry.CreatedUtc)
                    .ThenBy(
                        entry => entry.Name,
                        StringComparer.OrdinalIgnoreCase),

            EntrySortMode.CreatedOldest =>
                entries
                    .OrderBy(
                        entry => entry.CreatedUtc)
                    .ThenBy(
                        entry => entry.Name,
                        StringComparer.OrdinalIgnoreCase),

            EntrySortMode.TimelineNewest =>
                entries
                    .OrderByDescending(
                        entry => entry.EffectiveTimelineDate)
                    .ThenByDescending(
                        entry => entry.CreatedUtc)
                    .ThenBy(
                        entry => entry.EntryId),

            EntrySortMode.TimelineOldest =>
                entries
                    .OrderBy(
                        entry => entry.EffectiveTimelineDate)
                    .ThenBy(
                        entry => entry.CreatedUtc)
                    .ThenBy(
                        entry => entry.EntryId),

            EntrySortMode.ModifiedOldest =>
                entries
                    .OrderBy(
                        entry => entry.ModifiedUtc)
                    .ThenBy(
                        entry => entry.Name,
                        StringComparer.OrdinalIgnoreCase),

            _ =>
                entries
                    .OrderByDescending(
                        entry => entry.ModifiedUtc)
                    .ThenBy(
                        entry => entry.Name,
                        StringComparer.OrdinalIgnoreCase)
        };
    }

    private EntrySortMode GetSelectedSortMode()
    {
        return _selectedFolder?.Kind switch
        {
            VaultFolderFilterKind.Root =>
                _session.RootSortMode,

            VaultFolderFilterKind.Folder
                when _selectedFolder?.FolderId is Guid folderId =>
                    _session.GetFolderSortMode(folderId),

            _ => _session.AllEntriesSortMode
        };
    }

    private void SetSelectedSortMode(
        EntrySortMode sortMode)
    {
        switch (_selectedFolder?.Kind)
        {
            case VaultFolderFilterKind.Root:
                _session.SetRootSortMode(sortMode);
                break;

            case VaultFolderFilterKind.Folder
                when _selectedFolder?.FolderId is Guid folderId:
                _session.SetFolderSortMode(folderId, sortMode);
                break;

            default:
                _session.SetAllEntriesSortMode(sortMode);
                break;
        }
    }

    private void LoadSelectedSortPreference()
    {
        VaultEntrySortOptionViewModel option =
            VaultEntrySortOptionViewModel.FromMode(
                GetSelectedSortMode());

        if (ReferenceEquals(SelectedSortOption, option))
        {
            return;
        }

        _isLoadingSortPreference = true;

        try
        {
            SelectedSortOption = option;
        }
        finally
        {
            _isLoadingSortPreference = false;
        }
    }

    private void RefreshSessionFlags()
    {
        HasUnsavedChanges =
            _session.HasUnsavedChanges;

        HasSaveWork =
            HasUnsavedChanges ||
            _session.HasPendingEntryFileDeletions ||
            _session.HasPendingBlobFileDeletions;

        ManifestGenerationText =
            $"GENERATION {_session.ManifestGeneration}";

        OpenCopyDialogCommand.NotifyCanExecuteChanged();
        ConfirmCopyCommand.NotifyCanExecuteChanged();
    }

    private void RecordUnsavedChange(
        string statusMessage)
    {
        RefreshSessionFlags();

        SaveStatusText = HasUnsavedChanges
            ? $"UNSAVED · {statusMessage}"
            : $"SAVED · {statusMessage}";

        ClearError();
    }

    private void SetEntryEditorValidationState(
        bool hasValidationError)
    {
        HasEntryEditorValidationError =
            hasValidationError;
    }

    private void CloseEntryEditor()
    {
        Guid? entryId =
            EntryEditor?.EntryId;

        CloseEntryEditorWithoutRefresh();

        RefreshBrowser(
            selectedEntryId:
                entryId);
    }

    private void CloseEntryEditorWithoutRefresh()
    {
        if (EntryEditor is null)
        {
            return;
        }

        EntryEditorViewModel editor = EntryEditor;
        EntryEditor = null;
        editor.Dispose();
        HasEntryEditorValidationError = false;
    }

    private void OpenInputDialog(
        DialogAction action,
        string title,
        string description,
        string primaryActionText,
        string initialInput = "")
    {
        OpenDialog(
            action,
            title,
            description,
            primaryActionText,
            showInput: true,
            isDestructive: false,
            initialInput: initialInput);
    }

    private void OpenConfirmationDialog(
        DialogAction action,
        string title,
        string description,
        string primaryActionText)
    {
        OpenDialog(
            action,
            title,
            description,
            primaryActionText,
            showInput: false,
            isDestructive: true,
            initialInput: string.Empty);
    }

    private void OpenDialog(
        DialogAction action,
        string title,
        string description,
        string primaryActionText,
        bool showInput,
        bool isDestructive,
        string initialInput)
    {
        _dialogAction = action;

        DialogTitle = title;
        DialogDescription = description;

        DialogPrimaryActionText =
            primaryActionText;

        IsDialogInputVisible = showInput;
        IsDialogDestructive = isDestructive;
        DialogInput = showInput
            ? initialInput
            : string.Empty;
        DialogErrorMessage = null;
        IsDialogOpen = true;
    }

    private void CloseDialog()
    {
        IsDialogOpen = false;
        _dialogAction = DialogAction.None;
        DialogInput = string.Empty;
        DialogErrorMessage = null;
        IsDialogInputVisible = false;
        IsDialogDestructive = false;
    }

    private void CloseTimelineDateDialog()
    {
        IsTimelineDateDialogOpen = false;
        TimelineDateSelection = null;
        TimelineDateDialogDescription = string.Empty;
        TimelineDateFallbackText = string.Empty;
        HasTimelineDateOverride = false;
        _timelineDateEntryId = Guid.Empty;
        _originalTimelineDateOverride = null;
        _timelineDateFallback = default;
    }

    private void ClosePasswordChange()
    {
        IsPasswordKdfSettingsOpen = false;
        IsPasswordChangeOpen = false;
        ClearPasswordChangeInputs();
        ClearPasswordChangeError();
    }

    private void LoadCurrentPasswordKdfParameters()
    {
        Argon2idParameters parameters =
            _session.PasswordKdfParameters;

        _selectedPasswordKdfMemorySizeKiB =
            parameters.MemorySizeKiB;

        _selectedPasswordKdfIterations =
            parameters.Iterations;

        _selectedPasswordKdfParallelism =
            parameters.DegreeOfParallelism;

        CopySelectedPasswordKdfParametersToDraft();

        OnPropertyChanged(
            nameof(PasswordKdfSummaryText));

        OnPropertyChanged(
            nameof(PasswordKdfProfileText));
    }

    private void CopySelectedPasswordKdfParametersToDraft()
    {
        PasswordKdfDraftMemorySizeMiB =
            _selectedPasswordKdfMemorySizeKiB /
            1024;

        PasswordKdfDraftIterations =
            _selectedPasswordKdfIterations;

        PasswordKdfDraftParallelism =
            _selectedPasswordKdfParallelism;
    }

    private Argon2idParameters
        CreateDraftPasswordKdfParameters()
    {
        return new Argon2idParameters
        {
            Version =
                Argon2idParameters.SupportedVersion,

            MemorySizeKiB = checked(
                ToWholeNumber(
                    PasswordKdfDraftMemorySizeMiB) *
                1024),

            Iterations =
                ToWholeNumber(
                    PasswordKdfDraftIterations),

            DegreeOfParallelism =
                ToWholeNumber(
                    PasswordKdfDraftParallelism)
        };
    }

    private Argon2idParameters
        CreateSelectedPasswordKdfParameters()
    {
        return new Argon2idParameters
        {
            Version =
                Argon2idParameters.SupportedVersion,

            MemorySizeKiB =
                _selectedPasswordKdfMemorySizeKiB,

            Iterations =
                _selectedPasswordKdfIterations,

            DegreeOfParallelism =
                _selectedPasswordKdfParallelism
        };
    }

    private bool UsesRecommendedPasswordKdfParameters()
    {
        Argon2idParameters recommended =
            Argon2idParameters.Recommended;

        return _selectedPasswordKdfMemorySizeKiB ==
                   recommended.MemorySizeKiB &&
               _selectedPasswordKdfIterations ==
                   recommended.Iterations &&
               _selectedPasswordKdfParallelism ==
                   recommended.DegreeOfParallelism;
    }

    private void ClearPasswordChangeInputs()
    {
        NewPassword = string.Empty;
        ConfirmNewPassword = string.Empty;

        NewPasswordCaretIndex = 0;
        ConfirmNewPasswordCaretIndex = 0;

        IsNewPasswordVisible = false;
        IsConfirmNewPasswordVisible = false;
    }

    private void ClearError()
    {
        if (ErrorMessage is not null)
        {
            ErrorMessage = null;
        }
    }

    private void ClearDialogError()
    {
        if (DialogErrorMessage is not null)
        {
            DialogErrorMessage = null;
        }
    }

    private void ClearPasswordChangeError()
    {
        if (PasswordChangeErrorMessage is not null)
        {
            PasswordChangeErrorMessage = null;
        }
    }

    private void ClearCopyDialogError()
    {
        if (CopyDialogErrorMessage is not null)
        {
            CopyDialogErrorMessage = null;
        }
    }

    private void NotifyCommandStates()
    {
        NewEntryCommand.NotifyCanExecuteChanged();
        NewFolderCommand.NotifyCanExecuteChanged();
        NewTagCommand.NotifyCanExecuteChanged();
        OpenFolderMoveCommand.NotifyCanExecuteChanged();
        OpenEntryCommand.NotifyCanExecuteChanged();
        OpenEntryMoveCommand.NotifyCanExecuteChanged();
        OpenTimelineDateCommand.NotifyCanExecuteChanged();
        ApplyTimelineDateCommand.NotifyCanExecuteChanged();
        ClearTimelineDateCommand.NotifyCanExecuteChanged();
        CancelTimelineDateDialogCommand.NotifyCanExecuteChanged();
        DeleteFolderCommand.NotifyCanExecuteChanged();
        DeleteTagCommand.NotifyCanExecuteChanged();
        DeleteEntryCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        MoreOptionsCommand.NotifyCanExecuteChanged();
        ConfirmDialogCommand.NotifyCanExecuteChanged();
        OpenPasswordChangeCommand.NotifyCanExecuteChanged();
        ConfirmPasswordChangeCommand.NotifyCanExecuteChanged();
        CancelPasswordChangeCommand.NotifyCanExecuteChanged();
        ToggleNewPasswordVisibilityCommand.NotifyCanExecuteChanged();
        ToggleConfirmNewPasswordVisibilityCommand.NotifyCanExecuteChanged();
        OpenPasswordKdfSettingsCommand.NotifyCanExecuteChanged();
        CancelPasswordKdfSettingsCommand.NotifyCanExecuteChanged();
        RestoreDefaultPasswordKdfSettingsCommand.NotifyCanExecuteChanged();
        ApplyPasswordKdfSettingsCommand.NotifyCanExecuteChanged();
        ConfirmMoveDialogCommand.NotifyCanExecuteChanged();
        EnterCopySelectionCommand.NotifyCanExecuteChanged();
        CancelCopySelectionCommand.NotifyCanExecuteChanged();
        SelectAllVisibleForCopyCommand.NotifyCanExecuteChanged();
        ClearCopySelectionCommand.NotifyCanExecuteChanged();
        OpenCopyDialogCommand.NotifyCanExecuteChanged();
        CancelCopyDialogCommand.NotifyCanExecuteChanged();
        ToggleCopyPasswordVisibilityCommand.NotifyCanExecuteChanged();
        ConfirmCopyCommand.NotifyCanExecuteChanged();
    }

    private static string FormatCharacterCount(
        int characterCount)
    {
        return characterCount == 1
            ? "1 CHARACTER ENTERED"
            : $"{characterCount} CHARACTERS ENTERED";
    }

    private static string FormatCount(
        int count,
        string singularUnit)
    {
        return count == 1
            ? $"1 {singularUnit}"
            : $"{count} {singularUnit}s";
    }

    private static int ToWholeNumber(
        double value)
    {
        return checked(
            (int)Math.Round(
                value,
                MidpointRounding.AwayFromZero));
    }

    private static bool PathsEqual(
        string first,
        string second)
    {
        string normalizedFirst =
            Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(first));

        string normalizedSecond =
            Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(second));

        return string.Equals(
            normalizedFirst,
            normalizedSecond,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    private static bool IsExpectedOperationFailure(
        Exception exception)
    {
        return exception is ArgumentException or
            InvalidOperationException or
            IOException or
            CryptographicException or
            UnauthorizedAccessException or
            KeyNotFoundException or
            NotSupportedException;
    }

    private enum MoveOperationKind
    {
        None,
        Entry,
        Folder
    }

    private enum DialogAction
    {
        None,
        CreateEntry,
        CreateFolder,
        CreateTag,
        RenameEntry,
        RenameFolder,
        RenameTag,
        DeleteFolder,
        DeleteTag,
        DeleteEntry,
        DeleteEntries,
        LockWithoutSaving
    }
}
