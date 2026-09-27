using System.Net.Http;
using System.Runtime.Serialization.Json;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfDemo;

namespace WpfDemo.Tests;

[TestClass]
public sealed class LendingViewModelTests
{
    [TestMethod]
    public async Task SearchSelectionAndFilterShowEquipmentAndCurrentLoan()
    {
        using var rig = new TestRig();
        await rig.ViewModel.InitializeAsync();
        Assert.AreEqual(5, rig.ViewModel.VisibleItems.Count);

        rig.ViewModel.SearchText = "eq-1004";
        Assert.AreEqual(1, rig.ViewModel.VisibleItems.Count);
        rig.ViewModel.SelectedItem = rig.ViewModel.VisibleItems[0];
        Assert.IsTrue(rig.ViewModel.HasActiveLoan);
        Assert.IsTrue(rig.ViewModel.CanReturn);
        Assert.IsFalse(rig.ViewModel.CanChooseBorrower);
        Assert.IsFalse(rig.ViewModel.IsDirty);

        rig.ViewModel.SearchText = "missing";
        Assert.IsFalse(rig.ViewModel.HasSelection);
        Assert.IsTrue(rig.ViewModel.IsEmpty);
    }

    [TestMethod]
    public async Task ValidationAndCancelKeepOnlyValidOperationInput()
    {
        using var rig = new TestRig();
        await rig.ViewModel.InitializeAsync();
        rig.ViewModel.SelectedItem = rig.ViewModel.VisibleItems.Single(x => x.Id == 1001);
        Assert.IsTrue(rig.ViewModel.CanChooseBorrower);
        rig.ViewModel.DraftNote = "   ";
        Assert.IsFalse(rig.ViewModel.CanBorrow);
        StringAssert.Contains(rig.ViewModel.ValidationMessage, "공백");
        rig.ViewModel.DraftNote = new string('x', 201);
        Assert.IsFalse(rig.ViewModel.CanBorrow);
        StringAssert.Contains(rig.ViewModel.ValidationMessage, "200");

        rig.ViewModel.DraftNote = "";
        rig.ViewModel.SelectedBorrower = rig.ViewModel.Borrowers.Single(x => x.Id == "B");
        Assert.IsTrue(rig.ViewModel.CanBorrow);
        Assert.IsTrue(rig.ViewModel.IsDirty);
        Assert.IsFalse(rig.ViewModel.CanNavigate);
        rig.ViewModel.CancelCommand.Execute(null);
        Assert.IsFalse(rig.ViewModel.IsDirty);
        Assert.AreEqual("A", rig.ViewModel.SelectedBorrower.Id);
    }

    [TestMethod]
    public async Task PendingRequestIsSavedBeforeSendingAndLocksSecondOperation()
    {
        using var rig = new TestRig();
        await rig.ViewModel.InitializeAsync();
        rig.ViewModel.SelectedItem = rig.ViewModel.VisibleItems.Single(x => x.Id == 1001);
        rig.ViewModel.DraftNote = "first loan";
        var sendingStarted = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Api.BeforeSend = operation =>
        {
            Assert.AreEqual(operation.OperationId, rig.Store.Current.OperationId);
            sendingStarted.SetResult(null);
            return rig.Api.SendGate.Task;
        };

        var send = rig.ViewModel.BorrowAsync();
        await sendingStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsTrue(rig.ViewModel.IsSending);
        Assert.IsFalse(rig.ViewModel.CanNavigate);
        Assert.IsFalse(rig.ViewModel.CanEdit);
        Assert.IsFalse(rig.ViewModel.BorrowCommand.CanExecute(null));
        Assert.IsFalse(rig.ViewModel.CancelCommand.CanExecute(null));
        Assert.IsNotNull(rig.Store.Current);
        Assert.IsFalse(send.IsCompleted);

        rig.Api.SendGate.SetResult(null);
        await send;
        Assert.IsNull(rig.Store.Current);
        Assert.AreEqual(EquipmentStates.OnLoan, rig.ViewModel.SelectedItem.Status);
        Assert.AreEqual("대여가 완료되었습니다.", rig.ViewModel.OperationMessage);
    }

