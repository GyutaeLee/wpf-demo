using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WpfDemo;

namespace WpfDemo.Api;

public sealed class LendingStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;
    private readonly string _datasetId;

    public LendingStore(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(Path.GetFullPath(directory), "wpf-demo.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        _datasetId = Initialize();
    }

    public EquipmentListResponse GetEquipment()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.Id, e.Code, e.Name, e.Category, e.Status, e.Version,
                   l.Id, l.BorrowerId, b.DisplayName, l.BorrowedAtUtc
            FROM Equipment e
            LEFT JOIN Loans l ON l.EquipmentId = e.Id AND l.ReturnedAtUtc IS NULL
            LEFT JOIN Borrowers b ON b.Id = l.BorrowerId
            ORDER BY e.Id;
            """;
        var items = new List<EquipmentItem>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var item = new EquipmentItem
                {
                    Id = reader.GetInt32(0), Code = reader.GetString(1), Name = reader.GetString(2),
                    Category = reader.GetString(3), Status = reader.GetString(4), Version = reader.GetInt64(5)
                };
                if (!reader.IsDBNull(6))
                    item.ActiveLoan = new ActiveLoan
                    {
                        Id = reader.GetString(6), BorrowerId = reader.GetString(7),
                        BorrowerName = reader.GetString(8), BorrowedAtUtc = reader.GetString(9)
                    };
                items.Add(item);
            }
        }
        return new EquipmentListResponse { DatasetId = _datasetId, Items = items, Borrowers = GetBorrowers(connection) };
    }

    public IReadOnlyList<LoanHistoryEntry> GetHistory(int equipmentId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, LoanId, OperationId, Kind, BorrowerName, OccurredAtUtc, Note
            FROM LoanHistory WHERE EquipmentId = $equipmentId ORDER BY OccurredAtUtc DESC, Id DESC;
            """;
        command.Parameters.AddWithValue("$equipmentId", equipmentId);
        var history = new List<LoanHistoryEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            history.Add(new LoanHistoryEntry
            {
                Id = reader.GetString(0), LoanId = reader.GetString(1),
                OperationId = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Kind = reader.GetString(3), BorrowerName = reader.GetString(4),
                OccurredAtUtc = reader.GetString(5), Note = reader.GetString(6)
            });
        return history;
    }

    public IReadOnlyList<Borrower> GetBorrowers()
    {
        using var connection = Open();
        return GetBorrowers(connection);
    }

    public OperationExecution Borrow(int equipmentId, BorrowCommand request, string operationId,
        string requestFingerprint, DateTime nowUtc)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var previous = FindOperation(connection, transaction, operationId);
        if (previous != null)
            return ReplayOrReject(previous, requestFingerprint);

        var result = ValidateCommon(equipmentId, request.OperationId, request.DatasetId,
            request.ExpectedVersion, operationId);
        EquipmentItem equipment = null;
        if (result == null)
        {
            equipment = GetEquipment(connection, transaction, equipmentId);
            if (equipment == null) result = Rejection(operationId, "EquipmentNotFound", "장비를 찾을 수 없습니다.");
            else if (equipment.Version != request.ExpectedVersion)
                result = Rejection(operationId, "VersionConflict", "장비 정보가 변경되었습니다.", equipment);
            else if (equipment.Status == EquipmentStates.Maintenance)
                result = Rejection(operationId, "Maintenance", "점검 중인 장비는 대여할 수 없습니다.", equipment);
            else if (equipment.Status == EquipmentStates.OnLoan)
                result = Rejection(operationId, "AlreadyOnLoan", "이미 대여 중인 장비입니다.", equipment);
            else if (!GetBorrowers(connection).Any(x => x.Id == request.BorrowerId))
                result = Rejection(operationId, "BorrowerNotFound", "대여자를 확인할 수 없습니다.", equipment);
        }

        if (result != null)
        {
            var statusCode = result.Code == "EquipmentNotFound" ? 404 : 409;
            SaveOperation(connection, transaction, operationId, requestFingerprint, statusCode, result);
            transaction.Commit();
            return new OperationExecution(statusCode, result, false);
        }

        var loanId = Guid.NewGuid().ToString("D");
        var borrower = GetBorrowers(connection).Single(x => x.Id == request.BorrowerId);
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE Equipment SET Status = $status, Version = Version + 1 WHERE Id = $id AND Version = $version AND Status = $available;";
            update.Parameters.AddWithValue("$status", EquipmentStates.OnLoan);
            update.Parameters.AddWithValue("$id", equipmentId);
            update.Parameters.AddWithValue("$version", request.ExpectedVersion);
            update.Parameters.AddWithValue("$available", EquipmentStates.Available);
            if (update.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("장비 상태가 트랜잭션 중 변경되었습니다.");
        }
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO Loans(Id, EquipmentId, BorrowerId, BorrowerName, BorrowedAtUtc, ReturnedAtUtc, Note) VALUES($id, $equipmentId, $borrowerId, $borrowerName, $at, NULL, $note);";
            insert.Parameters.AddWithValue("$id", loanId);
            insert.Parameters.AddWithValue("$equipmentId", equipmentId);
            insert.Parameters.AddWithValue("$borrowerId", borrower.Id);
            insert.Parameters.AddWithValue("$borrowerName", borrower.DisplayName);
            insert.Parameters.AddWithValue("$at", Timestamp(nowUtc));
            insert.Parameters.AddWithValue("$note", request.Note.Trim());
            insert.ExecuteNonQuery();
        }
        var current = GetEquipment(connection, transaction, equipmentId);
        var eventId = InsertHistory(connection, transaction, equipmentId, loanId, operationId,
            "Borrowed", borrower.DisplayName, nowUtc, request.Note.Trim());
        var success = new LoanOperationResponse
        {
            OperationId = operationId, Success = true, Code = "Borrowed",
            Message = "대여가 완료되었습니다.", Equipment = current, LoanId = loanId, EventId = eventId
        };
        SaveOperation(connection, transaction, operationId, requestFingerprint, 200, success);
        transaction.Commit();
        return new OperationExecution(200, success, false);
    }

    public OperationExecution Return(int equipmentId, ReturnCommand request, string operationId,
        string requestFingerprint, DateTime nowUtc)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var previous = FindOperation(connection, transaction, operationId);
        if (previous != null)
            return ReplayOrReject(previous, requestFingerprint);

        var result = ValidateCommon(equipmentId, request.OperationId, request.DatasetId,
            request.ExpectedVersion, operationId);
        EquipmentItem equipment = null;
        if (result == null)
        {
            equipment = GetEquipment(connection, transaction, equipmentId);
            if (equipment == null) result = Rejection(operationId, "EquipmentNotFound", "장비를 찾을 수 없습니다.");
            else if (equipment.Version != request.ExpectedVersion)
                result = Rejection(operationId, "VersionConflict", "장비 정보가 변경되었습니다.", equipment);
            else if (equipment.ActiveLoan == null)
                result = Rejection(operationId, "NotOnLoan", "반납할 대여가 없습니다.", equipment);
            else if (!string.Equals(equipment.ActiveLoan.Id, request.LoanId, StringComparison.Ordinal))
                result = Rejection(operationId, "LoanConflict", "반납 대상 대여가 변경되었습니다.", equipment);
        }

        if (result != null)
        {
            var statusCode = result.Code == "EquipmentNotFound" ? 404 : 409;
            SaveOperation(connection, transaction, operationId, requestFingerprint, statusCode, result);
            transaction.Commit();
            return new OperationExecution(statusCode, result, false);
        }

        using (var updateLoan = connection.CreateCommand())
        {
            updateLoan.Transaction = transaction;
            updateLoan.CommandText = "UPDATE Loans SET ReturnedAtUtc = $at, ReturnNote = $note WHERE Id = $loanId AND ReturnedAtUtc IS NULL;";
            updateLoan.Parameters.AddWithValue("$at", Timestamp(nowUtc));
            updateLoan.Parameters.AddWithValue("$note", request.Note.Trim());
            updateLoan.Parameters.AddWithValue("$loanId", request.LoanId);
            if (updateLoan.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("대여 상태가 트랜잭션 중 변경되었습니다.");
        }
        using (var updateEquipment = connection.CreateCommand())
        {
            updateEquipment.Transaction = transaction;
            updateEquipment.CommandText = "UPDATE Equipment SET Status = $status, Version = Version + 1 WHERE Id = $id AND Version = $version AND Status = $onLoan;";
            updateEquipment.Parameters.AddWithValue("$status", EquipmentStates.Available);
            updateEquipment.Parameters.AddWithValue("$id", equipmentId);
            updateEquipment.Parameters.AddWithValue("$version", request.ExpectedVersion);
            updateEquipment.Parameters.AddWithValue("$onLoan", EquipmentStates.OnLoan);
            if (updateEquipment.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("장비 상태가 트랜잭션 중 변경되었습니다.");
        }
        var eventId = InsertHistory(connection, transaction, equipmentId, request.LoanId, operationId,
            "Returned", equipment.ActiveLoan.BorrowerName, nowUtc, request.Note.Trim());
        var success = new LoanOperationResponse
        {
            OperationId = operationId, Success = true, Code = "Returned",
            Message = "반납이 완료되었습니다.", Equipment = GetEquipment(connection, transaction, equipmentId),
            LoanId = request.LoanId, EventId = eventId
        };
        SaveOperation(connection, transaction, operationId, requestFingerprint, 200, success);
        transaction.Commit();
        return new OperationExecution(200, success, false);
    }

    private OperationExecution ReplayOrReject(StoredOperation previous, string fingerprint)
    {
        if (!string.Equals(previous.Fingerprint, fingerprint, StringComparison.Ordinal))
            return new OperationExecution(409,
                Rejection(previous.OperationId, "IdempotencyKeyReused", "같은 작업 키를 다른 요청에 사용할 수 없습니다."), true);
        return new OperationExecution(previous.StatusCode,
            JsonSerializer.Deserialize<LoanOperationResponse>(previous.ResponseJson, JsonOptions)!, true);
    }

    private LoanOperationResponse ValidateCommon(int equipmentId, string bodyOperationId,
        string datasetId, long version, string headerOperationId)
    {
        if (!Guid.TryParse(bodyOperationId, out var bodyId) || bodyId.ToString("D") != headerOperationId ||
            !Guid.TryParse(datasetId, out _) || version < 1)
            return Rejection(headerOperationId, "InvalidCommand", "요청 정보가 올바르지 않습니다.");
        if (!string.Equals(datasetId, _datasetId, StringComparison.OrdinalIgnoreCase))
            return Rejection(headerOperationId, "DatasetMismatch", "시연 데이터가 초기화되었습니다. 최신 목록을 다시 불러와 주세요.");
        if (equipmentId < 1)
            return Rejection(headerOperationId, "EquipmentNotFound", "장비를 찾을 수 없습니다.");
        return null;
    }

    private static void SaveOperation(SqliteConnection connection, SqliteTransaction transaction,
        string operationId, string fingerprint, int statusCode, LoanOperationResponse response)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO Operations(Id, Fingerprint, StatusCode, ResponseJson) VALUES($id, $fingerprint, $status, $json);";
        command.Parameters.AddWithValue("$id", operationId);
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        command.Parameters.AddWithValue("$status", statusCode);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(response, JsonOptions));
        command.ExecuteNonQuery();
    }

    private static StoredOperation FindOperation(SqliteConnection connection, SqliteTransaction transaction, string operationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Fingerprint, StatusCode, ResponseJson FROM Operations WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", operationId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new StoredOperation(operationId, reader.GetString(0), reader.GetInt32(1), reader.GetString(2)) : null;
    }

    private static EquipmentItem GetEquipment(SqliteConnection connection, SqliteTransaction transaction, int equipmentId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT e.Id, e.Code, e.Name, e.Category, e.Status, e.Version,
                   l.Id, l.BorrowerId, b.DisplayName, l.BorrowedAtUtc
            FROM Equipment e LEFT JOIN Loans l ON l.EquipmentId = e.Id AND l.ReturnedAtUtc IS NULL
            LEFT JOIN Borrowers b ON b.Id = l.BorrowerId WHERE e.Id = $id;
            """;
        command.Parameters.AddWithValue("$id", equipmentId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var item = new EquipmentItem
        {
            Id = reader.GetInt32(0), Code = reader.GetString(1), Name = reader.GetString(2),
            Category = reader.GetString(3), Status = reader.GetString(4), Version = reader.GetInt64(5)
        };
        if (!reader.IsDBNull(6))
            item.ActiveLoan = new ActiveLoan
            {
                Id = reader.GetString(6), BorrowerId = reader.GetString(7),
                BorrowerName = reader.GetString(8), BorrowedAtUtc = reader.GetString(9)
            };
        return item;
    }

    private static List<Borrower> GetBorrowers(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, DisplayName FROM Borrowers ORDER BY Id;";
        var borrowers = new List<Borrower>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) borrowers.Add(new Borrower { Id = reader.GetString(0), DisplayName = reader.GetString(1) });
        return borrowers;
    }

    private static string InsertHistory(SqliteConnection connection, SqliteTransaction transaction, int equipmentId,
        string loanId, string operationId, string kind, string borrowerName, DateTime nowUtc, string note)
    {
        var eventId = Guid.NewGuid().ToString("D");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO LoanHistory(Id, EquipmentId, LoanId, OperationId, Kind, BorrowerName, OccurredAtUtc, Note) VALUES($id, $equipmentId, $loanId, $operationId, $kind, $borrower, $at, $note);";
        command.Parameters.AddWithValue("$id", eventId);
        command.Parameters.AddWithValue("$equipmentId", equipmentId);
        command.Parameters.AddWithValue("$loanId", loanId);
        command.Parameters.AddWithValue("$operationId", operationId);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$borrower", borrowerName);
        command.Parameters.AddWithValue("$at", Timestamp(nowUtc));
        command.Parameters.AddWithValue("$note", note);
        command.ExecuteNonQuery();
        return eventId;
    }

    private static LoanOperationResponse Rejection(string operationId, string code, string message,
        EquipmentItem equipment = null) => new()
        {
            OperationId = operationId, Success = false, Code = code, Message = message, Equipment = equipment
        };

    private static string Timestamp(DateTime value) => value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 4000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private string Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Metadata(Name TEXT PRIMARY KEY, Value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Borrowers(Id TEXT PRIMARY KEY, DisplayName TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Equipment(
                Id INTEGER PRIMARY KEY, Code TEXT NOT NULL UNIQUE, Name TEXT NOT NULL, Category TEXT NOT NULL,
                Status TEXT NOT NULL CHECK(Status IN ('사용 가능','대여 중','점검 중')), Version INTEGER NOT NULL CHECK(Version > 0));
            CREATE TABLE IF NOT EXISTS Loans(
                Id TEXT PRIMARY KEY, EquipmentId INTEGER NOT NULL REFERENCES Equipment(Id),
                BorrowerId TEXT NOT NULL REFERENCES Borrowers(Id), BorrowerName TEXT NOT NULL,
                BorrowedAtUtc TEXT NOT NULL, ReturnedAtUtc TEXT NULL, Note TEXT NOT NULL, ReturnNote TEXT NOT NULL DEFAULT '');
            CREATE UNIQUE INDEX IF NOT EXISTS UX_Loans_OneActivePerEquipment ON Loans(EquipmentId) WHERE ReturnedAtUtc IS NULL;
            CREATE TABLE IF NOT EXISTS LoanHistory(
                Id TEXT PRIMARY KEY, EquipmentId INTEGER NOT NULL REFERENCES Equipment(Id), LoanId TEXT NOT NULL REFERENCES Loans(Id),
                OperationId TEXT NULL UNIQUE, Kind TEXT NOT NULL, BorrowerName TEXT NOT NULL,
                OccurredAtUtc TEXT NOT NULL, Note TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Operations(
                Id TEXT PRIMARY KEY, Fingerprint TEXT NOT NULL, StatusCode INTEGER NOT NULL, ResponseJson TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();

        var datasetId = ReadMetadata(connection, "DatasetId");
        if (datasetId != null) return datasetId;

        using var transaction = connection.BeginTransaction();
        datasetId = Guid.NewGuid().ToString("D");
        InsertMetadata(connection, transaction, "DatasetId", datasetId);
        InsertMetadata(connection, transaction, "SchemaVersion", "1");
        using (var borrowers = connection.CreateCommand())
        {
            borrowers.Transaction = transaction;
            borrowers.CommandText = "INSERT INTO Borrowers(Id, DisplayName) VALUES('A','가상 대여자 A'),('B','가상 대여자 B');";
            borrowers.ExecuteNonQuery();
        }
        using (var equipment = connection.CreateCommand())
        {
            equipment.Transaction = transaction;
            equipment.CommandText = """
                INSERT INTO Equipment(Id, Code, Name, Category, Status, Version) VALUES
                (1001,'EQ-1001','노트북 A','컴퓨터','사용 가능',1),
                (1002,'EQ-1002','노트북 B','컴퓨터','사용 가능',1),
                (1003,'EQ-1003','모니터 A','화면 장비','사용 가능',1),
                (1004,'EQ-1004','테스트 장치 A','테스트 장비','대여 중',1),
                (1005,'EQ-1005','모니터 B','화면 장비','점검 중',1);
                INSERT INTO Loans(Id, EquipmentId, BorrowerId, BorrowerName, BorrowedAtUtc, Note)
                VALUES('seed-loan-1004',1004,'B','가상 대여자 B',$at,'초기 시연 데이터');
                INSERT INTO LoanHistory(Id, EquipmentId, LoanId, OperationId, Kind, BorrowerName, OccurredAtUtc, Note)
                VALUES('seed-event-1004',1004,'seed-loan-1004',NULL,'Borrowed','가상 대여자 B',$at,'초기 시연 데이터');
                """;
            equipment.Parameters.AddWithValue("$at", Timestamp(DateTime.UtcNow.AddHours(-1)));
            equipment.ExecuteNonQuery();
        }
        transaction.Commit();
        return datasetId;
    }

    private static string ReadMetadata(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Metadata WHERE Name = $name;";
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() as string;
    }

    private static void InsertMetadata(SqliteConnection connection, SqliteTransaction transaction, string name, string value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO Metadata(Name, Value) VALUES($name, $value);";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    public static string RequestFingerprint(string kind, int equipmentId, string datasetId,
        long version, string borrowerId, string loanId, string note)
    {
        var value = string.Join("\n", kind, equipmentId, datasetId, version, borrowerId ?? "", loanId ?? "", note ?? "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private sealed record StoredOperation(string OperationId, string Fingerprint, int StatusCode, string ResponseJson);
}

public sealed record OperationExecution(int StatusCode, LoanOperationResponse Response, bool Replayed);
