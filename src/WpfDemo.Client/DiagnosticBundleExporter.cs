using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace WpfDemo
{
    public static class DiagnosticBundleExporter
    {
        private static readonly HashSet<string> ClientOperations = new HashSet<string>(StringComparer.Ordinal)
        {
            "equipment-list", "history", "borrow", "return", "diagnostics"
        };

        private static readonly string[] ServerRoutes =
        {
            "GET /api/diagnostics/recent",
            "GET /api/equipment/{id:int}",
            "GET /api/equipment/{id:int}/history",
            "GET /api/borrowers",
            "POST /api/equipment/{id:int}/borrow",
            "POST /api/equipment/{id:int}/return",
            "GET /api/equipment"
        };

        public static string Export(string outputDirectory, PendingOperation currentOperation,
            IReadOnlyList<ClientDiagnosticEvent> clientEvents, DiagnosticsResponse serverDiagnostics, double dpi)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory)) throw new ArgumentException("An output directory is required.", nameof(outputDirectory));
            Directory.CreateDirectory(outputDirectory);
            var capturedAt = DateTime.UtcNow;
            var path = Path.Combine(outputDirectory, "wpf-demo-diagnostics-" + capturedAt.ToString("yyyyMMdd-HHmmss-fff") + ".zip");
            var bundle = new DiagnosticBundleData
            {
                Environment = new DiagnosticEnvironmentData
                {
                    CapturedAtUtc = FormatTime(capturedAt),
                    AppVersion = typeof(DiagnosticBundleExporter).Assembly.GetName().Version?.ToString() ?? "unknown",
                    OperatingSystem = Environment.OSVersion.VersionString,
                    RuntimeVersion = Environment.Version.ToString(),
                    Is64BitProcess = Environment.Is64BitProcess,
                    Dpi = double.IsNaN(dpi) || double.IsInfinity(dpi) ? 96.0 : Math.Max(48.0, Math.Min(dpi, 768.0))
                },
                CurrentOperation = new DiagnosticOperationData
                {
                    OperationId = ValidGuid(currentOperation?.OperationId),
                    Kind = currentOperation?.Kind == "Borrow" || currentOperation?.Kind == "Return" ? currentOperation.Kind : ""
                },
                ClientRequests = (clientEvents ?? new ClientDiagnosticEvent[0]).Select(SanitizeClientEvent).ToList(),
                ServerRequests = new DiagnosticServerData
                {
                    IsAvailable = serverDiagnostics != null,
                    IsPartial = serverDiagnostics == null || serverDiagnostics.IsPartial,
                    ServerStartedAtUtc = SanitizeTimestamp(serverDiagnostics?.ServerStartedAtUtc),
                    Events = (serverDiagnostics?.Events ?? new List<RequestDiagnosticEvent>()).Select(SanitizeServerEvent).ToList()
                }
            };

            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
                {
                    WriteEntry(archive, "environment.json", bundle.Environment);
                    WriteEntry(archive, "current-operation.json", bundle.CurrentOperation);
                    WriteEntry(archive, "client-requests.json", bundle.ClientRequests);
                    WriteEntry(archive, "server-requests.json", bundle.ServerRequests);
                }
                File.Move(temporaryPath, path);
            }
            finally { File.Delete(temporaryPath); }
            return path;
        }

        private static DiagnosticClientEventData SanitizeClientEvent(ClientDiagnosticEvent item)
        {
            item = item ?? new ClientDiagnosticEvent();
            return new DiagnosticClientEventData
            {
                RequestId = ValidGuid(item.RequestId),
                OccurredAtUtc = SanitizeTimestamp(item.OccurredAtUtc),
                Operation = ClientOperations.Contains(item.Operation ?? "") ? item.Operation : "other",
                OperationId = ValidGuid(item.OperationId),
                StatusCode = item.StatusCode >= 100 && item.StatusCode <= 599 ? item.StatusCode : 0,
                ElapsedMilliseconds = Math.Max(0, Math.Min(item.ElapsedMilliseconds, 86400000)),
                Result = item.Result == "success" || item.Result == "confirmed-rejection" || item.Result == "unknown" || item.Result == "error"
                    ? item.Result : "error"
            };
        }

        private static DiagnosticServerEventData SanitizeServerEvent(RequestDiagnosticEvent item)
        {
            item = item ?? new RequestDiagnosticEvent();
            var method = item.Method == "GET" || item.Method == "POST" ? item.Method : "OTHER";
            var route = SanitizeRoute(item.Route);
            return new DiagnosticServerEventData
            {
                RequestId = ValidGuid(item.RequestId),
                OccurredAtUtc = SanitizeTimestamp(item.OccurredAtUtc),
                Method = method,
                Route = route,
                StatusCode = item.StatusCode >= 100 && item.StatusCode <= 599 ? item.StatusCode : 0,
                ElapsedMilliseconds = Math.Max(0, Math.Min(item.ElapsedMilliseconds, 86400000))
            };
        }

        private static string SanitizeRoute(string value)
        {
            var route = value ?? "";
            if (route.StartsWith("HTTP: ", StringComparison.Ordinal)) route = route.Substring("HTTP: ".Length);
            foreach (var allowed in ServerRoutes)
            {
                if (string.Equals(route, allowed, StringComparison.Ordinal) ||
                    route.StartsWith(allowed + "?", StringComparison.Ordinal) ||
                    route.StartsWith(allowed + " =>", StringComparison.Ordinal) ||
                    route.StartsWith(allowed + " ", StringComparison.Ordinal))
                    return allowed;
            }
            return "other";
        }

        private static string ValidGuid(string value)
        {
            return Guid.TryParse(value, out var id) ? id.ToString("D") : "";
        }

        private static string SanitizeTimestamp(string value)
        {
            return DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var timestamp) ? FormatTime(timestamp.ToUniversalTime()) : "";
        }

        private static string FormatTime(DateTime value)
        {
            return value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void WriteEntry<T>(ZipArchive archive, string name, T value)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using (var stream = entry.Open())
                new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
        }
    }

    [DataContract]
    public sealed class DiagnosticEnvironmentData
    {
        [DataMember(Name = "capturedAtUtc", Order = 1)] public string CapturedAtUtc { get; set; }
        [DataMember(Name = "appVersion", Order = 2)] public string AppVersion { get; set; }
        [DataMember(Name = "operatingSystem", Order = 3)] public string OperatingSystem { get; set; }
        [DataMember(Name = "runtimeVersion", Order = 4)] public string RuntimeVersion { get; set; }
        [DataMember(Name = "is64BitProcess", Order = 5)] public bool Is64BitProcess { get; set; }
        [DataMember(Name = "dpi", Order = 6)] public double Dpi { get; set; }
    }

    [DataContract]
    public sealed class DiagnosticOperationData
    {
        [DataMember(Name = "operationId", Order = 1)] public string OperationId { get; set; }
        [DataMember(Name = "kind", Order = 2)] public string Kind { get; set; }
    }

    [DataContract]
    public sealed class DiagnosticClientEventData
    {
        [DataMember(Name = "requestId", Order = 1)] public string RequestId { get; set; }
        [DataMember(Name = "occurredAtUtc", Order = 2)] public string OccurredAtUtc { get; set; }
        [DataMember(Name = "operation", Order = 3)] public string Operation { get; set; }
        [DataMember(Name = "operationId", Order = 4)] public string OperationId { get; set; }
        [DataMember(Name = "statusCode", Order = 5)] public int StatusCode { get; set; }
        [DataMember(Name = "elapsedMilliseconds", Order = 6)] public long ElapsedMilliseconds { get; set; }
        [DataMember(Name = "result", Order = 7)] public string Result { get; set; }
    }

    [DataContract]
    public sealed class DiagnosticServerData
    {
        [DataMember(Name = "isAvailable", Order = 1)] public bool IsAvailable { get; set; }
        [DataMember(Name = "isPartial", Order = 2)] public bool IsPartial { get; set; }
        [DataMember(Name = "serverStartedAtUtc", Order = 3)] public string ServerStartedAtUtc { get; set; }
        [DataMember(Name = "events", Order = 4)] public List<DiagnosticServerEventData> Events { get; set; }
    }

    [DataContract]
    public sealed class DiagnosticServerEventData
    {
        [DataMember(Name = "requestId", Order = 1)] public string RequestId { get; set; }
        [DataMember(Name = "occurredAtUtc", Order = 2)] public string OccurredAtUtc { get; set; }
        [DataMember(Name = "method", Order = 3)] public string Method { get; set; }
        [DataMember(Name = "route", Order = 4)] public string Route { get; set; }
        [DataMember(Name = "statusCode", Order = 5)] public int StatusCode { get; set; }
        [DataMember(Name = "elapsedMilliseconds", Order = 6)] public long ElapsedMilliseconds { get; set; }
    }

    [DataContract]
    public sealed class DiagnosticBundleData
    {
        [DataMember(Name = "environment", Order = 1)] public DiagnosticEnvironmentData Environment { get; set; }
        [DataMember(Name = "currentOperation", Order = 2)] public DiagnosticOperationData CurrentOperation { get; set; }
        [DataMember(Name = "clientRequests", Order = 3)] public List<DiagnosticClientEventData> ClientRequests { get; set; }
        [DataMember(Name = "serverRequests", Order = 4)] public DiagnosticServerData ServerRequests { get; set; }
    }
}