    [TestMethod]
    public async Task SlowLocalSaveLocksInputAndPreventsSecondRequestBeforeHttpStarts()
    {
        using var rig = new TestRig();
        using var slowStore = new BlockingStore(rig.Store);
        var vm = new LendingViewModel(rig.Api, slowStore, "http://127.0.0.1:5187", "A");
        await vm.InitializeAsync();
        vm.SelectedItem = vm.VisibleItems.Single(x => x.Id == 1001);
        var selected = vm.SelectedItem;
        var send = vm.BorrowAsync();
        try
        {
            Assert.IsTrue(slowStore.Entered.Wait(TimeSpan.FromSeconds(3)));
            Assert.IsTrue(vm.IsSending);
            Assert.IsFalse(vm.CanNavigate);
            Assert.IsFalse(vm.CanEdit);
            Assert.IsFalse(vm.BorrowCommand.CanExecute(null));
            Assert.IsFalse(vm.RefreshCommand.CanExecute(null));
            vm.SelectedItem = vm.VisibleItems.Single(x => x.Id == 1002);
            vm.DraftNote = "changed during local save";
            await vm.BorrowAsync();
            Assert.AreSame(selected, vm.SelectedItem);
            Assert.AreEqual("", vm.DraftNote);
            Assert.AreEqual(1, slowStore.SaveCount);
            Assert.AreEqual(0, rig.Api.SentOperations.Count);
        }
        finally
        {
            slowStore.Release.Set();
            await send;
        }
        Assert.AreEqual(1, rig.Api.SentOperations.Count);
        Assert.AreEqual(1001, rig.Api.SentOperations[0].EquipmentId);
    }

    [TestMethod]
    public async Task LocalSaveFailureSendsNothingAndUnlocksOriginalDraft()
    {
        using var rig = new TestRig();
        using var failingStore = new BlockingStore(rig.Store) { SaveFailure = new IOException("disk unavailable") };
        failingStore.Release.Set();
        var vm = new LendingViewModel(rig.Api, failingStore, "http://127.0.0.1:5187", "A");
        await vm.InitializeAsync();
        vm.SelectedItem = vm.VisibleItems.Single(x => x.Id == 1001);
        vm.DraftNote = "keep after local save failure";

        await vm.BorrowAsync();

        Assert.AreEqual(0, rig.Api.SentOperations.Count);
        Assert.IsNull(rig.Store.Current);
        Assert.IsFalse(vm.IsSending);
        Assert.IsFalse(vm.HasPendingOperation);
        Assert.IsTrue(vm.CanEdit);
        Assert.IsTrue(vm.CanBorrow);
        Assert.AreEqual("keep after local save failure", vm.DraftNote);
        StringAssert.Contains(vm.OperationMessage, "서버에 보내지 않았습니다");
    }

    [TestMethod]
    public async Task SuccessfulLoanLeavesAvailableFilterButKeepsCompletionNotice()
    {
        using var rig = new TestRig();
        await rig.ViewModel.InitializeAsync();
        rig.ViewModel.StatusFilter = EquipmentStates.Available;
        rig.ViewModel.SelectedItem = rig.ViewModel.VisibleItems.Single(x => x.Id == 1001);

        await rig.ViewModel.BorrowAsync();

        Assert.AreEqual(2, rig.ViewModel.VisibleItems.Count);
        Assert.IsFalse(rig.ViewModel.VisibleItems.Any(x => x.Id == 1001));
        Assert.IsNull(rig.ViewModel.SelectedItem);
        Assert.IsFalse(rig.ViewModel.HasSelection);
        Assert.IsFalse(rig.ViewModel.IsDirty);
        Assert.IsTrue(rig.ViewModel.CanNavigate);
        Assert.AreEqual("대여가 완료되었습니다.", rig.ViewModel.OperationMessage);
    }

