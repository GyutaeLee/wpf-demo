using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace WpfDemo
{
    public sealed class WorklistViewModel : INotifyPropertyChanged
    {
        public const string AllStatuses = WorkStatus.All;
        public const string Waiting = WorkStatus.Waiting;
        public const string InProgress = WorkStatus.InProgress;
        public const string Completed = WorkStatus.Completed;

        private readonly IWorkItemApi _api;
        private readonly List<WorkItem> _allItems = new List<WorkItem>();
        private string _searchText = "";
        private string _statusFilter = AllStatuses;
        private WorkItem _selectedItem;
        private string _editedStatus = Waiting;
        private string _editedNote = "";
        private bool _isLoading;
        private bool _isSaving;
        private bool _hasLoadError;
        private bool _hasSaveError;
        private string _notice = "";

        public WorklistViewModel(IWorkItemApi api)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            VisibleItems = new ObservableCollection<WorkItem>();
            StatusFilters = new[] { AllStatuses, Waiting, InProgress, Completed };
            EditableStatuses = new[] { Waiting, InProgress, Completed };
            LoadCommand = new AsyncRelayCommand(LoadAsync, () => !IsLoading && !IsSaving && !IsDirty);
            SaveCommand = new AsyncRelayCommand(SaveAsync, () => CanSave);
            CancelCommand = new RelayCommand(Cancel, () => IsDirty && !IsSaving);
        }

        public ObservableCollection<WorkItem> VisibleItems { get; private set; }
        public string[] StatusFilters { get; private set; }
        public string[] EditableStatuses { get; private set; }
        public AsyncRelayCommand LoadCommand { get; private set; }
        public AsyncRelayCommand SaveCommand { get; private set; }
        public RelayCommand CancelCommand { get; private set; }

        public string SearchText
        {
            get { return _searchText; }
            set
            {
                if (!CanNavigate || _searchText == value) return;
                _searchText = value ?? "";
                OnPropertyChanged();
                ApplyFilter();
            }
        }

        public string StatusFilter
        {
            get { return _statusFilter; }
            set
            {
                if (!CanNavigate || _statusFilter == value) return;
                _statusFilter = value ?? AllStatuses;
                OnPropertyChanged();
                ApplyFilter();
            }
        }

        public WorkItem SelectedItem
        {
            get { return _selectedItem; }
            set
            {
                if (!CanNavigate || ReferenceEquals(_selectedItem, value)) return;
                _selectedItem = value;
                _editedStatus = value == null ? Waiting : value.Status;
                _editedNote = value == null ? "" : value.Note ?? "";
                HasSaveError = false;
                Notice = "";
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(EditedStatus));
                OnPropertyChanged(nameof(EditedNote));
                UpdateEditingState();
            }
        }

        public string EditedStatus
        {
            get { return _editedStatus; }
            set
            {
                if (_editedStatus == value) return;
                _editedStatus = value;
                OnPropertyChanged();
                HasSaveError = false;
                Notice = "";
                UpdateEditingState();
            }
        }

        public string EditedNote
        {
            get { return _editedNote; }
            set
            {
                if (_editedNote == value) return;
                _editedNote = value ?? "";
                OnPropertyChanged();
                HasSaveError = false;
                Notice = "";
                UpdateEditingState();
            }
        }

        public bool IsLoading
        {
            get { return _isLoading; }
            private set { _isLoading = value; OnPropertyChanged(); UpdateEditingState(); }
        }

        public bool IsSaving
        {
            get { return _isSaving; }
            private set { _isSaving = value; OnPropertyChanged(); UpdateEditingState(); }
        }

        public bool HasLoadError
        {
            get { return _hasLoadError; }
            private set { _hasLoadError = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsEmpty)); }
        }

        public bool HasSaveError
        {
            get { return _hasSaveError; }
            private set { _hasSaveError = value; OnPropertyChanged(); }
        }

        public string Notice
        {
            get { return _notice; }
            private set { _notice = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasNotice)); }
        }

        public bool HasNotice { get { return !string.IsNullOrEmpty(Notice); } }
        public bool HasSelection { get { return SelectedItem != null; } }
        public bool IsDirty { get { return HasSelection && (EditedStatus != SelectedItem.Status || EditedNote != (SelectedItem.Note ?? "")); } }
        public bool CanNavigate { get { return !IsLoading && !IsSaving && !IsDirty; } }
        public bool CanEdit { get { return HasSelection && !IsLoading && !IsSaving; } }
        public bool IsEmpty { get { return !IsLoading && !HasLoadError && VisibleItems.Count == 0; } }
        public bool CanSave { get { return IsDirty && !IsLoading && !IsSaving && ValidationMessage == ""; } }

        public string ValidationMessage
        {
            get
            {
                if (EditedNote.Length > 200) return "메모는 200자 이내로 입력해 주세요.";
                if (EditedNote.Length > 0 && string.IsNullOrWhiteSpace(EditedNote)) return "메모에 공백만 입력할 수 없습니다.";
                if (!EditableStatuses.Contains(EditedStatus)) return "상태를 선택해 주세요.";
                return "";
            }
        }

        public async Task LoadAsync()
        {
            if (IsLoading || IsSaving || IsDirty) return;
            IsLoading = true;
            HasLoadError = false;
            Notice = "";
            try
            {
                var items = await _api.GetItemsAsync();
                _allItems.Clear();
                _allItems.AddRange(items);
                _selectedItem = null;
                OnPropertyChanged(nameof(SelectedItem));
                OnPropertyChanged(nameof(HasSelection));
                ApplyFilter();
            }
            catch (Exception)
            {
                _allItems.Clear();
                VisibleItems.Clear();
                _selectedItem = null;
                OnPropertyChanged(nameof(SelectedItem));
                OnPropertyChanged(nameof(HasSelection));
                HasLoadError = true;
            }
            finally
            {
                IsLoading = false;
                UpdateEditingState();
            }
        }

        public async Task SaveAsync()
        {
            if (!CanSave) return;
            var item = SelectedItem;
            var status = EditedStatus;
            var note = EditedNote.Trim();
            IsSaving = true;
            HasSaveError = false;
            try
            {
                var updated = await _api.UpdateItemAsync(item.Id, status, note);
                item.Status = updated.Status;
                item.Note = updated.Note ?? "";
                _editedStatus = item.Status;
                _editedNote = item.Note;
                OnPropertyChanged(nameof(EditedStatus));
                OnPropertyChanged(nameof(EditedNote));
            }
            catch (Exception)
            {
                HasSaveError = true;
            }
            finally
            {
                IsSaving = false;
                UpdateEditingState();
                if (!HasSaveError)
                {
                    ApplyFilter();
                    Notice = "저장되었습니다.";
                }
            }
        }

        public void Cancel()
        {
            if (!IsDirty || IsSaving) return;
            _editedStatus = SelectedItem.Status;
            _editedNote = SelectedItem.Note ?? "";
            OnPropertyChanged(nameof(EditedStatus));
            OnPropertyChanged(nameof(EditedNote));
            HasSaveError = false;
            UpdateEditingState();
        }

        private void ApplyFilter()
        {
            var matching = new List<WorkItem>();
            foreach (var item in _allItems)
            {
                if (StatusFilter != AllStatuses && item.Status != StatusFilter) continue;
                if (!string.IsNullOrWhiteSpace(SearchText) &&
                    item.Title.IndexOf(SearchText.Trim(), StringComparison.OrdinalIgnoreCase) < 0 &&
                    item.Category.IndexOf(SearchText.Trim(), StringComparison.OrdinalIgnoreCase) < 0 &&
                    item.Id.ToString().IndexOf(SearchText.Trim(), StringComparison.OrdinalIgnoreCase) < 0) continue;
                matching.Add(item);
            }

            for (var index = VisibleItems.Count - 1; index >= 0; index--)
                if (!matching.Contains(VisibleItems[index])) VisibleItems.RemoveAt(index);
            for (var index = 0; index < matching.Count; index++)
            {
                if (index < VisibleItems.Count && ReferenceEquals(VisibleItems[index], matching[index])) continue;
                var oldIndex = VisibleItems.IndexOf(matching[index]);
                if (oldIndex >= 0) VisibleItems.Move(oldIndex, index);
                else VisibleItems.Insert(index, matching[index]);
            }
            if (_selectedItem != null && !matching.Contains(_selectedItem)) SelectedItem = null;
            OnPropertyChanged(nameof(IsEmpty));
        }

        private void UpdateEditingState()
        {
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(CanNavigate));
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(CanSave));
            OnPropertyChanged(nameof(ValidationMessage));
            OnPropertyChanged(nameof(IsEmpty));
            LoadCommand.RaiseCanExecuteChanged();
            SaveCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }
    }
}
