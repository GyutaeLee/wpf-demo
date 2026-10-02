using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfDemo;

namespace WpfDemo.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ApiIntegrationTests
{
    [TestMethod]
    public async Task FirstDatabaseHasFiveEquipmentItemsAndFixedBorrowers()
    {
        using var host = new TestApiHost();
        var catalog = await host.Client.GetFromJsonAsync<EquipmentListResponse>("/api/equipment");

        Assert.IsNotNull(catalog);
        Assert.AreEqual(5, catalog.Items.Count);
        Assert.AreEqual(3, catalog.Items.Count(x => x.Status == EquipmentStates.Available));
        Assert.AreEqual(1, catalog.Items.Count(x => x.Status == EquipmentStates.OnLoan));
        Assert.AreEqual(1, catalog.Items.Count(x => x.Status == EquipmentStates.Maintenance));
        Assert.AreEqual(2, catalog.Borrowers.Count);
        Assert.IsTrue(Guid.TryParse(catalog.DatasetId, out _));
    }

    [TestMethod]
    public async Task LargeProfileCreatesDeterministicEquipmentAndHistoryOnlyForANewDatabase()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wpf-demo-large-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string datasetId;
            using (var first = new TestApiHost(directory, "Large"))
            {
                var catalog = await first.GetCatalogAsync();
                datasetId = catalog.DatasetId;
                Assert.AreEqual(10000, catalog.TotalCount);
                Assert.AreEqual(50, catalog.Items.Count);
                var lastPage = await first.Client.GetFromJsonAsync<EquipmentListResponse>("/api/equipment?page=100&pageSize=100");
                Assert.AreEqual("EQ-11000", lastPage.Items[^1].Code);
                var history = await first.Client.GetFromJsonAsync<LoanHistoryListResponse>("/api/equipment/1001/history?page=1&pageSize=100");
                Assert.AreEqual(20000, history.TotalCount);
                Assert.AreEqual(100, history.Items.Count);
            }