    [TestMethod]
    public async Task OlderHistoryResponseCannotOverwriteNewerSelectionRequest()
    {
        using var rig = new TestRig();
        await rig.ViewModel.InitializeAsync();
        var requests = new List<TaskCompletionSource<IReadOnlyList<LoanHistoryEntry>>>();
        rig.Api.HistoryResponse = _ =>
        {
            var request = new TaskCompletionSource<IReadOnlyList<LoanHistoryEntry>>();
            requests.Add(request);
            return request.Task;
        };
        var firstItem = rig.ViewModel.VisibleItems.Single(x => x.Id == 1001);
        rig.ViewModel.SelectedItem = firstItem;
        rig.ViewModel.SelectedItem = rig.ViewModel.VisibleItems.Single(x => x.Id == 1002);
        rig.ViewModel.SelectedItem = firstItem;
        Assert.AreEqual(3, requests.Count);

        requests[1].SetResult(Array.Empty<LoanHistoryEntry>());
        Assert.IsTrue(rig.ViewModel.IsLoadingHistory, "An older request must not clear the current loading state.");
        requests[2].SetResult(new[] { new LoanHistoryEntry { Id = "current" } });
        Assert.AreEqual("current", rig.ViewModel.History.Single().Id);
        requests[0].SetResult(new[] { new LoanHistoryEntry { Id = "old" } });
        Assert.AreEqual("current", rig.ViewModel.History.Single().Id);
        Assert.IsFalse(rig.ViewModel.IsLoadingHistory);
    }

    [TestMethod]
    public async Task UnconfirmedFailureRetriesSameKeyAndExactBody()
    {
        using var rig = new TestRig();
        await rig.ViewModel.InitializeAsync();
        rig.ViewModel.SelectedItem = rig.ViewModel.VisibleItems.Single(x => x.Id == 1001);
        rig.ViewModel.DraftNote = "keep this request";
        rig.Api.FailAfterCommitNext = new HttpRequestException("connection lost");

        await rig.ViewModel.BorrowAsync();
        var first = rig.Api.SentOperations.Single();
        Assert.IsTrue(rig.ViewModel.HasUnknownResult);
        Assert.AreEqual(first.OperationId, rig.Store.Current.OperationId);
        Assert.AreEqual(first.BodyJson, rig.Store.Current.BodyJson);
        Assert.AreEqual("keep this request", rig.ViewModel.DraftNote);
        Assert.IsFalse(rig.ViewModel.CanBorrow);

        await rig.ViewModel.RetryPendingAsync();
        Assert.AreEqual(2, rig.Api.SentOperations.Count);
        Assert.AreEqual(first.OperationId, rig.Api.SentOperations[1].OperationId);
        Assert.AreEqual(first.BodyJson, rig.Api.SentOperations[1].BodyJson);
        Assert.IsNull(rig.Store.Current);
        Assert.IsFalse(rig.ViewModel.HasUnknownResult);
        Assert.AreEqual(EquipmentStates.OnLoan, rig.ViewModel.SelectedItem.Status);
    }

    [TestMethod]
    public async Task ConfirmedVersionConflictKeepsDraftAndRefreshesEquipment()
    {
        using var rig = new TestRig();
        await rig.ViewModel.InitializeAsync();
        rig.ViewModel.SelectedItem = rig.ViewModel.VisibleItems.Single(x => x.Id == 1001);
        rig.ViewModel.DraftNote = "keep for review";
        rig.Api.BeforeSend = _ =>
        {
            rig.Api.MakeItemOnLoan(1001);
            throw new ApiResponseException(409, "VersionConflict", "장비 정보가 변경되었습니다.", true);
        };

        await rig.ViewModel.BorrowAsync();
        Assert.IsNull(rig.Store.Current);
        Assert.IsFalse(rig.ViewModel.HasUnknownResult);
        Assert.AreEqual("keep for review", rig.ViewModel.DraftNote);
        Assert.AreEqual(EquipmentStates.OnLoan, rig.ViewModel.SelectedItem.Status);
        Assert.IsFalse(rig.ViewModel.CanBorrow);
        Assert.IsTrue(rig.ViewModel.HasConfirmedError);
    }

