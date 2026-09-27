using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;

namespace WpfDemo
{
    public sealed class HttpLendingApi : ILendingApi
    {
        private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private readonly HttpClient _client;
        private readonly string _apiAddress;

        public HttpLendingApi(string apiAddress) : this(apiAddress, Client) { }

        internal HttpLendingApi(string apiAddress, HttpClient client)
        {
            _apiAddress = (apiAddress ?? throw new ArgumentNullException(nameof(apiAddress))).TrimEnd('/');
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public async Task<EquipmentListResponse> GetEquipmentAsync()
        {
            return await GetAsync<EquipmentListResponse>(_apiAddress + "/api/equipment");
        }

        public async Task<IReadOnlyList<LoanHistoryEntry>> GetHistoryAsync(int equipmentId)
        {
            return await GetAsync<List<LoanHistoryEntry>>(_apiAddress + "/api/equipment/" + equipmentId + "/history");
        }

        public async Task<LoanOperationResponse> SendOperationAsync(PendingOperation operation)
        {
            var uri = new Uri(new Uri(operation.ApiAddress.TrimEnd('/') + "/"), operation.RelativePath.TrimStart('/'));
            using (var request = new HttpRequestMessage(HttpMethod.Post, uri))
            {
                request.Headers.Add("Idempotency-Key", operation.OperationId);
                request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(operation.BodyJson));
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                using (var response = await _client.SendAsync(request))
                {
                    var body = await response.Content.ReadAsStringAsync();
                    if (response.IsSuccessStatusCode) return Deserialize<LoanOperationResponse>(body);
                    var status = (int)response.StatusCode;
                    if (status == 400 || status == 404 || status == 409)
                    {
                        var error = Deserialize<ApiErrorResponse>(body);
                        if (error == null || !IsConfirmedError(status, error.Code))
                            throw new InvalidOperationException("API 오류 응답을 확인할 수 없습니다.");
                        throw new ApiResponseException(status, error.Code, error.Message ?? "요청이 거부되었습니다.", true);
                    }
                    throw new ApiResponseException(status, "Unconfirmed", "요청 결과가 확정되지 않았습니다.", false);
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

        private async Task<T> GetAsync<T>(string address)
        {
            using (var response = await _client.GetAsync(address))
            {
                var body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                    throw new ApiResponseException((int)response.StatusCode, "ReadFailed", "API 조회에 실패했습니다.", false);
                return Deserialize<T>(body);
            }
        }

        private static T Deserialize<T>(string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json ?? "");
            using (var stream = new MemoryStream(bytes))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }
    }
}
