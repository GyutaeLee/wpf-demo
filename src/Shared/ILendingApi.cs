using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WpfDemo
{
    public interface ILendingApi
    {
        Task<EquipmentListResponse> GetEquipmentAsync();
        Task<IReadOnlyList<LoanHistoryEntry>> GetHistoryAsync(int equipmentId);
        Task<EquipmentListResponse> GetEquipmentAsync(string query, string status, int page, int pageSize, CancellationToken cancellationToken);
        Task<LoanHistoryListResponse> GetHistoryAsync(int equipmentId, int page, int pageSize, CancellationToken cancellationToken);
        Task<DiagnosticsResponse> GetDiagnosticsAsync(CancellationToken cancellationToken);
        Task<LoanOperationResponse> SendOperationAsync(PendingOperation operation);
    }

    public interface IPendingOperationStore
    {
        PendingOperation Current { get; }
        void Save(PendingOperation operation);
        void Delete(string operationId);
    }

    public sealed class CachedEquipmentSnapshot
    {
        public EquipmentListResponse Response { get; set; }
        public int CachedItemCount { get; set; }
        public string LastUpdatedUtc { get; set; }
        public bool HasSnapshot { get; set; }
    }

    public sealed class CachedHistorySnapshot
    {
        public LoanHistoryListResponse Response { get; set; }
        public int CachedItemCount { get; set; }
        public int ServerTotalCountAtLastFetch { get; set; }
        public string LastUpdatedUtc { get; set; }
        public bool HasSnapshot { get; set; }
    }

    public interface IReadCacheStore
    {
        void SaveEquipmentPage(string apiAddress, EquipmentListResponse response);
        CachedEquipmentSnapshot GetEquipmentPage(string apiAddress, string query, string status, int page, int pageSize);
        void SaveHistoryPage(string apiAddress, string datasetId, int equipmentId, LoanHistoryListResponse response);
        CachedHistorySnapshot GetHistoryPage(string apiAddress, string datasetId, int equipmentId, int page, int pageSize);
    }

    public interface IClientRequestLog
    {
        void Record(ClientDiagnosticEvent item);
        IReadOnlyList<ClientDiagnosticEvent> GetRecent();
    }
}