            using (var restarted = new TestApiHost(directory, "Large"))
            {
                var catalog = await restarted.GetCatalogAsync();
                Assert.AreEqual(datasetId, catalog.DatasetId);
                Assert.AreEqual(10000, catalog.TotalCount);
                Assert.AreEqual(20000, (await restarted.Client.GetFromJsonAsync<LoanHistoryListResponse>("/api/equipment/1001/history")).TotalCount);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task LargeProfileOnLoanEquipmentHasActiveLoansAndCanBeReturned()
    {
        using var host = new TestApiHost(profile: "Large");
        EquipmentListResponse firstPage = null;
        for (var page = 1; ; page++)
        {
            var catalog = await host.Client.GetFromJsonAsync<EquipmentListResponse>(
                "/api/equipment?status=" + Uri.EscapeDataString(EquipmentStates.OnLoan) + "&page=" + page + "&pageSize=100");
            firstPage ??= catalog;
            Assert.IsTrue(catalog.Items.All(item => item.ActiveLoan != null),
                "Every on-loan equipment item must identify the loan that can be returned.");
            if (page * catalog.PageSize >= catalog.TotalCount) break;
        }

        var item = firstPage.Items.Single(x => x.Id == 1021);
        var before = await host.Client.GetFromJsonAsync<LoanHistoryListResponse>("/api/equipment/1021/history");
        Assert.AreEqual(1, before.TotalCount);
        Assert.AreEqual(item.ActiveLoan.Id, before.Items.Single().LoanId);
        Assert.AreEqual("Borrowed", before.Items.Single().Kind);

        using var response = await host.SendReturnAsync(firstPage, item.Id, Guid.NewGuid().ToString("D"));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var latest = await host.Client.GetFromJsonAsync<EquipmentItem>("/api/equipment/1021");
        Assert.AreEqual(EquipmentStates.Available, latest.Status);
        Assert.IsNull(latest.ActiveLoan);
        var after = await host.Client.GetFromJsonAsync<LoanHistoryListResponse>("/api/equipment/1021/history");
        Assert.AreEqual(2, after.TotalCount);
        Assert.AreEqual(1, after.Items.Count(entry => entry.Kind == "Returned" && entry.LoanId == item.ActiveLoan.Id));
    }

    [TestMethod]
    public async Task EquipmentQueryFiltersOnServerAndReturnsStableBoundedPages()
    {
        using var host = new TestApiHost();
        var first = await host.Client.GetFromJsonAsync<EquipmentListResponse>(
            "/api/equipment?q=eq-100&status=%EC%82%AC%EC%9A%A9%20%EA%B0%80%EB%8A%A5&page=1&pageSize=2");
        var second = await host.Client.GetFromJsonAsync<EquipmentListResponse>(
            "/api/equipment?q=eq-100&status=%EC%82%AC%EC%9A%A9%20%EA%B0%80%EB%8A%A5&page=2&pageSize=2");

        Assert.AreEqual(3, first.TotalCount);
        Assert.AreEqual(2, first.Items.Count);
        CollectionAssert.AreEqual(new[] { 1001, 1002 }, first.Items.Select(x => x.Id).ToArray());
        Assert.AreEqual(1, second.Items.Count);
        Assert.AreEqual(1003, second.Items[0].Id);

        var capped = await host.Client.GetFromJsonAsync<EquipmentListResponse>("/api/equipment?pageSize=500");
        Assert.AreEqual(100, capped.PageSize);
        Assert.AreEqual(5, capped.TotalCount);
        Assert.AreEqual(5, capped.Items.Count);

        using var badPage = await host.Client.GetAsync("/api/equipment?page=0");
        using var badStatus = await host.Client.GetAsync("/api/equipment?status=unknown");
        Assert.AreEqual(HttpStatusCode.BadRequest, badPage.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, badStatus.StatusCode);
    }

    [TestMethod]
    public async Task DiagnosticsExposeOnlyTheLastHundredRequestMetadataEvents()
    {
        using var host = new TestApiHost();
        var requestIds = new List<string>();
        for (var index = 0; index < 105; index++)
        {
            var requestId = Guid.NewGuid().ToString("D");
            requestIds.Add(requestId);
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/equipment?q=private-search-text");
            request.Headers.Add("X-Request-ID", requestId);
            using var response = await host.Client.SendAsync(request);
            Assert.AreEqual(requestId, response.Headers.GetValues("X-Request-ID").Single());
        }

        var diagnostics = await host.Client.GetFromJsonAsync<DiagnosticsResponse>("/api/diagnostics/recent");
        Assert.IsTrue(diagnostics.IsPartial);
        Assert.IsTrue(DateTime.TryParse(diagnostics.ServerStartedAtUtc, out _));
        Assert.AreEqual(100, diagnostics.Events.Count);
        Assert.IsTrue(diagnostics.Events.All(item => Guid.TryParse(item.RequestId, out _) && item.StatusCode == 200));
        Assert.IsFalse(diagnostics.Events.Any(item => item.Route.Contains("private-search-text", StringComparison.Ordinal)));
        Assert.IsTrue(diagnostics.Events.Any(item => item.RequestId == requestIds[^1]));
    }

    [TestMethod]
    public async Task ClientWireEncodingSupportsBorrowReplayConflictAndReturn()
    {
        using var host = new TestApiHost();
        var address = host.Client.BaseAddress.AbsoluteUri;
        var api = new HttpLendingApi(address, host.Client);
        var catalog = await api.GetEquipmentAsync();
        var item = catalog.Items.Single(x => x.Id == 1001);
        var borrow = PendingOperationStore.CreateBorrow(address, catalog.DatasetId, item.Id, item.Version, "A", "한글 메모");
        var borrowed = await api.SendOperationAsync(borrow);
        var replayed = await api.SendOperationAsync(borrow);
        Assert.AreEqual(borrow.OperationId, borrowed.OperationId);
        Assert.AreEqual(borrowed.LoanId, replayed.LoanId);
        Assert.AreEqual("한글 메모", (await api.GetHistoryAsync(item.Id)).Single().Note);

        var stale = PendingOperationStore.CreateBorrow(address, catalog.DatasetId, item.Id, item.Version, "B", "");
        var conflict = await Assert.ThrowsExceptionAsync<ApiResponseException>(() => api.SendOperationAsync(stale));
        Assert.AreEqual("VersionConflict", conflict.Code);
        Assert.IsTrue(conflict.IsConfirmed);

        var latest = (await api.GetEquipmentAsync()).Items.Single(x => x.Id == item.Id);
        var returned = await api.SendOperationAsync(PendingOperationStore.CreateReturn(address, catalog.DatasetId, latest, ""));
        Assert.AreEqual("Returned", returned.Code);
        Assert.AreEqual(EquipmentStates.Available, (await api.GetEquipmentAsync()).Items.Single(x => x.Id == item.Id).Status);
        Assert.AreEqual(2, (await api.GetHistoryAsync(item.Id)).Count);
    }

    [TestMethod]
    public async Task CommandsForMissingEquipmentReturnNotFoundWithoutChangingEquipment()
    {
        using var host = new TestApiHost();
        var catalog = await host.GetCatalogAsync();
        foreach (var kind in new[] { "borrow", "return" })
        {
            var operationId = Guid.NewGuid().ToString("D");
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/equipment/9999/" + kind)
            {
                Content = JsonContent.Create(new
                {
                    operationId, datasetId = catalog.DatasetId,
                    expectedVersion = 1, borrowerId = "A", loanId = "seed-loan-1004", note = ""
                })
            };
            request.Headers.Add("Idempotency-Key", operationId);
            using var response = await host.Client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
            Assert.AreEqual("EquipmentNotFound", (await response.Content.ReadFromJsonAsync<ApiErrorResponse>()).Code);
        }
        var unchanged = await host.GetCatalogAsync();
        Assert.IsTrue(unchanged.Items.All(x => x.Version == 1));
        Assert.AreEqual(3, unchanged.Items.Count(x => x.Status == EquipmentStates.Available));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" ")]
    public async Task ReturnWithoutLoanIdentityIsInvalidAndDoesNotCreateAReceipt(string loanId)
    {
        using var host = new TestApiHost();
        var catalog = await host.GetCatalogAsync();
        var operationId = Guid.NewGuid().ToString("D");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/equipment/1004/return")
        {
            Content = JsonContent.Create(new
            {
                operationId, datasetId = catalog.DatasetId, expectedVersion = 1, loanId, note = ""
            })
        };
        request.Headers.Add("Idempotency-Key", operationId);
        using var response = await host.Client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual("InvalidCommand", (await response.Content.ReadFromJsonAsync<ApiErrorResponse>()).Code);
        var unchanged = (await host.GetCatalogAsync()).Items.Single(x => x.Id == 1004);
        Assert.AreEqual(1L, unchanged.Version);
        Assert.AreEqual("seed-loan-1004", unchanged.ActiveLoan.Id);
        using var connection = new SqliteConnection("Data Source=" + host.DatabasePath);
        connection.Open();
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM Operations;";
        Assert.AreEqual(0L, count.ExecuteScalar());
    }

