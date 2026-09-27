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

    private sealed class FixedResponseHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public FixedResponseHandler(HttpStatusCode status, string body) { _status = status; _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body) });
    }

    private sealed class MemoryStore : IPendingOperationStore
    {
        public PendingOperation Current { get; private set; }
        public void Save(PendingOperation operation) => Current = operation;
        public void Delete(string operationId) => Current = null;
    }
}
