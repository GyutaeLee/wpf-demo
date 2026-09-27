using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace WpfDemo
{
    public sealed class PendingOperationStore : IPendingOperationStore, IDisposable
    {
        private readonly string _directory;
        private readonly string _operationPath;
        private readonly FileStream _lock;

        public PendingOperationStore(string directory)
        {
            _directory = directory;
            Directory.CreateDirectory(_directory);
            _lock = new FileStream(Path.Combine(_directory, "client.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            _operationPath = Path.Combine(_directory, "pending-operation.json");
        }

        public PendingOperation Current
        {
            get
            {
                if (!File.Exists(_operationPath)) return null;
                using (var stream = File.OpenRead(_operationPath))
                {
                    var operation = (PendingOperation)new DataContractJsonSerializer(typeof(PendingOperation)).ReadObject(stream);
                    Validate(operation);
                    return operation;
                }
            }
        }

        public void Save(PendingOperation operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            if (File.Exists(_operationPath))
                throw new InvalidOperationException("미확정 요청이 이미 있습니다. 먼저 결과를 확인해 주세요.");

            var temporaryPath = _operationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    new DataContractJsonSerializer(typeof(PendingOperation)).WriteObject(stream, operation);
                    stream.Flush(true);
                }
                File.Move(temporaryPath, _operationPath);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        public void Delete(string operationId)
        {
            var current = Current;
            if (current == null) return;
            if (!string.Equals(current.OperationId, operationId, StringComparison.Ordinal))
                throw new InvalidOperationException("확인 중인 요청과 삭제하려는 요청이 다릅니다.");
            File.Delete(_operationPath);
        }

        public static PendingOperation CreateBorrow(string apiAddress, string datasetId, int equipmentId,
            long expectedVersion, string borrowerId, string note)
        {
            var operationId = Guid.NewGuid().ToString("D");
            var command = new BorrowCommand
            {
                OperationId = operationId,
                DatasetId = datasetId,
                ExpectedVersion = expectedVersion,
                BorrowerId = borrowerId,
                Note = note
            };
            return Create(operationId, "Borrow", apiAddress, datasetId, equipmentId,
                "/api/equipment/" + equipmentId + "/borrow", command);
        }

        public static PendingOperation CreateReturn(string apiAddress, string datasetId, EquipmentItem equipment,
            string note)
        {
            var operationId = Guid.NewGuid().ToString("D");
            var command = new ReturnCommand
            {
                OperationId = operationId,
                DatasetId = datasetId,
                ExpectedVersion = equipment.Version,
                LoanId = equipment.ActiveLoan.Id,
                Note = note
            };
            return Create(operationId, "Return", apiAddress, datasetId, equipment.Id,
                "/api/equipment/" + equipment.Id + "/return", command);
        }

        private static PendingOperation Create<T>(string operationId, string kind, string apiAddress,
            string datasetId, int equipmentId, string relativePath, T command)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(stream, command);
                return new PendingOperation
                {
                    OperationId = operationId,
                    Kind = kind,
                    EquipmentId = equipmentId,
                    DatasetId = datasetId,
                    ApiAddress = apiAddress,
                    RelativePath = relativePath,
                    BodyJson = Encoding.UTF8.GetString(stream.ToArray())
                };
            }
        }

        private static void Validate(PendingOperation operation)
        {
            if (operation == null || !Guid.TryParse(operation.OperationId, out var operationId) ||
                operationId.ToString("D") != operation.OperationId ||
                !Guid.TryParse(operation.DatasetId, out _) || operation.EquipmentId < 1 ||
                string.IsNullOrWhiteSpace(operation.BodyJson) || operation.BodyJson.Length > 8192)
                throw new InvalidDataException("보관된 요청 정보가 올바르지 않습니다.");

            if (!Uri.TryCreate(operation.ApiAddress, UriKind.Absolute, out var apiAddress) ||
                apiAddress.Scheme != Uri.UriSchemeHttp || !apiAddress.IsLoopback ||
                apiAddress.UserInfo.Length != 0 || apiAddress.Query.Length != 0 ||
                apiAddress.Fragment.Length != 0 || apiAddress.AbsolutePath != "/")
                throw new InvalidDataException("보관된 요청의 로컬 API 주소가 올바르지 않습니다.");

            if (operation.Kind == "Borrow")
            {
                if (operation.RelativePath != "/api/equipment/" + operation.EquipmentId + "/borrow")
                    throw new InvalidDataException("보관된 대여 요청의 주소가 올바르지 않습니다.");
                var command = ReadBody<BorrowCommand>(operation.BodyJson);
                if (command.OperationId != operation.OperationId || command.DatasetId != operation.DatasetId ||
                    command.ExpectedVersion < 1 || string.IsNullOrWhiteSpace(command.BorrowerId) || !ValidNote(command.Note))
                    throw new InvalidDataException("보관된 대여 요청 내용이 올바르지 않습니다.");
            }
            else if (operation.Kind == "Return")
            {
                if (operation.RelativePath != "/api/equipment/" + operation.EquipmentId + "/return")
                    throw new InvalidDataException("보관된 반납 요청의 주소가 올바르지 않습니다.");
                var command = ReadBody<ReturnCommand>(operation.BodyJson);
                if (command.OperationId != operation.OperationId || command.DatasetId != operation.DatasetId ||
                    command.ExpectedVersion < 1 || string.IsNullOrWhiteSpace(command.LoanId) || !ValidNote(command.Note))
                    throw new InvalidDataException("보관된 반납 요청 내용이 올바르지 않습니다.");
            }
            else
            {
                throw new InvalidDataException("보관된 요청 종류가 올바르지 않습니다.");
            }
        }

        private static T ReadBody<T>(string body)
        {
            try
            {
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(body)))
                    return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
            }
            catch (Exception exception) when (exception is System.Runtime.Serialization.SerializationException ||
                exception is ArgumentException || exception is FormatException)
            {
                throw new InvalidDataException("보관된 요청 내용을 읽을 수 없습니다.", exception);
            }
        }

        private static bool ValidNote(string note) => note == null ||
            (note.Length <= 200 && (note.Length == 0 || !string.IsNullOrWhiteSpace(note)));

        public void Dispose()
        {
            _lock.Dispose();
        }
    }
}
