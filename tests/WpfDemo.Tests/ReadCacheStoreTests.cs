using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfDemo;

namespace WpfDemo.Tests;

[TestClass]
public sealed class ReadCacheStoreTests
{
    private const string ApiAddress = "http://127.0.0.1:5187";

    [TestMethod]
    public void DisposedCacheReleasesDatabaseFileForExclusiveAccess()
    {
        using var fixture = new CacheFixture();
        using (var cache = new ReadCacheStore(fixture.DatabasePath))
        {
            cache.SaveEquipmentPage(ApiAddress,
                EquipmentResponse("dataset-a", Equipment(1001, "EQ-1001", EquipmentStates.Available)));
            Assert.AreEqual(1, cache.GetEquipmentPage(ApiAddress, "", EquipmentStates.All, 1, 50).CachedItemCount);
        }

        using var database = new FileStream(fixture.DatabasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.IsTrue(database.Length > 0);
    }

    [TestMethod]
    public void EquipmentCacheFiltersPagesAndSeparatesApiAndDataset()
    {
        using var fixture = new CacheFixture();
        using var cache = new ReadCacheStore(fixture.DatabasePath);
        cache.SaveEquipmentPage(ApiAddress, EquipmentResponse("dataset-a",
            Equipment(1001, "EQ-1001", EquipmentStates.Available),
            Equipment(1002, "EQ-1002", EquipmentStates.Available),
            Equipment(1003, "EQ-2001", EquipmentStates.Maintenance)));

        var secondPage = cache.GetEquipmentPage(ApiAddress + "/", "eq-100", EquipmentStates.Available, 2, 1);
        Assert.IsTrue(secondPage.HasSnapshot);
        Assert.AreEqual(3, secondPage.CachedItemCount);
        Assert.AreEqual(2, secondPage.Response.TotalCount);
        Assert.AreEqual(1002, secondPage.Response.Items.Single().Id);
        Assert.IsFalse(string.IsNullOrWhiteSpace(secondPage.LastUpdatedUtc));
        Assert.IsNull(cache.GetEquipmentPage("http://127.0.0.1:5188", "", EquipmentStates.All, 1, 50));

        cache.SaveEquipmentPage(ApiAddress, EquipmentResponse("dataset-b", Equipment(2001, "EQ-2001", EquipmentStates.Available)));
        var replacement = cache.GetEquipmentPage(ApiAddress, "", EquipmentStates.All, 1, 50);
        Assert.AreEqual("dataset-b", replacement.Response.DatasetId);
        Assert.AreEqual(1, replacement.CachedItemCount);
        Assert.AreEqual(2001, replacement.Response.Items.Single().Id);
    }

    [TestMethod]
    public void HistoryCacheKeepsRecentHundredAndRecordsAnEmptySuccessfulRead()
    {
        using var fixture = new CacheFixture();
        using var cache = new ReadCacheStore(fixture.DatabasePath);
        cache.SaveEquipmentPage(ApiAddress, EquipmentResponse("dataset-a", Equipment(1001, "EQ-1001", EquipmentStates.Available)));
        var entries = Enumerable.Range(1, 120).Select(index => new LoanHistoryEntry
        {
            Id = "event-" + index.ToString("D3"), LoanId = "loan-" + index.ToString("D3"),
            Kind = index % 2 == 0 ? "Borrowed" : "Returned", BorrowerName = "가상 대여자 A",
            OccurredAtUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(index).ToString("O"), Note = ""
        }).ToList();
        cache.SaveHistoryPage(ApiAddress, "dataset-a", 1001,
            new LoanHistoryListResponse { Items = entries, TotalCount = 120, Page = 1, PageSize = 100 });

        var recent = cache.GetHistoryPage(ApiAddress, "dataset-a", 1001, 1, 50);
        Assert.AreEqual(100, recent.CachedItemCount);
        Assert.AreEqual(120, recent.ServerTotalCountAtLastFetch);
        Assert.AreEqual(100, recent.Response.TotalCount);
        Assert.AreEqual("event-120", recent.Response.Items[0].Id);
        Assert.AreEqual("event-071", recent.Response.Items[^1].Id);

        cache.SaveHistoryPage(ApiAddress, "dataset-a", 1002,
            new LoanHistoryListResponse { Items = new List<LoanHistoryEntry>(), TotalCount = 0, Page = 1, PageSize = 50 });
        var empty = cache.GetHistoryPage(ApiAddress, "dataset-a", 1002, 1, 50);
        Assert.IsTrue(empty.HasSnapshot);
        Assert.AreEqual(0, empty.CachedItemCount);
        Assert.AreEqual(0, empty.ServerTotalCountAtLastFetch);
    }

    [TestMethod]
    public void HistoryWriteForPreviousDatasetIsIgnoredAfterCatalogReplacement()
    {
        using var fixture = new CacheFixture();
        using var cache = new ReadCacheStore(fixture.DatabasePath);
        var oldHistory = new LoanHistoryListResponse
        {
            Items = new List<LoanHistoryEntry> { new() { Id = "old-event", LoanId = "old-loan", Kind = "Borrowed", Note = "" } },
            TotalCount = 1, Page = 1, PageSize = 50
        };
        cache.SaveEquipmentPage(ApiAddress, EquipmentResponse("dataset-a", Equipment(1001, "EQ-1001", EquipmentStates.Available)));
        cache.SaveHistoryPage(ApiAddress, "dataset-a", 1001, oldHistory);
        cache.SaveEquipmentPage(ApiAddress, EquipmentResponse("dataset-b", Equipment(1001, "EQ-1001", EquipmentStates.Available)));

        cache.SaveHistoryPage(ApiAddress, "dataset-a", 1001, oldHistory);

        Assert.IsNull(cache.GetHistoryPage(ApiAddress, "dataset-a", 1001, 1, 50));
        Assert.IsNull(cache.GetHistoryPage(ApiAddress, "dataset-b", 1001, 1, 50),
            "A late write with the original dataset ID must not create history for the replacement dataset.");
    }

    [TestMethod]
    public void EquipmentCacheEvictsLeastRecentlyFetchedRowsAtItsLimit()
    {
        using var fixture = new CacheFixture();
        using var cache = new ReadCacheStore(fixture.DatabasePath);
        var initial = Enumerable.Range(1, 1000).Select(index => Equipment(index, "EQ-" + index.ToString("D5"), EquipmentStates.Available)).ToList();
        cache.SaveEquipmentPage(ApiAddress, EquipmentResponse("dataset-a", initial.ToArray()));
        cache.SaveEquipmentPage(ApiAddress, EquipmentResponse("dataset-a", initial[0]));
        cache.SaveEquipmentPage(ApiAddress, EquipmentResponse("dataset-a", Equipment(2000, "EQ-02000", EquipmentStates.Available)));

        var kept = cache.GetEquipmentPage(ApiAddress, "EQ-00001", EquipmentStates.All, 1, 50);
        var evicted = cache.GetEquipmentPage(ApiAddress, "EQ-01000", EquipmentStates.All, 1, 50);
        var all = cache.GetEquipmentPage(ApiAddress, "", EquipmentStates.All, 1, 100);
        Assert.AreEqual(1000, all.CachedItemCount);
        Assert.AreEqual(1, kept.Response.TotalCount);
        Assert.AreEqual(0, evicted.Response.TotalCount);
    }

    [TestMethod]
    public void CorruptCacheIsQuarantinedWithoutChangingPendingRequestFile()
    {
        using var fixture = new CacheFixture();
        File.WriteAllText(fixture.DatabasePath, "not a sqlite database");
        var pendingPath = Path.Combine(fixture.DirectoryPath, "pending-operation.json");
        File.WriteAllText(pendingPath, "pending request must stay byte-for-byte");

        using (var cache = new ReadCacheStore(fixture.DatabasePath))
        {
            Assert.IsNull(cache.GetEquipmentPage(ApiAddress, "", EquipmentStates.All, 1, 50));
        }

        Assert.AreEqual("pending request must stay byte-for-byte", File.ReadAllText(pendingPath));
        Assert.IsTrue(Directory.GetFiles(fixture.DirectoryPath, "read-cache.db.corrupt-*").Length > 0);
    }

    private static EquipmentListResponse EquipmentResponse(string datasetId, params EquipmentItem[] items) => new()
    {
        DatasetId = datasetId,
        Items = items.ToList(),
        Borrowers = new List<Borrower> { new() { Id = "A", DisplayName = "가상 대여자 A" } },
        TotalCount = items.Length,
        Page = 1,
        PageSize = items.Length
    };

    private static EquipmentItem Equipment(int id, string code, string status) => new()
    {
        Id = id, Code = code, Name = "장비 " + id, Category = "장비", Status = status, Version = 1
    };

    private sealed class CacheFixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "wpf-demo-cache-test-" + Guid.NewGuid().ToString("N"));
        public string DatabasePath => Path.Combine(DirectoryPath, "read-cache.db");
        public CacheFixture() { Directory.CreateDirectory(DirectoryPath); }
        public void Dispose() { Directory.Delete(DirectoryPath, recursive: true); }
    }
}
