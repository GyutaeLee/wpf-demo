using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfDemo;

namespace WpfDemo.Tests;

[TestClass]
public sealed class DiagnosticBundleTests
{
    [TestMethod]
    public void ExportContainsOnlyAllowlistedMetadataAndSanitizesRoutes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wpf-demo-diagnostics-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var operation = new PendingOperation
            {
                OperationId = Guid.NewGuid().ToString("D"), Kind = "Borrow", BodyJson = "private note text",
                ApiAddress = "http://127.0.0.1:5187", RelativePath = "/api/equipment/1001/borrow?q=private-search-text"
            };
            var log = new[]
            {
                new ClientDiagnosticEvent
                {
                    RequestId = Guid.NewGuid().ToString("D"), Operation = "GET /api/equipment?q=private-search-text",
                    OperationId = "not-an-operation-id", StatusCode = 200, ElapsedMilliseconds = 12, Result = "success"
                }
            };
            var server = new DiagnosticsResponse
            {
                IsPartial = true,
                ServerStartedAtUtc = DateTime.UtcNow.ToString("O"),
                Events = new List<RequestDiagnosticEvent>
                {
                    new() { RequestId = Guid.NewGuid().ToString("D"), Method = "GET",
                        Route = "HTTP: GET /api/equipment", StatusCode = 200, ElapsedMilliseconds = 7 },
                    new() { RequestId = Guid.NewGuid().ToString("D"), Method = "GET",
                        Route = "HTTP: GET /api/equipment?q=private-search-text", StatusCode = 200, ElapsedMilliseconds = 8 },
                    new() { RequestId = Guid.NewGuid().ToString("D"), Method = "GET",
                        Route = "HTTP: GET /api/private?private-search-text", StatusCode = 200, ElapsedMilliseconds = 9 }
                }
            };

            var path = DiagnosticBundleExporter.Export(directory, operation, log, server, 144);
            using var archive = ZipFile.OpenRead(path);
            CollectionAssert.AreEquivalent(new[]
            {
                "environment.json", "current-operation.json", "client-requests.json", "server-requests.json"
            }, archive.Entries.Select(entry => entry.FullName).ToArray());
            var content = string.Join("\n", archive.Entries.Select(ReadEntry));
            StringAssert.Contains(content, "\"dpi\":144");
            StringAssert.Contains(content, "\"route\":\"GET \\/api\\/equipment\"");
            StringAssert.Contains(content, "\"route\":\"other\"");
            StringAssert.Contains(content, "\"operation\":\"other\"");
            Assert.IsFalse(content.Contains("private note text", StringComparison.Ordinal));
            Assert.IsFalse(content.Contains("private-search-text", StringComparison.Ordinal));
            Assert.IsFalse(content.Contains(directory, StringComparison.Ordinal));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public void ExportWithoutServerMarksTheBundlePartial()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wpf-demo-partial-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = DiagnosticBundleExporter.Export(directory, null, Array.Empty<ClientDiagnosticEvent>(), null, 96);
            using var archive = ZipFile.OpenRead(path);
            var server = ReadEntry(archive.GetEntry("server-requests.json"));
            StringAssert.Contains(server, "\"isAvailable\":false");
            StringAssert.Contains(server, "\"isPartial\":true");
            StringAssert.Contains(server, "\"events\":[]");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task PublishedZipCanBeReadImmediatelyWhileExportTaskCompletes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wpf-demo-atomic-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var events = Enumerable.Range(0, 5000).Select(_ => new ClientDiagnosticEvent
            {
                RequestId = Guid.NewGuid().ToString("D"), Operation = "borrow",
                OperationId = Guid.NewGuid().ToString("D"), OccurredAtUtc = DateTime.UtcNow.ToString("O"),
                StatusCode = 200, Result = "success"
            }).ToArray();
            var export = Task.Run(() => DiagnosticBundleExporter.Export(directory, null, events, null, 96));
            try
            {
                string publishedPath = null;
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (publishedPath == null && DateTime.UtcNow < deadline)
                {
                    publishedPath = Directory.GetFiles(directory, "*.zip").SingleOrDefault();
                    if (publishedPath == null) await Task.Delay(1);
                }
                Assert.IsNotNull(publishedPath, "Export did not publish a diagnostics ZIP.");
                using var archive = ZipFile.OpenRead(publishedPath);
                Assert.AreEqual(4, archive.Entries.Count);
                foreach (var entry in archive.Entries)
                {
                    using var document = JsonDocument.Parse(ReadEntry(entry));
                    if (entry.FullName == "client-requests.json")
                        Assert.AreEqual(events.Length, document.RootElement.GetArrayLength());
                }
            }
            finally { await export; }
            Assert.AreEqual(0, Directory.GetFiles(directory, "*.tmp").Length);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public void ClientRequestLogSanitizesUnexpectedValuesBeforeWriting()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wpf-demo-client-log-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var log = new ClientRequestLog(directory);
            log.Record(new ClientDiagnosticEvent
            {
                RequestId = "private path", Operation = "/api/equipment?q=secret-search",
                OperationId = "private note", StatusCode = 700, ElapsedMilliseconds = -5, Result = "private response"
            });

