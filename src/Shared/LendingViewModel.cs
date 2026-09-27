using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace WpfDemo
{
    public sealed class LendingViewModel : INotifyPropertyChanged
    {
        private readonly ILendingApi _api;
        private readonly IPendingOperationStore _pendingStore;
        private readonly string _apiAddress;
        private readonly string _preferredBorrowerId;
        private readonly List<EquipmentItem> _allItems = new List<EquipmentItem>();
        private string _datasetId = "";
        private string _searchText = "";
        private string _statusFilter = EquipmentStates.All;
        private EquipmentItem _selectedItem;
        private Borrower _selectedBorrower;
        private string _draftNote = "";
        private string _initialNote = "";
        private string _initialBorrowerId = "";
        private IReadOnlyList<LoanHistoryEntry> _history = new LoanHistoryEntry[0];
        private PendingOperation _pending;
        private bool _isLoading;
        private bool _isLoadingHistory;
        private bool _isSending;
        private bool _isOffline;
        private bool _hasLoadError;
        private bool _hasHistoryError;
        private bool _hasUnknownResult;
        private bool _hasRecoveryError;
        private bool _hasConfirmedError;
        private string _operationMessage = "";
        private string _loadMessage = "";
        private int _historyRequestId;

        public LendingViewModel(ILendingApi api, IPendingOperationStore pendingStore,
            string apiAddress, string preferredBorrowerId)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _pendingStore = pendingStore ?? throw new ArgumentNullException(nameof(pendingStore));
            _apiAddress = (apiAddress ?? throw new ArgumentNullException(nameof(apiAddress))).TrimEnd('/');
            _preferredBorrowerId = preferredBorrowerId;
            Equipment = new ObservableCollection<EquipmentItem>();
            Borrowers = new ObservableCollection<Borrower>();
            VisibleItems = new ObservableCollection<EquipmentItem>();
            StatusFilters = new[] { EquipmentStates.All, EquipmentStates.Available, EquipmentStates.OnLoan, EquipmentStates.Maintenance };
            RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => CanNavigate && !IsLoading);
            BorrowCommand = new AsyncRelayCommand(BorrowAsync, () => CanBorrow);
            ReturnCommand = new AsyncRelayCommand(ReturnAsync, () => CanReturn);
            RetryCommand = new AsyncRelayCommand(RetryPendingAsync, () => _pending != null && !IsSending);
            CancelCommand = new RelayCommand(CancelDraft, () => IsDirty && _pending == null && !IsSending);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public ObservableCollection<EquipmentItem> Equipment { get; private set; }
        public ObservableCollection<Borrower> Borrowers { get; private set; }
        public ObservableCollection<EquipmentItem> VisibleItems { get; private set; }
        public string[] StatusFilters { get; private set; }
        public AsyncRelayCommand RefreshCommand { get; private set; }
        public AsyncRelayCommand BorrowCommand { get; private set; }
        public AsyncRelayCommand ReturnCommand { get; private set; }
        public AsyncRelayCommand RetryCommand { get; private set; }
        public RelayCommand CancelCommand { get; private set; }

        public string DatasetId { get { return _datasetId; } private set { Set(ref _datasetId, value); } }
        public string SearchText
        {
            get { return _searchText; }
            set { if (!CanNavigate || _searchText == value) return; _searchText = value ?? ""; OnPropertyChanged(); ApplyFilter(); }
        }
        public string StatusFilter
        {
            get { return _statusFilter; }
            set { if (!CanNavigate || _statusFilter == value) return; _statusFilter = value ?? EquipmentStates.All; OnPropertyChanged(); ApplyFilter(); }
        }
        public EquipmentItem SelectedItem
        {
            get { return _selectedItem; }
            set
            {
                if (!CanNavigate || ReferenceEquals(_selectedItem, value)) return;
                _selectedItem = value;
                _draftNote = "";
                _initialNote = "";
                _selectedBorrower = Borrowers.FirstOrDefault(x => x.Id == _preferredBorrowerId) ?? Borrowers.FirstOrDefault();
                _initialBorrowerId = _selectedBorrower?.Id ?? "";
                _history = new LoanHistoryEntry[0];
                HasHistoryError = false;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(HasActiveLoan));
                OnPropertyChanged(nameof(History));
                OnPropertyChanged(nameof(HasHistory));
                OnPropertyChanged(nameof(HasNoHistory));
                OnPropertyChanged(nameof(ShowNoHistoryMessage));
                OnPropertyChanged(nameof(DraftNote));
                OnPropertyChanged(nameof(SelectedBorrower));
                UpdateState();
                if (value != null) _ = LoadHistoryAsync(value.Id);
            }
        }
        public Borrower SelectedBorrower
        {
            get { return _selectedBorrower; }
            set
            {
                if (!CanChooseBorrower || ReferenceEquals(_selectedBorrower, value)) return;
                _selectedBorrower = value;
                OnPropertyChanged();
                UpdateState();
            }
        }
        public string DraftNote
        {
            get { return _draftNote; }
            set
            {
                if (!CanEdit || _draftNote == value) return;
                _draftNote = value ?? "";
                OnPropertyChanged();
                OnPropertyChanged(nameof(ValidationMessage));
                UpdateState();
            }
        }
        public IReadOnlyList<LoanHistoryEntry> History { get { return _history; } private set { _history = value ?? new LoanHistoryEntry[0]; OnPropertyChanged(); OnPropertyChanged(nameof(HasHistory)); OnPropertyChanged(nameof(HasNoHistory)); } }
        public bool HasHistory { get { return History.Count > 0; } }
        public bool HasNoHistory { get { return History.Count == 0; } }
        public bool ShowNoHistoryMessage { get { return HasNoHistory && !HasHistoryError && !IsLoadingHistory; } }
        public bool HasActiveLoan { get { return SelectedItem?.ActiveLoan != null; } }
        public bool HasSelection { get { return SelectedItem != null; } }
        public bool IsDirty { get { return HasSelection && (DraftNote != _initialNote || (CanChooseBorrower && (SelectedBorrower?.Id ?? "") != _initialBorrowerId)); } }
        public bool IsLoading { get { return _isLoading; } private set { Set(ref _isLoading, value); } }
        public bool IsLoadingHistory { get { return _isLoadingHistory; } private set { Set(ref _isLoadingHistory, value); } }
        public bool IsSending { get { return _isSending; } private set { Set(ref _isSending, value); } }
        public bool IsOffline { get { return _isOffline; } private set { Set(ref _isOffline, value); } }
        public bool HasLoadError { get { return _hasLoadError; } private set { Set(ref _hasLoadError, value); } }
        public bool HasNoResultsLoadError { get { return HasLoadError && VisibleItems.Count == 0; } }
        public bool HasStaleResults { get { return HasLoadError && VisibleItems.Count > 0; } }
        public bool HasHistoryError { get { return _hasHistoryError; } private set { Set(ref _hasHistoryError, value); } }
        public bool HasUnknownResult { get { return _hasUnknownResult; } private set { Set(ref _hasUnknownResult, value); } }
        public bool HasRecoveryError { get { return _hasRecoveryError; } private set { Set(ref _hasRecoveryError, value); } }
        public bool HasConfirmedError { get { return _hasConfirmedError; } private set { Set(ref _hasConfirmedError, value); } }
        public bool HasPendingOperation { get { return _pending != null; } }
        public bool CanNavigate { get { return !IsLoading && !IsSending && _pending == null && !IsDirty && !HasRecoveryError; } }
        public bool CanEdit { get { return HasSelection && !IsLoading && !IsSending && _pending == null && !IsOffline && !HasRecoveryError; } }
        public bool CanChooseBorrower { get { return CanEdit && SelectedItem.Status == EquipmentStates.Available; } }
        public bool CanBorrow { get { return CanEdit && SelectedItem.Status == EquipmentStates.Available && SelectedBorrower != null && IsValidNote; } }
        public bool CanReturn { get { return CanEdit && SelectedItem.Status == EquipmentStates.OnLoan && SelectedItem.ActiveLoan != null && IsValidNote; } }
        public bool CanRetry { get { return _pending != null && !IsSending; } }
        public bool IsEmpty { get { return !IsLoading && !HasLoadError && VisibleItems.Count == 0; } }
        public string ValidationMessage
        {
            get
            {
                if (DraftNote.Length > 200) return "메모는 200자 이내로 입력해 주세요.";
                if (DraftNote.Length > 0 && string.IsNullOrWhiteSpace(DraftNote)) return "메모에 공백만 입력할 수 없습니다.";
                return "";
            }
        }
        public string OperationMessage { get { return _operationMessage; } private set { Set(ref _operationMessage, value); } }
        public string LoadMessage { get { return _loadMessage; } private set { Set(ref _loadMessage, value); } }

        private bool IsValidNote { get { return ValidationMessage.Length == 0; } }

        public async Task InitializeAsync()
        {
            try { _pending = _pendingStore.Current; }
            catch (Exception)
            {
                HasRecoveryError = true;
                OperationMessage = "저장된 요청을 읽지 못했습니다. 파일을 보존하고 복구 상태를 확인해 주세요.";
            }
            OnPropertyChanged(nameof(HasPendingOperation));
            UpdateState();
            if (_pending != null)
            {
                HasUnknownResult = true;
                OperationMessage = "이전 요청의 결과를 확인하는 중입니다.";
                await SendPendingAsync();
                if (_pending == null) return;
            }
            await RefreshAsync(preserveDraft: true);
        }

        public async Task RefreshAsync() => await RefreshAsync(preserveDraft: false);

        public Task BorrowAsync() => StartOperationAsync("Borrow");
        public Task ReturnAsync() => StartOperationAsync("Return");

        private async Task RefreshAsync(bool preserveDraft)
        {
            if (IsLoading || IsSending || (_pending != null && !preserveDraft)) return;
            var selectedId = SelectedItem?.Id;
            var note = DraftNote;
            var borrowerId = SelectedBorrower?.Id;
            IsLoading = true;
            HasLoadError = false;
            LoadMessage = "";
            UpdateState();
            try
            {
                var response = await _api.GetEquipmentAsync();
                if (response?.Items == null || string.IsNullOrWhiteSpace(response.DatasetId))
                    throw new InvalidOperationException("장비 목록 응답이 올바르지 않습니다.");
                DatasetId = response.DatasetId;
                Equipment.Clear();
                foreach (var item in response.Items) Equipment.Add(item);
                Borrowers.Clear();
                foreach (var borrower in response.Borrowers ?? new List<Borrower>()) Borrowers.Add(borrower);
                _allItems.Clear();
                _allItems.AddRange(Equipment);
                _selectedItem = _allItems.FirstOrDefault(x => x.Id == selectedId);
                OnPropertyChanged(nameof(SelectedItem));
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(HasActiveLoan));
                if (preserveDraft && _selectedItem != null)
                {
                    _draftNote = note;
                    _selectedBorrower = Borrowers.FirstOrDefault(x => x.Id == borrowerId) ?? Borrowers.FirstOrDefault();
                }
                else
                {
                    _draftNote = "";
                    _initialNote = "";
                    _selectedBorrower = Borrowers.FirstOrDefault(x => x.Id == _preferredBorrowerId) ?? Borrowers.FirstOrDefault();
                    _initialBorrowerId = _selectedBorrower?.Id ?? "";
                }
                OnPropertyChanged(nameof(DraftNote));
                OnPropertyChanged(nameof(SelectedBorrower));
                IsOffline = false;
                ApplyFilter();
                if (_selectedItem != null) await LoadHistoryAsync(_selectedItem.Id);
            }
            catch (Exception)
            {
                HasLoadError = true;
                IsOffline = true;
                LoadMessage = "연결할 수 없습니다. 이전 조회 결과를 확인하고 연결이 복구되면 다시 시도해 주세요.";
            }
            finally
            {
                IsLoading = false;
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(IsDirty));
                UpdateState();
            }
        }

        private async Task LoadHistoryAsync(int equipmentId)
        {
            var requestId = ++_historyRequestId;
            IsLoadingHistory = true;
            HasHistoryError = false;
            OnPropertyChanged(nameof(ShowNoHistoryMessage));
            try
            {
                var history = await _api.GetHistoryAsync(equipmentId);
                if (requestId == _historyRequestId && SelectedItem?.Id == equipmentId) History = history;
            }
            catch (Exception)
            {
                if (requestId == _historyRequestId && SelectedItem?.Id == equipmentId)
                {
                    HasHistoryError = true;
                    OnPropertyChanged(nameof(ShowNoHistoryMessage));
                    OperationMessage = "장비 정보는 불러왔지만 대여 이력을 불러오지 못했습니다.";
                }
            }
            finally
            {
                if (requestId == _historyRequestId)
                {
                    IsLoadingHistory = false;
                    OnPropertyChanged(nameof(ShowNoHistoryMessage));
                }
            }
        }

        private async Task StartOperationAsync(string kind)
        {
            if (IsLoading || IsSending || _pending != null || IsOffline || HasRecoveryError || SelectedItem == null || !IsValidNote) return;
            if (kind == "Borrow" && (SelectedItem.Status != EquipmentStates.Available || SelectedBorrower == null)) return;
            if (kind == "Return" && (SelectedItem.Status != EquipmentStates.OnLoan || SelectedItem.ActiveLoan == null)) return;
            PendingOperation operation;
            IsSending = true;
            HasConfirmedError = false;
            OperationMessage = "전송할 요청을 보관하는 중입니다.";
            try
            {
                operation = kind == "Borrow"
                    ? PendingOperationStore.CreateBorrow(_apiAddress, DatasetId, SelectedItem.Id,
                        SelectedItem.Version, SelectedBorrower.Id, DraftNote.Trim())
                    : PendingOperationStore.CreateReturn(_apiAddress, DatasetId, SelectedItem, DraftNote.Trim());
                await Task.Run(() => _pendingStore.Save(operation));
            }
            catch (Exception)
            {
                IsSending = false;
                HasConfirmedError = true;
                OperationMessage = "요청을 안전하게 보관하지 못해 서버에 보내지 않았습니다.";
                UpdateState();
                return;
            }
            _pending = operation;
            OnPropertyChanged(nameof(HasPendingOperation));
            HasConfirmedError = false;
            await SendPendingAsync();
        }

        public async Task RetryPendingAsync()
        {
            if (_pending == null || IsSending) return;
            await SendPendingAsync();
        }

        private async Task SendPendingAsync()
        {
            var operation = _pending;
            if (operation == null) return;
            IsSending = true;
            HasUnknownResult = false;
            OperationMessage = "요청 결과를 확인하는 중입니다.";
            UpdateState();
            LoanOperationResponse response = null;
            try
            {
                response = await _api.SendOperationAsync(operation);
                if (response == null || response.OperationId != operation.OperationId || !response.Success ||
                    response.Code != (operation.Kind == "Borrow" ? "Borrowed" : "Returned"))
                    throw new InvalidOperationException("서버 응답을 요청 결과로 확인할 수 없습니다.");
            }
            catch (ApiResponseException ex) when (ex.IsConfirmed)
            {
                await FinishDefiniteFailureAsync(operation, ex);
                return;
            }
            catch (Exception)
            {
                IsSending = false;
                IsOffline = true;
                HasUnknownResult = true;
                OperationMessage = "요청 결과를 확인하지 못했습니다. 같은 요청으로 다시 시도할 수 있습니다.";
                UpdateState();
                return;
            }

            try
            {
                await Task.Run(() => _pendingStore.Delete(operation.OperationId));
                _pending = null;
                OnPropertyChanged(nameof(HasPendingOperation));
            }
            catch (Exception)
            {
                IsSending = false;
                HasUnknownResult = true;
                OperationMessage = "서버 처리 결과는 받았지만 요청 기록을 정리하지 못했습니다. 같은 요청으로 다시 확인해 주세요.";
                UpdateState();
                return;
            }

            IsSending = false;
            HasUnknownResult = false;
            HasConfirmedError = false;
            _draftNote = "";
            _initialNote = "";
            OnPropertyChanged(nameof(DraftNote));
            OnPropertyChanged(nameof(IsDirty));
            OperationMessage = operation.Kind == "Borrow" ? "대여가 완료되었습니다." : "반납이 완료되었습니다.";
            await RefreshAsync(preserveDraft: false);
            if (HasLoadError)
                OperationMessage = (operation.Kind == "Borrow" ? "대여" : "반납") + " 완료를 확인했습니다. 최신 장비 상태를 불러오지 못했습니다.";
            UpdateState();
        }

        private async Task FinishDefiniteFailureAsync(PendingOperation operation, ApiResponseException exception)
        {
            try
            {
                await Task.Run(() => _pendingStore.Delete(operation.OperationId));
                _pending = null;
                OnPropertyChanged(nameof(HasPendingOperation));
            }
            catch (Exception)
            {
                IsSending = false;
                HasUnknownResult = true;
                OperationMessage = "요청은 거부되었지만 요청 기록을 정리하지 못했습니다. 같은 요청으로 다시 확인해 주세요.";
                UpdateState();
                return;
            }

            IsSending = false;
            HasUnknownResult = false;
            HasConfirmedError = true;
            IsOffline = false;
            OperationMessage = exception.Message;
            await RefreshAsync(preserveDraft: true);
            UpdateState();
        }

        private void CancelDraft()
        {
            if (!IsDirty || _pending != null || IsSending) return;
            _draftNote = _initialNote;
            _selectedBorrower = Borrowers.FirstOrDefault(x => x.Id == _initialBorrowerId);
            OnPropertyChanged(nameof(DraftNote));
            OnPropertyChanged(nameof(SelectedBorrower));
            OnPropertyChanged(nameof(IsDirty));
            UpdateState();
        }

        private void ApplyFilter()
        {
            var query = (SearchText ?? "").Trim();
            var matching = _allItems.Where(item =>
                (StatusFilter == EquipmentStates.All || item.Status == StatusFilter) &&
                (query.Length == 0 || item.Code.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 item.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 item.Category.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            VisibleItems.Clear();
            foreach (var item in matching) VisibleItems.Add(item);
            if (_selectedItem != null && !matching.Any(x => x.Id == _selectedItem.Id))
            {
                _selectedItem = null;
                _draftNote = "";
                _initialNote = "";
                _history = new LoanHistoryEntry[0];
                OnPropertyChanged(nameof(SelectedItem));
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(HasActiveLoan));
                OnPropertyChanged(nameof(DraftNote));
                OnPropertyChanged(nameof(History));
                OnPropertyChanged(nameof(HasHistory));
                OnPropertyChanged(nameof(HasNoHistory));
                OnPropertyChanged(nameof(ShowNoHistoryMessage));
            }
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasNoResultsLoadError));
            OnPropertyChanged(nameof(HasStaleResults));
            OnPropertyChanged(nameof(IsDirty));
            UpdateState();
        }

        private void UpdateState()
        {
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(HasActiveLoan));
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(CanNavigate));
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(CanChooseBorrower));
            OnPropertyChanged(nameof(CanBorrow));
            OnPropertyChanged(nameof(CanReturn));
            OnPropertyChanged(nameof(CanRetry));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasNoResultsLoadError));
            OnPropertyChanged(nameof(HasStaleResults));
            RefreshCommand.RaiseCanExecuteChanged();
            BorrowCommand.RaiseCanExecuteChanged();
            ReturnCommand.RaiseCanExecuteChanged();
            RetryCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
        }

        private bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            UpdateState();
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public sealed class ApiResponseException : Exception
    {
        public ApiResponseException(int statusCode, string code, string message, bool isConfirmed)
            : base(message)
        {
            StatusCode = statusCode;
            Code = code;
            IsConfirmed = isConfirmed;
        }

        public int StatusCode { get; private set; }
        public string Code { get; private set; }
        public bool IsConfirmed { get; private set; }
    }
}