    [TestMethod]
    public async Task BorrowAndReturnChangeStatusAndCreateOneEventEach()
    {
        using var host = new TestApiHost();
        var catalog = await host.GetCatalogAsync();
        var borrowId = Guid.NewGuid().ToString("D");
        using var borrowed = await host.SendBorrowAsync(catalog, 1001, borrowId);
        Assert.AreEqual(HttpStatusCode.OK, borrowed.StatusCode);
        var borrowResult = await borrowed.Content.ReadFromJsonAsync<LoanOperationResponse>();
        Assert.IsTrue(borrowResult.Success);
        Assert.AreEqual(EquipmentStates.OnLoan, borrowResult.Equipment.Status);
        Assert.AreEqual(2L, borrowResult.Equipment.Version);

        var afterBorrow = await host.GetCatalogAsync();
        var returnId = Guid.NewGuid().ToString("D");
        using var returned = await host.SendReturnAsync(afterBorrow, 1001, returnId);
        Assert.AreEqual(HttpStatusCode.OK, returned.StatusCode);
        var returnResult = await returned.Content.ReadFromJsonAsync<LoanOperationResponse>();
        Assert.AreEqual(EquipmentStates.Available, returnResult.Equipment.Status);
        Assert.AreEqual(3L, returnResult.Equipment.Version);

        var history = await host.Client.GetFromJsonAsync<LoanHistoryListResponse>("/api/equipment/1001/history");
        Assert.AreEqual(2, history.TotalCount);
        Assert.AreEqual(1, history.Items.Count(x => x.Kind == "Borrowed"));
        Assert.AreEqual(1, history.Items.Count(x => x.Kind == "Returned"));
    }

