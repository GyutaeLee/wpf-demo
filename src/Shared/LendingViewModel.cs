using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace WpfDemo
{
    public sealed class LendingViewModel : INotifyPropertyChanged
    {
        private readonly ILendingApi _api;
        private readonly PendingOperationWorkflow _operations;
        private readonly IReadCacheStore _readCache;
        private readonly IClientRequestLog _requestLog;
        private readonly Func<double> _dpiProvider;
        private readonly string _diagnosticsOutputDirectory;
        private readonly string _apiAddress;
        private readonly string _preferredBorrowerId;
        private string _datasetId = "";
        private string _searchText = "";
        private string _statusFilter = EquipmentStates.All;
        private EquipmentItem _selectedItem;
        private Borrower _selectedBorrower;
        private string _draftNote = "";
        private string _initialNote = "";
        private string _initialBorrowerId = "";
        private IReadOnlyList<LoanHistoryEntry> _history = new LoanHistoryEntry[0];
        private bool _isLoading;
        private bool _isLoadingHistory;
        private bool _isSending;
        private bool _isExportingDiagnostics;
        private bool _isOffline;
        private bool _hasLoadError;
        private bool _hasHistoryError;
        private bool _hasUnknownResult;
        private bool _hasRecoveryError;
        private bool _hasConfirmedError;
        private bool _isShowingCachedData;
        private bool _isShowingCachedHistory;
        private bool _hasCachedHistorySnapshot;
        private string _operationMessage = "";
        private string _loadMessage = "";
        private string _cacheNotice = "";
        private string _historyCacheNotice = "";
        private string _diagnosticsMessage = "";
        private int _historyRequestId;
        private int _equipmentRequestId;
        private int _equipmentPage = 1;
        private int _equipmentTotalCount;
        private int _historyPage = 1;
        private int _historyTotalCount;
        private CancellationTokenSource _equipmentReadCancellation;
        private CancellationTokenSource _historyReadCancellation;
        private CancellationTokenSource _searchDebounceCancellation;

        public LendingViewModel(ILendingApi api, IPendingOperationStore pendingStore,
            string apiAddress, string preferredBorrowerId, IReadCacheStore readCache = null,
            IClientRequestLog requestLog = null, Func<double> dpiProvider = null, string diagnosticsOutputDirectory = null)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _operations = new PendingOperationWorkflow(_api, pendingStore);
            _readCache = readCache;
            _requestLog = requestLog;
            _dpiProvider = dpiProvider;
            _diagnosticsOutputDirectory = string.IsNullOrWhiteSpace(diagnosticsOutputDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) : diagnosticsOutputDirectory;
            _apiAddress = (apiAddress ?? throw new ArgumentNullException(nameof(apiAddress))).TrimEnd('/');
            _preferredBorrowerId = preferredBorrowerId;
            Equipment = new ObservableCollection<EquipmentItem>();
            Borrowers = new ObservableCollection<Borrower>();
            VisibleItems = new ObservableCollection<EquipmentItem>();
            StatusFilters = new[] { EquipmentStates.All, EquipmentStates.Available, EquipmentStates.OnLoan, EquipmentStates.Maintenance };
            RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => CanNavigate && !IsLoading);
            PreviousPageCommand = new AsyncRelayCommand(() => ChangeEquipmentPageAsync(EquipmentPage - 1), () => CanPreviousEquipmentPage);
            NextPageCommand = new AsyncRelayCommand(() => ChangeEquipmentPageAsync(EquipmentPage + 1), () => CanNextEquipmentPage);
            PreviousHistoryPageCommand = new AsyncRelayCommand(() => LoadHistoryAsync(SelectedItem?.Id ?? 0, HistoryPage - 1), () => CanPreviousHistoryPage);
            NextHistoryPageCommand = new AsyncRelayCommand(() => LoadHistoryAsync(SelectedItem?.Id ?? 0, HistoryPage + 1), () => CanNextHistoryPage);
            BorrowCommand = new AsyncRelayCommand(BorrowAsync, () => CanBorrow);
            ReturnCommand = new AsyncRelayCommand(ReturnAsync, () => CanReturn);
            RetryCommand = new AsyncRelayCommand(RetryPendingAsync, () => _operations.Pending != null && !IsSending);
            CancelCommand = new RelayCommand(CancelDraft, () => IsDirty && _operations.Pending == null && !IsSending);
            ExportDiagnosticsCommand = new AsyncRelayCommand(ExportDiagnosticsAsync, () => _requestLog != null && !IsExportingDiagnostics);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public ObservableCollection<EquipmentItem> Equipment { get; private set; }
        public ObservableCollection<Borrower> Borrowers { get; private set; }
        public ObservableCollection<EquipmentItem> VisibleItems { get; private set; }
        public string[] StatusFilters { get; private set; }
        public AsyncRelayCommand RefreshCommand { get; private set; }
        public AsyncRelayCommand PreviousPageCommand { get; private set; }
        public AsyncRelayCommand NextPageCommand { get; private set; }
        public AsyncRelayCommand PreviousHistoryPageCommand { get; private set; }
        public AsyncRelayCommand NextHistoryPageCommand { get; private set; }
        public AsyncRelayCommand BorrowCommand { get; private set; }
        public AsyncRelayCommand ReturnCommand { get; private set; }
        public AsyncRelayCommand RetryCommand { get; private set; }
        public RelayCommand CancelCommand { get; private set; }
        public AsyncRelayCommand ExportDiagnosticsCommand { get; private set; }

        public string DatasetId { get { return _datasetId; } private set { Set(ref _datasetId, value); } }
        public string SearchText
        {
            get { return _searchText; }
            set
            {
                if (!CanChangeQuery || _searchText == value) return;
                _searchText = value ?? "";
                _equipmentPage = 1;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EquipmentPage));
                ScheduleSearchRefresh();
            }
        }
        public string StatusFilter
        {
            get { return _statusFilter; }
            set
            {
                if (!CanChangeQuery || _statusFilter == value) return;
                _statusFilter = value ?? EquipmentStates.All;
                _equipmentPage = 1;
                _searchDebounceCancellation?.Cancel();
                OnPropertyChanged();
                OnPropertyChanged(nameof(EquipmentPage));
                _ = RefreshEquipmentPageAsync(preserveDraft: false, page: 1);
            }
        }
        public EquipmentItem SelectedItem
        {
            get { return _selectedItem; }
            set
            {
                if (!CanNavigate || ReferenceEquals(_selectedItem, value)) return;
                _selectedItem = value;
                _historyReadCancellation?.Cancel();
                _historyRequestId++;
                IsLoadingHistory = false;
                _draftNote = "";
                _initialNote = "";
                _selectedBorrower = Borrowers.FirstOrDefault(x => x.Id == _preferredBorrowerId) ?? Borrowers.FirstOrDefault();
                _initialBorrowerId = _selectedBorrower?.Id ?? "";
                _history = new LoanHistoryEntry[0];
                _historyPage = 1;
                _historyTotalCount = 0;
                IsShowingCachedHistory = false;
                HasCachedHistorySnapshot = false;
                HistoryCacheNotice = "";
                OnPropertyChanged(nameof(HistoryPage));
                OnPropertyChanged(nameof(HistoryTotalCount));
                OnPropertyChanged(nameof(HistoryPageLabel));
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
                if (value != null) _ = LoadHistoryAsync(value.Id, 1);
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
        public bool ShowNoHistoryMessage { get { return HasNoHistory && !HasHistoryError && !IsLoadingHistory && (!IsShowingCachedHistory || HasCachedHistorySnapshot); } }
        public bool HasActiveLoan { get { return SelectedItem?.ActiveLoan != null; } }
        public bool HasSelection { get { return SelectedItem != null; } }
        public bool IsDirty { get { return HasSelection && (DraftNote != _initialNote || (CanChooseBorrower && (SelectedBorrower?.Id ?? "") != _initialBorrowerId)); } }
        public bool IsLoading { get { return _isLoading; } private set { Set(ref _isLoading, value); } }
        public bool IsLoadingHistory { get { return _isLoadingHistory; } private set { Set(ref _isLoadingHistory, value); } }
        public bool IsSending { get { return _isSending; } private set { Set(ref _isSending, value); } }
        public bool IsExportingDiagnostics { get { return _isExportingDiagnostics; } private set { Set(ref _isExportingDiagnostics, value); } }
        public bool CanExportDiagnostics { get { return _requestLog != null && !IsExportingDiagnostics; } }
        public bool IsOffline { get { return _isOffline; } private set { Set(ref _isOffline, value); } }
        public bool IsShowingCachedData { get { return _isShowingCachedData; } private set { Set(ref _isShowingCachedData, value); } }
        public bool IsShowingCachedHistory { get { return _isShowingCachedHistory; } private set { Set(ref _isShowingCachedHistory, value); } }
        public bool HasCachedHistorySnapshot { get { return _hasCachedHistorySnapshot; } private set { Set(ref _hasCachedHistorySnapshot, value); } }
        public bool HasLoadError { get { return _hasLoadError; } private set { Set(ref _hasLoadError, value); } }
        public bool HasNoResultsLoadError { get { return HasLoadError && VisibleItems.Count == 0 && !IsShowingCachedData; } }
        public bool HasStaleResults { get { return HasLoadError && VisibleItems.Count > 0 && !IsShowingCachedData; } }
        public bool HasHistoryError { get { return _hasHistoryError; } private set { Set(ref _hasHistoryError, value); } }
        public bool HasUnknownResult { get { return _hasUnknownResult; } private set { Set(ref _hasUnknownResult, value); } }
        public bool HasRecoveryError { get { return _hasRecoveryError; } private set { Set(ref _hasRecoveryError, value); } }
        public bool HasConfirmedError { get { return _hasConfirmedError; } private set { Set(ref _hasConfirmedError, value); } }
        public bool HasPendingOperation { get { return _operations.Pending != null; } }
        public bool CanNavigate { get { return !IsLoading && !IsSending && _operations.Pending == null && !IsDirty && !HasRecoveryError; } }
        public bool CanEdit { get { return HasSelection && !IsLoading && !IsSending && _operations.Pending == null && !IsOffline && !HasRecoveryError; } }
        public bool CanChooseBorrower { get { return CanEdit && SelectedItem.Status == EquipmentStates.Available; } }
        public bool CanBorrow { get { return CanEdit && SelectedItem.Status == EquipmentStates.Available && SelectedBorrower != null && IsValidNote; } }
        public bool CanReturn { get { return CanEdit && SelectedItem.Status == EquipmentStates.OnLoan && SelectedItem.ActiveLoan != null && IsValidNote; } }
        public bool CanRetry { get { return _operations.Pending != null && !IsSending; } }
        public bool IsEmpty { get { return !IsLoading && VisibleItems.Count == 0 && (!HasLoadError || IsShowingCachedData); } }
        public int EquipmentTotalCount { get { return _equipmentTotalCount; } }
        public int EquipmentPage { get { return _equipmentPage; } }
        public string EquipmentPageLabel { get { return _equipmentTotalCount == 0 ? "0개" : $"{_equipmentPage} / {EquipmentPageCount} 페이지 · {_equipmentTotalCount:N0}개"; } }
        public bool CanPreviousEquipmentPage { get { return CanChangeQuery && !IsLoading && EquipmentPage > 1; } }
        public bool CanNextEquipmentPage { get { return CanChangeQuery && !IsLoading && EquipmentPage < EquipmentPageCount; } }
        public int HistoryTotalCount { get { return _historyTotalCount; } }
        public int HistoryPage { get { return _historyPage; } }
        public string HistoryPageLabel { get { return _historyTotalCount == 0 ? "0개" : $"{_historyPage} / {HistoryPageCount} 페이지 · {_historyTotalCount:N0}개"; } }
        public bool CanPreviousHistoryPage { get { return HasSelection && !IsLoadingHistory && HistoryPage > 1; } }
        public bool CanNextHistoryPage { get { return HasSelection && !IsLoadingHistory && HistoryPage < HistoryPageCount; } }
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
        public string CacheNotice { get { return _cacheNotice; } private set { Set(ref _cacheNotice, value); } }
        public string HistoryCacheNotice { get { return _historyCacheNotice; } private set { Set(ref _historyCacheNotice, value); } }
        public string DiagnosticsMessage { get { return _diagnosticsMessage; } private set { Set(ref _diagnosticsMessage, value); } }

        private bool IsValidNote { get { return ValidationMessage.Length == 0; } }
        private const int PageSize = 50;
        private int EquipmentPageCount { get { return Math.Max(1, (int)Math.Ceiling((double)_equipmentTotalCount / PageSize)); } }
        private int HistoryPageCount { get { return Math.Max(1, (int)Math.Ceiling((double)_historyTotalCount / PageSize)); } }
        public bool CanChangeQuery { get { return !IsSending && _operations.Pending == null && !IsDirty && !HasRecoveryError; } }

        public async Task InitializeAsync()
        {
            try { _operations.Restore(); }
            catch (Exception)
            {
                HasRecoveryError = true;
                OperationMessage = "저장된 요청을 읽지 못했습니다. 파일을 보존하고 복구 상태를 확인해 주세요.";
            }
            OnPropertyChanged(nameof(HasPendingOperation));
            UpdateState();
            if (_operations.Pending != null)
            {
                HasUnknownResult = true;
                OperationMessage = "이전 요청의 결과를 확인하는 중입니다.";
                await SendPendingAsync();
                if (_operations.Pending == null) return;
            }
            await RefreshAsync(preserveDraft: true);
        }

        public async Task ExportDiagnosticsAsync()
        {
            if (IsExportingDiagnostics || _requestLog == null) return;
            IsExportingDiagnostics = true;
            DiagnosticsMessage = "진단 정보를 모으는 중입니다.";
            try
            {
                DiagnosticsResponse server = null;
                try { server = await _api.GetDiagnosticsAsync(CancellationToken.None); }
                catch (Exception) { }
                var current = _operations.Pending;
                var clientEvents = _requestLog.GetRecent();
                var dpi = _dpiProvider == null ? 96.0 : _dpiProvider();
                var outputPath = await Task.Run(() => DiagnosticBundleExporter.Export(
                    _diagnosticsOutputDirectory,
                    current, clientEvents, server, dpi));
                DiagnosticsMessage = server == null
                    ? "로컬 진단 파일을 만들었습니다. 서버 기록은 연결할 수 없어 포함하지 못했습니다."
                    : "진단 파일을 만들었습니다: " + System.IO.Path.GetFileName(outputPath);
            }
            catch (Exception)
            {
                DiagnosticsMessage = "진단 파일을 만들지 못했습니다. 문서 폴더의 쓰기 권한을 확인해 주세요.";
            }
            finally { IsExportingDiagnostics = false; }
        }

        public async Task RefreshAsync() => await RefreshEquipmentPageAsync(preserveDraft: false, page: EquipmentPage, allowCacheOnly: false);

        public Task BorrowAsync() => StartOperationAsync("Borrow");
        public Task ReturnAsync() => StartOperationAsync("Return");

        private async Task RefreshAsync(bool preserveDraft)
        {
            await RefreshEquipmentPageAsync(preserveDraft, EquipmentPage, allowCacheOnly: false);
        }

        private async Task RefreshEquipmentPageAsync(bool preserveDraft, int page, bool allowCacheOnly = true)
        {
            if (IsSending || (_operations.Pending != null && !preserveDraft)) return;
            _equipmentReadCancellation?.Cancel();
            var cancellation = new CancellationTokenSource();
            _equipmentReadCancellation = cancellation;
            var requestId = ++_equipmentRequestId;
            var selectedId = SelectedItem?.Id;
            var note = DraftNote;
            var borrowerId = SelectedBorrower?.Id;
            IsLoading = true;
            HasLoadError = false;
            LoadMessage = "";
            UpdateState();
            try
            {
                EquipmentListResponse response = null;
                CachedEquipmentSnapshot cached = null;
                var usingCache = allowCacheOnly && IsOffline && _readCache != null;
                if (usingCache)
                {
                    try { cached = await Task.Run(() => _readCache.GetEquipmentPage(_apiAddress, SearchText, StatusFilter, page, PageSize)); }
                    catch (Exception) { }
                    if (cached?.HasSnapshot != true)
                    {
                        if (requestId == _equipmentRequestId)
                        {
                            HasLoadError = true;
                            IsOffline = true;
                            IsShowingCachedData = false;
                            CacheNotice = "저장된 장비 조회 결과가 없습니다. 서버 연결을 확인해 주세요.";
                            LoadMessage = "연결할 수 없고, 이 화면에서 다시 보여줄 수 있는 저장 결과도 없습니다.";
                        }
                        return;
                    }
                    response = cached.Response;
                }
                else
                {
                    response = await _api.GetEquipmentAsync(SearchText, StatusFilter, page, PageSize, cancellation.Token);
                    if (requestId != _equipmentRequestId) return;
                    if (_readCache != null)
                    {
                        try { await Task.Run(() => _readCache.SaveEquipmentPage(_apiAddress, response)); }
                        catch (Exception) { }
                    }
                }
                if (requestId != _equipmentRequestId) return;
                if (response?.Items == null || string.IsNullOrWhiteSpace(response.DatasetId) || response.TotalCount < 0)
                    throw new InvalidOperationException("장비 목록 응답이 올바르지 않습니다.");
                var lastPage = Math.Max(1, (int)Math.Ceiling((double)response.TotalCount / PageSize));
                if (response.Items.Count == 0 && page > lastPage)
                {
                    _equipmentPage = lastPage;
                    OnPropertyChanged(nameof(EquipmentPage));
                    OnPropertyChanged(nameof(EquipmentPageLabel));
                    await RefreshEquipmentPageAsync(preserveDraft, lastPage, allowCacheOnly);
                    return;
                }

                if (usingCache)
                {
                    HasLoadError = true;
                    IsOffline = true;
                    IsShowingCachedData = true;
                    CacheNotice = ComposeEquipmentCacheNotice(cached);
                    LoadMessage = "API에 연결할 수 없어 저장된 장비 조회 결과를 표시합니다.";
                }
                else
                {
                    HasLoadError = false;
                    IsOffline = false;
                    IsShowingCachedData = false;
                    CacheNotice = "";
                    LoadMessage = "";
                }
                ApplyEquipmentResponse(response, preserveDraft, selectedId, note, borrowerId);
                if (_selectedItem != null) await LoadHistoryAsync(_selectedItem.Id, 1);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception)
            {
                if (requestId == _equipmentRequestId)
                {
                    HasLoadError = true;
                    IsOffline = true;
                    LoadMessage = "연결할 수 없습니다. 이전 조회 결과를 확인하고 연결이 복구되면 다시 시도해 주세요.";
                    CachedEquipmentSnapshot cached = null;
                    if (_readCache != null)
                    {
                        try { cached = await Task.Run(() => _readCache.GetEquipmentPage(_apiAddress, SearchText, StatusFilter, page, PageSize)); }
                        catch (Exception) { }
                    }
                    if (requestId == _equipmentRequestId && cached?.HasSnapshot == true)
                    {
                        HasLoadError = true;
                        IsOffline = true;
                        IsShowingCachedData = true;
                        CacheNotice = ComposeEquipmentCacheNotice(cached);
                        LoadMessage = "API에 연결할 수 없어 저장된 장비 조회 결과를 표시합니다.";
                        ApplyEquipmentResponse(cached.Response, preserveDraft, selectedId, note, borrowerId);
                        if (_selectedItem != null) await LoadHistoryAsync(_selectedItem.Id, 1);
                    }
                    else if (requestId == _equipmentRequestId)
                    {
                        IsShowingCachedData = false;
                        CacheNotice = _readCache == null ? "" : "저장된 장비 조회 결과가 없습니다.";
                    }
                }
            }
            finally
            {
                cancellation.Dispose();
                if (ReferenceEquals(_equipmentReadCancellation, cancellation)) _equipmentReadCancellation = null;
                if (requestId == _equipmentRequestId)
                {
                    IsLoading = false;
                    OnPropertyChanged(nameof(HasSelection));
                    OnPropertyChanged(nameof(IsDirty));
                    UpdateState();
                }
            }
        }

        private void ApplyEquipmentResponse(EquipmentListResponse response, bool preserveDraft,
            int? selectedId, string note, string borrowerId)
        {
            DatasetId = response.DatasetId;
            _equipmentTotalCount = response.TotalCount;
            _equipmentPage = response.Page;
            Equipment.Clear();
            VisibleItems.Clear();
            foreach (var item in response.Items)
            {
                Equipment.Add(item);
                VisibleItems.Add(item);
            }
            Borrowers.Clear();
            foreach (var borrower in response.Borrowers ?? new List<Borrower>()) Borrowers.Add(borrower);
            _selectedItem = Equipment.FirstOrDefault(x => x.Id == selectedId);
            OnPropertyChanged(nameof(SelectedItem));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(HasActiveLoan));
            if (_selectedItem == null && selectedId.HasValue) ClearSelectedItemState();
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
            OnPropertyChanged(nameof(EquipmentTotalCount));
            OnPropertyChanged(nameof(EquipmentPage));
            OnPropertyChanged(nameof(EquipmentPageLabel));
            OnPropertyChanged(nameof(IsEmpty));
            UpdateState();
        }

        private static string ComposeEquipmentCacheNotice(CachedEquipmentSnapshot cached)
        {
            return string.Format(CultureInfo.CurrentCulture,
                "API 연결 불가 · 저장된 {0:N0}개 장비 중 조건에 맞는 항목을 표시 · 마지막 갱신 {1}",
                cached.CachedItemCount, FormatCacheTimestamp(cached.LastUpdatedUtc));
        }

        private static string FormatCacheTimestamp(string value)
        {
            if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp))
                return "시각 정보 없음";
            return timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
        }

        private void ScheduleSearchRefresh()
        {
            _searchDebounceCancellation?.Cancel();
            _equipmentReadCancellation?.Cancel();
            _equipmentRequestId++;
            IsLoading = true;
            UpdateState();
            var cancellation = new CancellationTokenSource();
            _searchDebounceCancellation = cancellation;
            _ = RunDebouncedSearchAsync(cancellation);
        }

        private async Task RunDebouncedSearchAsync(CancellationTokenSource cancellation)
        {
            try
            {
                await Task.Delay(250, cancellation.Token);
                await RefreshEquipmentPageAsync(preserveDraft: false, page: 1);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            finally
            {
                if (ReferenceEquals(_searchDebounceCancellation, cancellation)) _searchDebounceCancellation = null;
                cancellation.Dispose();
            }
        }

        private Task ChangeEquipmentPageAsync(int page)
        {
            if (!CanChangeQuery || IsLoading || page < 1 || page > EquipmentPageCount) return Task.CompletedTask;
            _equipmentPage = page;
            OnPropertyChanged(nameof(EquipmentPage));
            OnPropertyChanged(nameof(EquipmentPageLabel));
            return RefreshEquipmentPageAsync(preserveDraft: false, page: page);
        }

        private async Task LoadHistoryAsync(int equipmentId, int page, bool allowCacheOnly = true)
        {
            if (equipmentId <= 0) return;
            var datasetId = DatasetId;
            _historyReadCancellation?.Cancel();
            var cancellation = new CancellationTokenSource();
            _historyReadCancellation = cancellation;
            var requestId = ++_historyRequestId;
            _historyPage = page;
            OnPropertyChanged(nameof(HistoryPage));
            OnPropertyChanged(nameof(HistoryPageLabel));
            IsLoadingHistory = true;
            HasHistoryError = false;
            OnPropertyChanged(nameof(ShowNoHistoryMessage));
            UpdateState();
            try
            {
                LoanHistoryListResponse response = null;
                CachedHistorySnapshot cached = null;
                var usingCache = allowCacheOnly && IsOffline && _readCache != null;
                if (usingCache)
                {
                    try { cached = await Task.Run(() => _readCache.GetHistoryPage(_apiAddress, datasetId, equipmentId, page, PageSize)); }
                    catch (Exception) { }
                    if (cached?.HasSnapshot != true)
                    {
                        if (requestId == _historyRequestId && SelectedItem?.Id == equipmentId)
                        {
                            HasHistoryError = true;
                            IsShowingCachedHistory = true;
                            HasCachedHistorySnapshot = false;
                            HistoryCacheNotice = "이 장비의 대여 이력은 로컬에 저장된 적이 없습니다.";
                            OnPropertyChanged(nameof(ShowNoHistoryMessage));
                        }
                        return;
                    }
                    response = cached.Response;
                }
                else
                {
                    response = await _api.GetHistoryAsync(equipmentId, page, PageSize, cancellation.Token);
                    if (requestId != _historyRequestId || SelectedItem?.Id != equipmentId) return;
                    if (_readCache != null)
                    {
                        try { await Task.Run(() => _readCache.SaveHistoryPage(_apiAddress, datasetId, equipmentId, response)); }
                        catch (Exception) { }
                    }
                }
                if (requestId != _historyRequestId || SelectedItem?.Id != equipmentId) return;
                if (response?.Items == null || response.TotalCount < 0)
                    throw new InvalidOperationException("대여 이력 응답이 올바르지 않습니다.");
                var lastPage = Math.Max(1, (int)Math.Ceiling((double)response.TotalCount / PageSize));
                if (response.Items.Count == 0 && page > lastPage)
                {
                    await LoadHistoryAsync(equipmentId, lastPage);
                    return;
                }
                _historyPage = response.Page;
                _historyTotalCount = response.TotalCount;
                History = response.Items;
                IsShowingCachedHistory = usingCache;
                HasCachedHistorySnapshot = usingCache;
                HistoryCacheNotice = usingCache ? ComposeHistoryCacheNotice(cached) : "";
                HasHistoryError = false;
                OnPropertyChanged(nameof(HistoryPage));
                OnPropertyChanged(nameof(HistoryTotalCount));
                OnPropertyChanged(nameof(HistoryPageLabel));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception)
            {
                if (requestId == _historyRequestId && SelectedItem?.Id == equipmentId)
                {
                    CachedHistorySnapshot cached = null;
                    if (_readCache != null)
                    {
                        try { cached = await Task.Run(() => _readCache.GetHistoryPage(_apiAddress, datasetId, equipmentId, page, PageSize)); }
                        catch (Exception) { }
                    }
                    if (requestId == _historyRequestId && SelectedItem?.Id == equipmentId && cached?.HasSnapshot == true)
                    {
                        _historyPage = cached.Response.Page;
                        _historyTotalCount = cached.Response.TotalCount;
                        History = cached.Response.Items;
                        IsShowingCachedHistory = true;
                        HasCachedHistorySnapshot = true;
                        HasHistoryError = false;
                        HistoryCacheNotice = ComposeHistoryCacheNotice(cached);
                    }
                    else if (requestId == _historyRequestId)
                    {
                        IsShowingCachedHistory = true;
                        HasCachedHistorySnapshot = false;
                        HasHistoryError = true;
                        HistoryCacheNotice = _readCache == null ? "" : "이 장비의 대여 이력은 로컬에 저장된 적이 없습니다.";
                        OperationMessage = "장비 정보는 불러왔지만 대여 이력을 불러오지 못했습니다.";
                    }
                    OnPropertyChanged(nameof(HistoryPage));
                    OnPropertyChanged(nameof(HistoryTotalCount));
                    OnPropertyChanged(nameof(HistoryPageLabel));
                    OnPropertyChanged(nameof(ShowNoHistoryMessage));
                }
            }
            finally
            {
                cancellation.Dispose();
                if (ReferenceEquals(_historyReadCancellation, cancellation)) _historyReadCancellation = null;
                if (requestId == _historyRequestId)
                {
                    IsLoadingHistory = false;
                    OnPropertyChanged(nameof(ShowNoHistoryMessage));
                    UpdateState();
                }
            }
        }

        private static string ComposeHistoryCacheNotice(CachedHistorySnapshot cached)
        {
            if (cached.CachedItemCount < cached.ServerTotalCountAtLastFetch)
                return string.Format(CultureInfo.CurrentCulture,
                    "API 연결 불가 · 저장된 이력 {0:N0}건만 표시 (마지막 조회 때 서버 전체 {1:N0}건) · 갱신 {2}",
                    cached.CachedItemCount, cached.ServerTotalCountAtLastFetch, FormatCacheTimestamp(cached.LastUpdatedUtc));
            return string.Format(CultureInfo.CurrentCulture,
                "API 연결 불가 · 저장된 이력 {0:N0}건 표시 · 갱신 {1}",
                cached.CachedItemCount, FormatCacheTimestamp(cached.LastUpdatedUtc));
        }

        private void ClearSelectedItemState()
        {
            _historyReadCancellation?.Cancel();
            _historyRequestId++;
            IsLoadingHistory = false;
            HasHistoryError = false;
            _history = new LoanHistoryEntry[0];
            _historyPage = 1;
            _historyTotalCount = 0;
            _isShowingCachedHistory = false;
            _hasCachedHistorySnapshot = false;
            _historyCacheNotice = "";
            _draftNote = "";
            _initialNote = "";
            OnPropertyChanged(nameof(DraftNote));
            OnPropertyChanged(nameof(History));
            OnPropertyChanged(nameof(HasHistory));
            OnPropertyChanged(nameof(HasNoHistory));
            OnPropertyChanged(nameof(ShowNoHistoryMessage));
            OnPropertyChanged(nameof(HistoryPage));
            OnPropertyChanged(nameof(HistoryTotalCount));
            OnPropertyChanged(nameof(HistoryPageLabel));
            OnPropertyChanged(nameof(IsShowingCachedHistory));
            OnPropertyChanged(nameof(HasCachedHistorySnapshot));
            OnPropertyChanged(nameof(HistoryCacheNotice));
        }

        private async Task StartOperationAsync(string kind)
        {
            if (IsLoading || IsSending || _operations.Pending != null || IsOffline || HasRecoveryError || SelectedItem == null || !IsValidNote) return;
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
                await _operations.PersistAsync(operation);
            }
            catch (Exception)
            {
                IsSending = false;
                HasConfirmedError = true;
                OperationMessage = "요청을 안전하게 보관하지 못해 서버에 보내지 않았습니다.";
                UpdateState();
                return;
            }
            OnPropertyChanged(nameof(HasPendingOperation));
            HasConfirmedError = false;
            await SendPendingAsync();
        }

        public async Task RetryPendingAsync()
        {
            if (_operations.Pending == null || IsSending) return;
            await SendPendingAsync();
        }

        private async Task SendPendingAsync()
        {
            var operation = _operations.Pending;
            if (operation == null) return;
            IsSending = true;
            HasUnknownResult = false;
            OperationMessage = "요청 결과를 확인하는 중입니다.";
            UpdateState();
            var attempt = await _operations.SendAsync();
            IsSending = false;
            OnPropertyChanged(nameof(HasPendingOperation));
            if (!attempt.IsConfirmed)
            {
                IsOffline = true;
                HasUnknownResult = true;
                OperationMessage = "요청 결과를 확인하지 못했습니다. 같은 요청으로 다시 시도할 수 있습니다.";
                UpdateState();
                return;
            }
            if (attempt.CleanupFailed)
            {
                HasUnknownResult = true;
                OperationMessage = attempt.Rejection == null
                    ? "서버 처리 결과는 받았지만 요청 기록을 정리하지 못했습니다. 같은 요청으로 다시 확인해 주세요."
                    : "요청은 거부되었지만 요청 기록을 정리하지 못했습니다. 같은 요청으로 다시 확인해 주세요.";
                UpdateState();
                return;
            }
            if (attempt.Rejection != null)
            {
                HasUnknownResult = false;
                HasConfirmedError = true;
                IsOffline = false;
                OperationMessage = attempt.Rejection.Message;
                await RefreshAsync(preserveDraft: true);
                UpdateState();
                return;
            }

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

        private void CancelDraft()
        {
            if (!IsDirty || _operations.Pending != null || IsSending) return;
            _draftNote = _initialNote;
            _selectedBorrower = Borrowers.FirstOrDefault(x => x.Id == _initialBorrowerId);
            OnPropertyChanged(nameof(DraftNote));
            OnPropertyChanged(nameof(SelectedBorrower));
            OnPropertyChanged(nameof(IsDirty));
            UpdateState();
        }

        private void UpdateState()
        {
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(HasActiveLoan));
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(CanNavigate));
            OnPropertyChanged(nameof(CanChangeQuery));
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(CanChooseBorrower));
            OnPropertyChanged(nameof(CanBorrow));
            OnPropertyChanged(nameof(CanReturn));
            OnPropertyChanged(nameof(CanRetry));
            OnPropertyChanged(nameof(CanExportDiagnostics));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasNoResultsLoadError));
            OnPropertyChanged(nameof(HasStaleResults));
            OnPropertyChanged(nameof(EquipmentTotalCount));
            OnPropertyChanged(nameof(EquipmentPageLabel));
            OnPropertyChanged(nameof(CanPreviousEquipmentPage));
            OnPropertyChanged(nameof(CanNextEquipmentPage));
            OnPropertyChanged(nameof(HistoryPageLabel));
            OnPropertyChanged(nameof(CanPreviousHistoryPage));
            OnPropertyChanged(nameof(CanNextHistoryPage));
            RefreshCommand.RaiseCanExecuteChanged();
            PreviousPageCommand.RaiseCanExecuteChanged();
            NextPageCommand.RaiseCanExecuteChanged();
            PreviousHistoryPageCommand.RaiseCanExecuteChanged();
            NextHistoryPageCommand.RaiseCanExecuteChanged();
            BorrowCommand.RaiseCanExecuteChanged();
            ReturnCommand.RaiseCanExecuteChanged();
            RetryCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
            ExportDiagnosticsCommand.RaiseCanExecuteChanged();
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
}
