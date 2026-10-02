using WpfDemo;

namespace WpfDemo.Api;

public sealed class RecentApiDiagnostics
{
    private const int Capacity = 100;
    private readonly object _sync = new();
    private readonly Queue<RequestDiagnosticEvent> _events = new();
    private readonly string _serverStartedAtUtc = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    public void Record(RequestDiagnosticEvent item)
    {
        lock (_sync)
        {
            _events.Enqueue(item);
            while (_events.Count > Capacity) _events.Dequeue();
        }
    }

    public DiagnosticsResponse GetRecent()
    {
        lock (_sync)
        {
            return new DiagnosticsResponse
            {
                ServerStartedAtUtc = _serverStartedAtUtc,
                IsPartial = true,
                Events = _events.Select(item => new RequestDiagnosticEvent
                {
                    RequestId = item.RequestId,
                    OccurredAtUtc = item.OccurredAtUtc,
                    Method = item.Method,
                    Route = item.Route,
                    StatusCode = item.StatusCode,
                    ElapsedMilliseconds = item.ElapsedMilliseconds
                }).ToList()
            };
        }
    }
}