            var saved = log.GetRecent().Single();
            Assert.AreEqual("", saved.RequestId);
            Assert.AreEqual("other", saved.Operation);
            Assert.AreEqual("", saved.OperationId);
            Assert.AreEqual(0, saved.StatusCode);
            Assert.AreEqual(0, saved.ElapsedMilliseconds);
            Assert.AreEqual("error", saved.Result);
            var disk = File.ReadAllText(Path.Combine(directory, "client-requests.jsonl"));
            Assert.IsFalse(disk.Contains("secret-search", StringComparison.Ordinal));
            Assert.IsFalse(disk.Contains("private note", StringComparison.Ordinal));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task RetryUsesNewRequestIdButKeepsIdempotencyKeyAndRecordsBothAttempts()
    {
        var operation = PendingOperationStore.CreateBorrow("http://127.0.0.1:5187",
            Guid.NewGuid().ToString("D"), 1001, 1, "A", "note omitted from diagnostics");
        using var handler = new RetryHandler(operation.OperationId);
        using var client = new HttpClient(handler);
        var requestLog = new MemoryRequestLog();
        var api = new HttpLendingApi("http://127.0.0.1:5187", client, requestLog);

        await Assert.ThrowsExceptionAsync<ApiResponseException>(() => api.SendOperationAsync(operation));
        var result = await api.SendOperationAsync(operation);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(2, handler.Attempts.Count);
        Assert.AreEqual(operation.OperationId, handler.Attempts[0].OperationId);
        Assert.AreEqual(operation.OperationId, handler.Attempts[1].OperationId);
        Assert.AreNotEqual(handler.Attempts[0].RequestId, handler.Attempts[1].RequestId);
        Assert.AreEqual(2, requestLog.Events.Count);
        Assert.AreEqual("unknown", requestLog.Events[0].Result);
        Assert.AreEqual("success", requestLog.Events[1].Result);
        Assert.AreEqual(operation.OperationId, requestLog.Events[0].OperationId);
        Assert.AreEqual(operation.OperationId, requestLog.Events[1].OperationId);
        Assert.IsTrue(requestLog.Events.All(item => item.Operation == "borrow"));
    }

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private sealed class RetryHandler : HttpMessageHandler
    {
        private readonly string _operationId;
        public RetryHandler(string operationId) { _operationId = operationId; }
        public List<(string RequestId, string OperationId)> Attempts { get; } = new();
        private int _count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts.Add((request.Headers.GetValues("X-Request-ID").Single(), request.Headers.GetValues("Idempotency-Key").Single()));
            if (_count++ == 0)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var json = "{\"operationId\":\"" + _operationId + "\",\"success\":true,\"code\":\"Borrowed\",\"message\":\"ok\",\"equipment\":null,\"loanId\":\"loan\",\"eventId\":\"event\"}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    private sealed class MemoryRequestLog : IClientRequestLog
    {
        public List<ClientDiagnosticEvent> Events { get; } = new();
        public void Record(ClientDiagnosticEvent item) => Events.Add(item);
        public IReadOnlyList<ClientDiagnosticEvent> GetRecent() => Events.ToArray();
    }
}
