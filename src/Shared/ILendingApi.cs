using System.Collections.Generic;
using System.Threading.Tasks;

namespace WpfDemo
{
    public interface ILendingApi
    {
        Task<EquipmentListResponse> GetEquipmentAsync();
        Task<IReadOnlyList<LoanHistoryEntry>> GetHistoryAsync(int equipmentId);
        Task<LoanOperationResponse> SendOperationAsync(PendingOperation operation);
    }

    public interface IPendingOperationStore
    {
        PendingOperation Current { get; }
        void Save(PendingOperation operation);
        void Delete(string operationId);
    }
}