    [TestMethod]
    public async Task LoadFailureKeepsEarlierResultsVisibleButReadOnlyAndCanRetry()
    {
        using var rig = new TestRig();
        await rig.ViewModel.InitializeAsync();
        rig.Api.FailLoad = true;

        await rig.ViewModel.RefreshAsync();

        Assert.IsTrue(rig.ViewModel.HasLoadError);
        Assert.IsTrue(rig.ViewModel.HasStaleResults);
        Assert.AreEqual(5, rig.ViewModel.VisibleItems.Count);
        Assert.IsTrue(rig.ViewModel.IsOffline);
        Assert.IsFalse(rig.ViewModel.CanBorrow);

        rig.Api.FailLoad = false;
        await rig.ViewModel.RefreshAsync();
        Assert.IsFalse(rig.ViewModel.HasLoadError);
        Assert.IsFalse(rig.ViewModel.IsOffline);
        Assert.AreEqual(5, rig.ViewModel.VisibleItems.Count);
    }

    [TestMethod]
    public async Task StartupRetriesPersistedOperationAndKeepsItsOriginalIdentity()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wpf-demo-client-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var api = new FakeApi();
            var initialCatalog = await api.GetEquipmentAsync();
            var operation = PendingOperationStore.CreateBorrow("http://127.0.0.1:5187", initialCatalog.DatasetId,
                1001, initialCatalog.Items.Single(x => x.Id == 1001).Version, "A", "saved before restart");
            using (var originalStore = new PendingOperationStore(directory)) originalStore.Save(operation);

            using var recoveredStore = new PendingOperationStore(directory);
            var vm = new LendingViewModel(api, recoveredStore, "http://127.0.0.1:5187", "A");
            await vm.InitializeAsync();

