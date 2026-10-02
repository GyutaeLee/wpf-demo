using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WpfDemo
{
    public sealed class HttpLendingApi : ILendingApi
    {
        private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private readonly HttpClient _client;
        private readonly string _apiAddress;
        private readonly IClientRequestLog _requestLog;

        public HttpLendingApi(string apiAddress) : this(apiAddress, Client, null) { }

        public HttpLendingApi(string apiAddress, IClientRequestLog requestLog) : this(apiAddress, Client, requestLog) { }

        internal HttpLendingApi(string apiAddress, HttpClient client) : this(apiAddress, client, null) { }

        internal HttpLendingApi(string apiAddress, HttpClient client, IClientRequestLog requestLog)
        {
            _apiAddress = (apiAddress ?? throw new ArgumentNullException(nameof(apiAddress))).TrimEnd('/');
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _requestLog = requestLog;
        }

        public Task<EquipmentListResponse> GetEquipmentAsync()
        {
            return GetEquipmentAsync("", EquipmentStates.All, 1, 50, CancellationToken.None);
        }

        public async Task<IReadOnlyList<LoanHistoryEntry>> GetHistoryAsync(int equipmentId)
        {
            var response = await GetHistoryAsync(equipmentId, 1, 50, CancellationToken.None);
            return response.Items;
        }

        public Task<EquipmentListResponse> GetEquipmentAsync(string query, string status, int page, int pageSize,
            CancellationToken cancellationToken)
        {
            var address = _apiAddress + "/api/equipment?q=" + Uri.EscapeDataString(query ?? "") +
                "&status=" + Uri.EscapeDataString(status ?? EquipmentStates.All) +
                "&page=" + page + "&pageSize=" + pageSize;
            return GetAsync<EquipmentListResponse>(address, cancellationToken, "equipment-list");
        }

        public Task<LoanHistoryListResponse> GetHistoryAsync(int equipmentId, int page, int pageSize,
            CancellationToken cancellationToken)
        {
            var address = _apiAddress + "/api/equipment/" + equipmentId + "/history?page=" + page + "&pageSize=" + pageSize;
            return GetAsync<LoanHistoryListResponse>(address, cancellationToken, "history");
        }

        public Task<DiagnosticsResponse> GetDiagnosticsAsync(CancellationToken cancellationToken)
        {
            return GetAsync<DiagnosticsResponse>(_apiAddress + "/api/diagnostics/recent", cancellationToken, "diagnostics");
        }

        public async Task<LoanOperationResponse> SendOperationAsync(PendingOperation operation)
        {
            var uri = new Uri(new Uri(operation.ApiAddress.TrimEnd('/') + "/"), operation.RelativePath.TrimStart('/'));
            var requestId = Guid.NewGuid().ToString("D");
            var stopwatch = Stopwatch.StartNew();
            var statusCode = 0;
            var result = "unknown";
            using (var request = new HttpRequestMessage(HttpMethod.Post, uri))
            {
                request.Headers.Add("Idempotency-Key", operation.OperationId);
                request.Headers.Add("X-Request-ID", requestId);
                request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(operation.BodyJson));
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                try
                {
                    using (var response = await _client.SendAsync(request))
                    {
                        statusCode = (int)response.StatusCode;
                        var body = await response.Content.ReadAsStringAsync();
                        if (response.IsSuccessStatusCode)
                        {
                            var success = Deserialize<LoanOperationResponse>(body);
                            result = "success";
                            return success;
                        }
                        if (statusCode == 400 || statusCode == 404 || statusCode == 409)
                        {
                            var error = Deserialize<ApiErrorResponse>(body);
                            if (error == null || !IsConfirmedError(statusCode, error.Code))
                                throw new InvalidOperationException("API 오류 응답을 확인할 수 없습니다.");
                            throw new ApiResponseException(statusCode, error.Code, error.Message ?? "요청이 거부되었습니다.", true);
                        }
                        throw new ApiResponseException(statusCode, "Unconfirmed", "요청 결과가 확정되지 않았습니다.", false);
                    }
                }
                catch (ApiResponseException exception)
                {
                    result = exception.IsConfirmed ? "confirmed-rejection" : "unknown";
                    throw;
                }
                catch (Exception)
                {
                    result = "unknown";
                    throw;
                }
                finally
                {
                    stopwatch.Stop();
                    RecordRequest(requestId, operation.Kind.ToLowerInvariant(), operation.OperationId, statusCode,
                        stopwatch.ElapsedMilliseconds, result);
                }
            }
        }

        private static bool IsConfirmedError(int status, string code)
        {
            if (status == 400) return code == "InvalidCommand" || code == "InvalidNote";
            if (status == 404) return code == "EquipmentNotFound";
            if (status != 409) return false;
            return code == "VersionConflict" || code == "Maintenance" || code == "AlreadyOnLoan" ||
                code == "BorrowerNotFound" || code == "NotOnLoan" || code == "LoanConflict" ||
                code == "DatasetMismatch" || code == "IdempotencyKeyReused";
        }

        private async Task<T> GetAsync<T>(string address, CancellationToken cancellationToken, string operation)
        {
            var requestId = Guid.NewGuid().ToString("D");
            var stopwatch = Stopwatch.StartNew();
            var statusCode = 0;
            var result = "error";
            using (var request = new HttpRequestMessage(HttpMethod.Get, address))
            {
                request.Headers.Add("X-Request-ID", requestId);
                try
                {
                    using (var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken))
                    {
                        statusCode = (int)response.StatusCode;
                        var body = await response.Content.ReadAsStringAsync();
                        if (!response.IsSuccessStatusCode)
                            throw new ApiResponseException(statusCode, "ReadFailed", "API 조회에 실패했습니다.", false);
                        var value = Deserialize<T>(body);
                        result = "success";
                        return value;
                    }
                }
                catch (Exception)
                {
                    result = "error";
                    throw;
                }
                finally
                {
                    stopwatch.Stop();
                    RecordRequest(requestId, operation, "", statusCode, stopwatch.ElapsedMilliseconds, result);
                }
            }
        }

        private void RecordRequest(string requestId, string operation, string operationId, int statusCode,
            long elapsedMilliseconds, string result)
        {
            if (_requestLog == null) return;
            try
            {
                _requestLog.Record(new ClientDiagnosticEvent
                {
                    RequestId = requestId,
                    OccurredAtUtc = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    Operation = operation,
                    OperationId = operationId ?? "",
                    StatusCode = statusCode,
                    ElapsedMilliseconds = elapsedMilliseconds,
                    Result = result
                });
            }
            catch (Exception) { }
        }

        private static T Deserialize<T>(string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json ?? "");
            using (var stream = new MemoryStream(bytes))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }
    }
}