    [TestMethod]
    public async Task TwoConcurrentBorrowsForSameVersionAllowOnlyOne()
    {
        using var host = new TestApiHost();
        var catalog = await host.GetCatalogAsync();
        using var start = new ManualResetEventSlim(false);
        var ready = 0;
        async Task<HttpResponseMessage> StartBorrow(string operationId)
        {
            Interlocked.Increment(ref ready);
            start.Wait();
            return await host.SendBorrowAsync(catalog, 1001, operationId);
        }

        var first = Task.Run(() => StartBorrow(Guid.NewGuid().ToString("D")));
        var second = Task.Run(() => StartBorrow(Guid.NewGuid().ToString("D")));
        Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref ready) == 2, TimeSpan.FromSeconds(3)));
        start.Set();
        using var firstResponse = await first;
        using var secondResponse = await second;

        CollectionAssert.AreEquivalent(new[] { 200, 409 }, new[] { (int)firstResponse.StatusCode, (int)secondResponse.StatusCode });
        var current = await host.GetCatalogAsync();
        var item = current.Items.Single(x => x.Id == 1001);
        Assert.AreEqual(EquipmentStates.OnLoan, item.Status);
        Assert.IsNotNull(item.ActiveLoan);
        var history = await host.Client.GetFromJsonAsync<LoanHistoryListResponse>("/api/equipment/1001/history");
        Assert.AreEqual(1, history.TotalCount);
        Assert.AreEqual(1, history.Items.Count(x => x.Kind == "Borrowed"));
    }

    [TestMethod]
    public async Task ReplayingSameOperationReturnsReceiptWithoutAnotherLoanOrEvent()
    {
        using var host = new TestApiHost();
        var catalog = await host.GetCatalogAsync();
        var operationId = Guid.NewGuid().ToString("D");
        using var first = await host.SendBorrowAsync(catalog, 1001, operationId);
        var original = await first.Content.ReadFromJsonAsync<LoanOperationResponse>();
        using var replay = await host.SendBorrowAsync(catalog, 1001, operationId);
        var repeated = await replay.Content.ReadFromJsonAsync<LoanOperationResponse>();

        Assert.AreEqual(HttpStatusCode.OK, replay.StatusCode);
        Assert.AreEqual(original.OperationId, repeated.OperationId);
        Assert.AreEqual(original.LoanId, repeated.LoanId);
        var current = await host.GetCatalogAsync();
        Assert.AreEqual(2L, current.Items.Single(x => x.Id == 1001).Version);
        Assert.AreEqual(original.LoanId, current.Items.Single(x => x.Id == 1001).ActiveLoan.Id);
        var history = await host.Client.GetFromJsonAsync<LoanHistoryListResponse>("/api/equipment/1001/history");
        Assert.AreEqual(1, history.TotalCount);
    }

    [TestMethod]
    public async Task ReusingOperationIdWithDifferentCommandIsRejectedWithoutMutation()
    {
        using var host = new TestApiHost();
        var catalog = await host.GetCatalogAsync();
        var operationId = Guid.NewGuid().ToString("D");
        using var first = await host.SendBorrowAsync(catalog, 1001, operationId, "first");
        using var changed = await host.SendBorrowAsync(catalog, 1001, operationId, "different");
        Assert.AreEqual(HttpStatusCode.Conflict, changed.StatusCode);
        var error = await changed.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.AreEqual("IdempotencyKeyReused", error.Code);
        var current = await host.GetCatalogAsync();
        Assert.AreEqual(2L, current.Items.Single(x => x.Id == 1001).Version);
        var history = await host.Client.GetFromJsonAsync<LoanHistoryListResponse>("/api/equipment/1001/history");
        Assert.AreEqual(1, history.TotalCount);
    }

    [TestMethod]
    public async Task InvalidCommandsAreRejectedWithoutChangingEquipment()
    {
        using var host = new TestApiHost();
        var catalog = await host.GetCatalogAsync();
        using var missingKey = await host.SendBorrowWithoutKeyAsync(catalog, 1001);
        Assert.AreEqual(HttpStatusCode.BadRequest, missingKey.StatusCode);
        using var whitespace = await host.SendBorrowAsync(catalog, 1001, Guid.NewGuid().ToString("D"), "   ");
        Assert.AreEqual(HttpStatusCode.BadRequest, whitespace.StatusCode);
        using var tooLong = await host.SendBorrowAsync(catalog, 1001, Guid.NewGuid().ToString("D"), new string('x', 201));
        Assert.AreEqual(HttpStatusCode.BadRequest, tooLong.StatusCode);
        using var accepted = await host.SendBorrowAsync(catalog, 1001, Guid.NewGuid().ToString("D"), new string('x', 200));
        Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode);
        var current = await host.GetCatalogAsync();
        Assert.AreEqual(EquipmentStates.OnLoan, current.Items.Single(x => x.Id == 1001).Status);
        Assert.AreEqual(1, (await host.Client.GetFromJsonAsync<LoanHistoryListResponse>("/api/equipment/1001/history")).TotalCount);
    }

    [TestMethod]
    public async Task OldReturnReplayDoesNotChangeLaterLoanState()
    {
        using var host = new TestApiHost();
        var catalog = await host.GetCatalogAsync();
        using var firstBorrow = await host.SendBorrowAsync(catalog, 1001, Guid.NewGuid().ToString("D"));
        var borrowed = await host.GetCatalogAsync();
        var oldReturnId = Guid.NewGuid().ToString("D");
        using var firstReturn = await host.SendReturnAsync(borrowed, 1001, oldReturnId);
        var originalReceipt = await firstReturn.Content.ReadFromJsonAsync<LoanOperationResponse>();

        var beforeSecondLoan = await host.GetCatalogAsync();
        using var secondBorrow = await host.SendBorrowAsync(beforeSecondLoan, 1001, Guid.NewGuid().ToString("D"));
        using var oldReplay = await host.SendReturnAsync(borrowed, 1001, oldReturnId);
        var replayReceipt = await oldReplay.Content.ReadFromJsonAsync<LoanOperationResponse>();
        var latest = await host.GetCatalogAsync();
        var current = latest.Items.Single(x => x.Id == 1001);

        Assert.AreEqual(HttpStatusCode.OK, oldReplay.StatusCode);
        Assert.AreEqual(originalReceipt.LoanId, replayReceipt.LoanId);
        Assert.AreNotEqual(originalReceipt.LoanId, current.ActiveLoan.Id);
        Assert.AreEqual(EquipmentStates.OnLoan, current.Status);
        Assert.AreEqual(4L, current.Version);
        Assert.AreEqual(3, (await host.Client.GetFromJsonAsync<LoanHistoryListResponse>("/api/equipment/1001/history")).TotalCount);
    }

    [TestMethod]
    public async Task ServerRestartKeepsDatasetStateHistoryAndOperationReceipt()
    {
        var path = Path.Combine(Path.GetTempPath(), "wpf-demo-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            string datasetId;
            EquipmentListResponse catalog;
            var operationId = Guid.NewGuid().ToString("D");
            using (var firstHost = new TestApiHost(path))
            {
                catalog = await firstHost.GetCatalogAsync();
                datasetId = catalog.DatasetId;
                using var response = await firstHost.SendBorrowAsync(catalog, 1001, operationId);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }
            using (var restarted = new TestApiHost(path))
            {
                var afterRestart = await restarted.GetCatalogAsync();
                Assert.AreEqual(datasetId, afterRestart.DatasetId);
                Assert.AreEqual(EquipmentStates.OnLoan, afterRestart.Items.Single(x => x.Id == 1001).Status);
                using var replay = await restarted.SendBorrowAsync(catalog, 1001, operationId);
                Assert.AreEqual(HttpStatusCode.OK, replay.StatusCode);
                Assert.AreEqual(1, (await restarted.Client.GetFromJsonAsync<LoanHistoryListResponse>("/api/equipment/1001/history")).TotalCount);
                Assert.AreEqual(2L, (await restarted.GetCatalogAsync()).Items.Single(x => x.Id == 1001).Version);
            }
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    [TestMethod]
    public async Task FailedHistoryInsertRollsBackStatusLoanAndOperationReceipt()
    {
        using var host = new TestApiHost();
        var catalog = await host.GetCatalogAsync();
        using (var connection = new SqliteConnection("Data Source=" + host.DatabasePath))
        {
            connection.Open();
            using var trigger = connection.CreateCommand();
            trigger.CommandText = "CREATE TRIGGER FailNewHistory BEFORE INSERT ON LoanHistory WHEN NEW.OperationId IS NOT NULL BEGIN SELECT RAISE(ABORT, 'forced test failure'); END;";
            trigger.ExecuteNonQuery();
        }

        var operationId = Guid.NewGuid().ToString("D");
        using var failed = await host.SendBorrowAsync(catalog, 1001, operationId);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        var unchanged = await host.GetCatalogAsync();
        Assert.AreEqual(EquipmentStates.Available, unchanged.Items.Single(x => x.Id == 1001).Status);
        Assert.AreEqual(1L, unchanged.Items.Single(x => x.Id == 1001).Version);
        Assert.AreEqual(0, (await host.Client.GetFromJsonAsync<LoanHistoryListResponse>("/api/equipment/1001/history")).TotalCount);

        using (var connection = new SqliteConnection("Data Source=" + host.DatabasePath))
        {
            connection.Open();
            using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TRIGGER FailNewHistory;";
            drop.ExecuteNonQuery();
        }
        using var retried = await host.SendBorrowAsync(catalog, 1001, operationId);
        Assert.AreEqual(HttpStatusCode.OK, retried.StatusCode);
        Assert.AreEqual(EquipmentStates.OnLoan, (await host.GetCatalogAsync()).Items.Single(x => x.Id == 1001).Status);
    }

    [TestMethod]
    public async Task ResetDatasetRejectsOldPendingOperation()
    {
        var path = Path.Combine(Path.GetTempPath(), "wpf-demo-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            EquipmentListResponse oldCatalog;
            string operationId;
            using (var firstHost = new TestApiHost(path))
            {
                oldCatalog = await firstHost.GetCatalogAsync();
                operationId = Guid.NewGuid().ToString("D");
            }
            SqliteConnection.ClearAllPools();
            File.Delete(Path.Combine(path, "wpf-demo.db"));
            var wal = Path.Combine(path, "wpf-demo.db-wal");
            var shm = Path.Combine(path, "wpf-demo.db-shm");
            if (File.Exists(wal)) File.Delete(wal);
            if (File.Exists(shm)) File.Delete(shm);
            using var resetHost = new TestApiHost(path);
            var newCatalog = await resetHost.GetCatalogAsync();
            Assert.AreNotEqual(oldCatalog.DatasetId, newCatalog.DatasetId);
            using var response = await resetHost.SendBorrowAsync(oldCatalog, 1001, operationId);
            Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
            Assert.AreEqual("DatasetMismatch", (await response.Content.ReadFromJsonAsync<ApiErrorResponse>()).Code);
            Assert.AreEqual(EquipmentStates.Available, (await resetHost.GetCatalogAsync()).Items.Single(x => x.Id == 1001).Status);
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    private sealed class TestApiHost : IDisposable
    {
        private readonly string _directory;
        private readonly bool _ownsDirectory;
        private readonly TestFactory _factory;
        private readonly string _previousDataDirectory;
        private readonly string _previousDataProfile;
        public HttpClient Client { get; }
        public string DatabasePath => Path.Combine(_directory, "wpf-demo.db");

        public TestApiHost(string directory = null, string profile = null)
        {
            _ownsDirectory = directory == null;
            _directory = directory ?? Path.Combine(Path.GetTempPath(), "wpf-demo-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            _previousDataDirectory = Environment.GetEnvironmentVariable("WPFDEMO_SERVER_DATA_DIR");
            _previousDataProfile = Environment.GetEnvironmentVariable("WPFDEMO_DATASET_PROFILE");
            Environment.SetEnvironmentVariable("WPFDEMO_SERVER_DATA_DIR", _directory);
            Environment.SetEnvironmentVariable("WPFDEMO_DATASET_PROFILE", profile);
            _factory = new TestFactory(_directory, profile);
            Client = _factory.CreateClient();
        }

        public Task<EquipmentListResponse> GetCatalogAsync() => Client.GetFromJsonAsync<EquipmentListResponse>("/api/equipment");

        public Task<HttpResponseMessage> SendBorrowAsync(EquipmentListResponse catalog, int equipmentId,
            string operationId, string note = "")
        {
            var requestId = operationId ?? Guid.NewGuid().ToString("D");
            return SendAsync(catalog, equipmentId, requestId, "borrow", new
            {
                operationId = requestId, datasetId = catalog.DatasetId,
                expectedVersion = catalog.Items.Single(x => x.Id == equipmentId).Version,
                borrowerId = "A", note
            });
        }

        public Task<HttpResponseMessage> SendBorrowWithoutKeyAsync(EquipmentListResponse catalog, int equipmentId)
        {
            var operationId = Guid.NewGuid().ToString("D");
            return SendAsync(catalog, equipmentId, null, "borrow", new
            {
                operationId, datasetId = catalog.DatasetId,
                expectedVersion = catalog.Items.Single(x => x.Id == equipmentId).Version,
                borrowerId = "A", note = ""
            });
        }

        public Task<HttpResponseMessage> SendReturnAsync(EquipmentListResponse catalog, int equipmentId, string operationId)
        {
            var item = catalog.Items.Single(x => x.Id == equipmentId);
            return SendAsync(catalog, equipmentId, operationId, "return", new
            {
                operationId, datasetId = catalog.DatasetId, expectedVersion = item.Version,
                loanId = item.ActiveLoan?.Id, note = ""
            });
        }

        private async Task<HttpResponseMessage> SendAsync<T>(EquipmentListResponse catalog, int equipmentId,
            string operationId, string kind, T command)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/equipment/" + equipmentId + "/" + kind)
            {
                Content = JsonContent.Create(command)
            };
            if (!string.IsNullOrWhiteSpace(operationId)) request.Headers.Add("Idempotency-Key", operationId);
            return await Client.SendAsync(request);
        }

        public void Dispose()
        {
            try
            {
                Client.Dispose();
                _factory.Dispose();
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                Environment.SetEnvironmentVariable("WPFDEMO_SERVER_DATA_DIR", _previousDataDirectory);
                Environment.SetEnvironmentVariable("WPFDEMO_DATASET_PROFILE", _previousDataProfile);
                if (_ownsDirectory && Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
            }
        }
    }

    private sealed class TestFactory : WebApplicationFactory<Program>
    {
        private readonly string _directory;
        private readonly string _profile;
        public TestFactory(string directory, string profile) { _directory = directory; _profile = profile; }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var values = new Dictionary<string, string> { ["WPFDEMO_SERVER_DATA_DIR"] = _directory };
                if (_profile != null) values["WPFDEMO_DATASET_PROFILE"] = _profile;
                configuration.AddInMemoryCollection(values);
            });
        }
    }
}
