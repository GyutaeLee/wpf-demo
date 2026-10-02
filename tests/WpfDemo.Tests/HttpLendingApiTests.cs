using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfDemo;

namespace WpfDemo.Tests;

[TestClass]
public sealed class HttpLendingApiTests
{
    [DataTestMethod]
    [DataRow(400)]
    [DataRow(404)]
    [DataRow(409)]
    public async Task UnknownErrorCodeDoesNotConfirmOrDiscardPendingOperation(int status)
    {
        using var client = new HttpClient(new FixedResponseHandler((HttpStatusCode)status,
            "{\"code\":\"UnexpectedFailure\",\"message\":\"No definite result\"}"));
        var api = new HttpLendingApi("http://127.0.0.1:5187", client);
        var operation = PendingOperationStore.CreateBorrow("http://127.0.0.1:5187",
            Guid.NewGuid().ToString("D"), 1001, 1, "A", "retained input");
        var store = new MemoryStore();
        store.Save(operation);
        var vm = new LendingViewModel(api, store, "http://127.0.0.1:5187", "A");

        await vm.InitializeAsync();

        Assert.IsTrue(vm.HasUnknownResult);
        Assert.IsTrue(vm.HasPendingOperation);
        Assert.AreEqual(operation.OperationId, store.Current.OperationId);
        Assert.AreEqual(operation.BodyJson, store.Current.BodyJson);
        Assert.IsFalse(vm.CanBorrow);
    }

    [TestMethod]
    public async Task EquipmentReadEncodesSearchAndPassesCancellationToHttpClient()
    {
        using var handler = new BlockingGetHandler();
        using var client = new HttpClient(handler);
        var api = new HttpLendingApi("http://127.0.0.1:5187", client);
        using var cancellation = new CancellationTokenSource();
        var request = api.GetEquipmentAsync("A&B +", EquipmentStates.Available, 2, 50, cancellation.Token);

        var sent = await handler.Request.Task.WaitAsync(TimeSpan.FromSeconds(2));
        StringAssert.Contains(sent.RequestUri.Query, "q=A%26B%20%2B");
        StringAssert.Contains(sent.RequestUri.Query, "status=%EC%82%AC%EC%9A%A9%20%EA%B0%80%EB%8A%A5");
        StringAssert.Contains(sent.RequestUri.Query, "page=2&pageSize=50");
        cancellation.Cancel();
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => request);
    }

    private sealed class FixedResponseHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public FixedResponseHandler(HttpStatusCode status, string body) { _status = status; _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body) });
    }

    private sealed class BlockingGetHandler : HttpMessageHandler
    {
        public TaskCompletionSource<HttpRequestMessage> Request { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request.TrySetResult(request);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("A canceled read unexpectedly completed.");
        }
    }

    private sealed class MemoryStore : IPendingOperationStore
    {
        public PendingOperation Current { get; private set; }
        public void Save(PendingOperation operation) => Current = operation;
        public void Delete(string operationId) => Current = null;
    }
}
