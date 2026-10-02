using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace WpfDemo
{
    public sealed class ClientRequestLog : IClientRequestLog
    {
        private const long MaxLogBytes = 1024 * 1024;
        private readonly object _sync = new object();
        private readonly string _activePath;
        private readonly string _previousPath;

        public ClientRequestLog(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("A log directory is required.", nameof(directory));
            Directory.CreateDirectory(directory);
            _activePath = Path.Combine(directory, "client-requests.jsonl");
            _previousPath = _activePath + ".1";
        }

        public void Record(ClientDiagnosticEvent item)
        {
            try
            {
                var safeItem = Sanitize(item);
                var line = Serialize(safeItem) + Environment.NewLine;
                var bytes = Encoding.UTF8.GetBytes(line);
                lock (_sync)
                {
                    if (File.Exists(_activePath) && new FileInfo(_activePath).Length + bytes.Length > MaxLogBytes)
                    {
                        if (File.Exists(_previousPath)) File.Delete(_previousPath);
                        File.Move(_activePath, _previousPath);
                    }
                    using (var stream = new FileStream(_activePath, FileMode.Append, FileAccess.Write, FileShare.Read))
                    {
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush();
                    }
                }
            }
            catch (Exception) { }
        }

        public IReadOnlyList<ClientDiagnosticEvent> GetRecent()
        {
            lock (_sync)
            {
                var events = new List<ClientDiagnosticEvent>();
                foreach (var path in new[] { _previousPath, _activePath })
                {
                    if (!File.Exists(path)) continue;
                    try
                    {
                        foreach (var line in File.ReadLines(path))
                        {
                            try { events.Add(Sanitize(Deserialize(line))); }
                            catch (Exception) { }
                        }
                    }
                    catch (Exception) { }
                }
                return events;
            }
        }

        private static ClientDiagnosticEvent Sanitize(ClientDiagnosticEvent item)
        {
            item = item ?? new ClientDiagnosticEvent();
            var operation = item.Operation;
            if (operation != "equipment-list" && operation != "history" && operation != "borrow" &&
                operation != "return" && operation != "diagnostics") operation = "other";
            var result = item.Result;
            if (result != "success" && result != "confirmed-rejection" && result != "unknown" && result != "error") result = "error";
            var occurredAtUtc = DateTime.TryParse(item.OccurredAtUtc, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var occurredAt)
                ? occurredAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                : DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            return new ClientDiagnosticEvent
            {
                RequestId = Guid.TryParse(item.RequestId, out var requestId) ? requestId.ToString("D") : "",
                OccurredAtUtc = occurredAtUtc,
                Operation = operation,
                OperationId = Guid.TryParse(item.OperationId, out var operationId) ? operationId.ToString("D") : "",
                StatusCode = item.StatusCode >= 100 && item.StatusCode <= 599 ? item.StatusCode : 0,
                ElapsedMilliseconds = Math.Max(0, Math.Min(item.ElapsedMilliseconds, 86400000)),
                Result = result
            };
        }

        private static string Serialize(ClientDiagnosticEvent item)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(ClientDiagnosticEvent)).WriteObject(stream, item);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private static ClientDiagnosticEvent Deserialize(string json)
        {
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json ?? "")))
                return (ClientDiagnosticEvent)new DataContractJsonSerializer(typeof(ClientDiagnosticEvent)).ReadObject(stream);
        }
    }
}
