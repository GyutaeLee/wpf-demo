using System;
using System.Threading.Tasks;

namespace WpfDemo
{
    public enum PendingOperationState
    {
        Ready, Saving, Sending, Unconfirmed, CleanupRequired, ConfirmedSuccess, ConfirmedRejection, RecoveryError
    }

    // Owns request persistence and confirmation; screen selection and messages stay in the ViewModel.
    public sealed class PendingOperationWorkflow
    {
        private readonly ILendingApi _api;
        private readonly IPendingOperationStore _store;

        public PendingOperationWorkflow(ILendingApi api, IPendingOperationStore store)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public PendingOperation Pending { get; private set; }
        public PendingOperationState State { get; private set; }

        public void Restore()
        {
            try
            {
                Pending = _store.Current;
                State = Pending == null ? PendingOperationState.Ready : PendingOperationState.Unconfirmed;
            }
            catch
            {
                State = PendingOperationState.RecoveryError;
                throw;
            }
        }

        public async Task PersistAsync(PendingOperation operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            if (Pending != null || State == PendingOperationState.Saving || State == PendingOperationState.RecoveryError)
                throw new InvalidOperationException("먼저 보관된 요청의 결과를 확인해 주세요.");
            State = PendingOperationState.Saving;
            try
            {
                await Task.Run(() => _store.Save(operation));
                Pending = operation;
                State = PendingOperationState.Unconfirmed;
            }
            catch
            {
                State = PendingOperationState.Ready;
                throw;
            }
        }

        public async Task<OperationAttempt> SendAsync()
        {
            if (Pending == null || State == PendingOperationState.Sending)
                throw new InvalidOperationException("전송할 수 있는 보관 요청이 없습니다.");
            var operation = Pending;
            State = PendingOperationState.Sending;
            ApiResponseException rejection = null;
            try
            {
                var response = await _api.SendOperationAsync(operation);
                if (response == null || response.OperationId != operation.OperationId || !response.Success ||
                    response.Code != (operation.Kind == "Borrow" ? "Borrowed" : "Returned"))
                    throw new InvalidOperationException("서버 응답을 요청 결과로 확인할 수 없습니다.");
            }
            catch (ApiResponseException exception) when (exception.IsConfirmed)
            {
                rejection = exception;
            }
            catch (Exception)
            {
                State = PendingOperationState.Unconfirmed;
                return new OperationAttempt(operation, null, false, false);
            }

            try
            {
                await Task.Run(() => _store.Delete(operation.OperationId));
                Pending = null;
            }
            catch (Exception)
            {
                State = PendingOperationState.CleanupRequired;
                return new OperationAttempt(operation, rejection, true, true);
            }
            State = rejection == null ? PendingOperationState.ConfirmedSuccess : PendingOperationState.ConfirmedRejection;
            return new OperationAttempt(operation, rejection, true, false);
        }
    }

    public sealed class OperationAttempt
    {
        internal OperationAttempt(PendingOperation operation, ApiResponseException rejection,
            bool isConfirmed, bool cleanupFailed)
        {
            Operation = operation;
            Rejection = rejection;
            IsConfirmed = isConfirmed;
            CleanupFailed = cleanupFailed;
        }

        public PendingOperation Operation { get; private set; }
        public ApiResponseException Rejection { get; private set; }
        public bool IsConfirmed { get; private set; }
        public bool CleanupFailed { get; private set; }
    }
}