            Assert.AreEqual(1, api.SentOperations.Count);
            Assert.AreEqual(operation.OperationId, api.SentOperations[0].OperationId);
            Assert.AreEqual(operation.BodyJson, api.SentOperations[0].BodyJson);
            Assert.IsNull(recoveredStore.Current);
            Assert.IsFalse(vm.HasPendingOperation);
            Assert.AreEqual(EquipmentStates.OnLoan, vm.Equipment.Single(x => x.Id == 1001).Status);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task CorruptPendingFileBlocksNewOperationsWithoutDeletingEvidence()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wpf-demo-client-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var pendingPath = Path.Combine(directory, "pending-operation.json");
        File.WriteAllText(pendingPath, "not valid json");
        try
        {
            using var store = new PendingOperationStore(directory);
            var api = new FakeApi();
            var vm = new LendingViewModel(api, store, "http://127.0.0.1:5187", "A");
            await vm.InitializeAsync();
            vm.SelectedItem = vm.VisibleItems.Single(x => x.Id == 1001);

            Assert.IsTrue(vm.HasRecoveryError);
            Assert.IsFalse(vm.CanBorrow);
            Assert.IsTrue(File.Exists(pendingPath));
            Assert.AreEqual(0, api.SentOperations.Count);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public void ClientDataDirectoryCannotBeOpenedByTwoInstances()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wpf-demo-client-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var first = new PendingOperationStore(directory);
            Assert.ThrowsException<IOException>(() => new PendingOperationStore(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task CorruptPendingTargetIsRejectedAndPreservedWithoutSending()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wpf-demo-client-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var operationId = Guid.NewGuid().ToString("D");
        var datasetId = Guid.NewGuid().ToString("D");
        var operation = new PendingOperation
        {
            OperationId = operationId,
            Kind = "Borrow",
            EquipmentId = 1001,
            DatasetId = datasetId,
            ApiAddress = "http://127.0.0.1:5187",
            RelativePath = "//example.invalid/collect",
            BodyJson = Write(new BorrowCommand
            {
                OperationId = operationId,
                DatasetId = datasetId,
                ExpectedVersion = 1,
                BorrowerId = "A",
                Note = ""
            })
        };
        var pendingPath = Path.Combine(directory, "pending-operation.json");
        using (var stream = File.Create(pendingPath))
            new DataContractJsonSerializer(typeof(PendingOperation)).WriteObject(stream, operation);

        try
        {
            using var store = new PendingOperationStore(directory);
            var api = new FakeApi();
            var vm = new LendingViewModel(api, store, "http://127.0.0.1:5187", "A");
            await vm.InitializeAsync();

            Assert.IsTrue(vm.HasRecoveryError);
            Assert.IsFalse(vm.CanBorrow);
            Assert.IsTrue(File.Exists(pendingPath));
            Assert.AreEqual(0, api.SentOperations.Count);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string Write<T>(T value)
    {
        using var stream = new MemoryStream();
        new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private sealed class TestRig : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "wpf-demo-client-test-" + Guid.NewGuid().ToString("N"));
        public FakeApi Api { get; }
        public PendingOperationStore Store { get; }
        public LendingViewModel ViewModel { get; }

        public TestRig()
        {
            Directory.CreateDirectory(_directory);
            Api = new FakeApi();
            Store = new PendingOperationStore(_directory);
            ViewModel = new LendingViewModel(Api, Store, "http://127.0.0.1:5187", "A");
        }

        public void Dispose()
        {
            Store.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class BlockingStore : IPendingOperationStore, IDisposable
    {
        private readonly IPendingOperationStore _inner;
        public ManualResetEventSlim Entered { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);
        public int SaveCount { get; private set; }
        public Exception SaveFailure { get; set; }
        public PendingOperation Current => _inner.Current;

        public BlockingStore(IPendingOperationStore inner) { _inner = inner; }

        public void Save(PendingOperation operation)
        {
            SaveCount++;
            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test save was not released.");
            if (SaveFailure != null) throw SaveFailure;
            _inner.Save(operation);
        }

        public void Delete(string operationId) => _inner.Delete(operationId);
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }

    private sealed class FakeApi : ILendingApi
    {
        private readonly string _datasetId = Guid.NewGuid().ToString("D");
        private readonly List<LoanHistoryEntry> _history = new();
        public bool FailLoad { get; set; }
        public Exception FailAfterCommitNext { get; set; }
        public Func<PendingOperation, Task> BeforeSend { get; set; }
        public Func<int, Task<IReadOnlyList<LoanHistoryEntry>>> HistoryResponse { get; set; }
        public TaskCompletionSource<object> SendGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<PendingOperation> SentOperations { get; } = new();
        private readonly Dictionary<string, LoanOperationResponse> _receipts = new();
        public List<EquipmentItem> Items { get; } = new()
        {
            Item(1001, "EQ-1001", "Laptop A", EquipmentStates.Available),
            Item(1002, "EQ-1002", "Laptop B", EquipmentStates.Available),
            Item(1003, "EQ-1003", "Monitor A", EquipmentStates.Available),
            LoanedItem(),
            Item(1005, "EQ-1005", "Monitor B", EquipmentStates.Maintenance)
        };

        public Task<EquipmentListResponse> GetEquipmentAsync()
        {
            if (FailLoad) throw new HttpRequestException("connection unavailable");
            return Task.FromResult(new EquipmentListResponse
            {
                DatasetId = _datasetId,
                Items = Items.Select(Clone).ToList(),
                Borrowers = new List<Borrower> { new() { Id = "A", DisplayName = "Borrower A" }, new() { Id = "B", DisplayName = "Borrower B" } }
            });
        }

        public Task<IReadOnlyList<LoanHistoryEntry>> GetHistoryAsync(int equipmentId)
        {
            if (HistoryResponse != null) return HistoryResponse(equipmentId);
            return Task.FromResult<IReadOnlyList<LoanHistoryEntry>>(_history.Where(x => x.OperationId != null).Select(Clone).ToArray());
        }

        public async Task<LoanOperationResponse> SendOperationAsync(PendingOperation operation)
        {
            SentOperations.Add(operation);
            if (BeforeSend != null) await BeforeSend(operation);
            if (_receipts.TryGetValue(operation.OperationId, out var savedReceipt)) return savedReceipt;
            var item = Items.Single(x => x.Id == operation.EquipmentId);
            string loanId;
            string eventId;
            if (operation.Kind == "Borrow")
            {
                var command = Read<BorrowCommand>(operation.BodyJson);
                MakeItemOnLoan(item.Id, command.BorrowerId);
                loanId = item.ActiveLoan.Id;
                eventId = Guid.NewGuid().ToString("D");
                _history.Add(new LoanHistoryEntry { Id = eventId, LoanId = loanId, OperationId = operation.OperationId,
                    Kind = "Borrowed", BorrowerName = item.ActiveLoan.BorrowerName, OccurredAtUtc = DateTime.UtcNow.ToString("O"), Note = command.Note });
            }
            else
            {
                var command = Read<ReturnCommand>(operation.BodyJson);
                loanId = command.LoanId;
                eventId = Guid.NewGuid().ToString("D");
                item.Status = EquipmentStates.Available;
                item.Version++;
                item.ActiveLoan = null;
                _history.Add(new LoanHistoryEntry { Id = eventId, LoanId = loanId,
                    OperationId = operation.OperationId, Kind = "Returned", BorrowerName = "Borrower A", OccurredAtUtc = DateTime.UtcNow.ToString("O"), Note = command.Note });
            }
            var response = new LoanOperationResponse { OperationId = operation.OperationId, Success = true,
                Code = operation.Kind == "Borrow" ? "Borrowed" : "Returned", Equipment = Clone(item),
                LoanId = loanId, EventId = eventId };
            _receipts.Add(operation.OperationId, response);
            if (FailAfterCommitNext != null)
            {
                var error = FailAfterCommitNext;
                FailAfterCommitNext = null;
                throw error;
            }
            return response;
        }

        public void MakeItemOnLoan(int id, string borrowerId = "B")
        {
            var item = Items.Single(x => x.Id == id);
            item.Status = EquipmentStates.OnLoan;
            item.Version++;
            item.ActiveLoan = new ActiveLoan { Id = Guid.NewGuid().ToString("D"), BorrowerId = borrowerId,
                BorrowerName = "Borrower " + borrowerId, BorrowedAtUtc = DateTime.UtcNow.ToString("O") };
        }

        private static EquipmentItem LoanedItem()
        {
            var item = Item(1004, "EQ-1004", "Test device", EquipmentStates.OnLoan);
            item.ActiveLoan = new ActiveLoan { Id = "seed-loan-1004", BorrowerId = "B", BorrowerName = "Borrower B", BorrowedAtUtc = DateTime.UtcNow.ToString("O") };
            return item;
        }

        private static EquipmentItem Item(int id, string code, string name, string status) =>
            new() { Id = id, Code = code, Name = name, Category = "Equipment", Status = status, Version = 1 };

        private static EquipmentItem Clone(EquipmentItem item) => new()
        {
            Id = item.Id, Code = item.Code, Name = item.Name, Category = item.Category, Status = item.Status,
            Version = item.Version, ActiveLoan = item.ActiveLoan == null ? null : new ActiveLoan
            {
                Id = item.ActiveLoan.Id, BorrowerId = item.ActiveLoan.BorrowerId,
                BorrowerName = item.ActiveLoan.BorrowerName, BorrowedAtUtc = item.ActiveLoan.BorrowedAtUtc
            }
        };

        private static LoanHistoryEntry Clone(LoanHistoryEntry entry) => new()
        {
            Id = entry.Id, LoanId = entry.LoanId, OperationId = entry.OperationId, Kind = entry.Kind,
            BorrowerName = entry.BorrowerName, OccurredAtUtc = entry.OccurredAtUtc, Note = entry.Note
        };

        private static T Read<T>(string body)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
            return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }
    }
}
