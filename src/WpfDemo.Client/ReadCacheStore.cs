using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using Microsoft.Data.Sqlite;

namespace WpfDemo
{
    public sealed class ReadCacheStore : IReadCacheStore, IDisposable
    {
        private const int MaxEquipmentRows = 1000;
        private const int MaxHistoryRowsPerEquipment = 100;
        private readonly object _sync = new object();
        private readonly string _databasePath;
        private readonly string _connectionString;
        private bool _disposed;

        public ReadCacheStore(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A cache path is required.", nameof(path));
            _databasePath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath));
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false, // Release the cache file handle when each connection closes.
                DefaultTimeout = 5
            }.ToString();

            try { Initialize(); }
            catch (SqliteException exception) when (IsCorrupt(exception))
            {
                Quarantine();
                Initialize();
            }
        }

        public void SaveEquipmentPage(string apiAddress, EquipmentListResponse response)
        {
            if (response == null || string.IsNullOrWhiteSpace(response.DatasetId) || response.Items == null)
                throw new ArgumentException("A valid equipment response is required.", nameof(response));
            lock (_sync)
            {
                ThrowIfDisposed();
                using (var connection = Open())
                using (var transaction = connection.BeginTransaction())
                {
                    var existingDatasetId = GetDatasetId(connection, transaction, apiAddress);
                    if (existingDatasetId != null && existingDatasetId != response.DatasetId)
                        DeleteAddressRows(connection, transaction, apiAddress);

                    var now = UtcNow();
                    using (var metadata = connection.CreateCommand())
                    {
                        metadata.Transaction = transaction;
                        metadata.CommandText = @"
                            INSERT INTO CacheMetadata(ApiAddress, DatasetId, BorrowersJson, EquipmentUpdatedAtUtc)
                            VALUES($address, $dataset, $borrowers, $updated)
                            ON CONFLICT(ApiAddress) DO UPDATE SET DatasetId = excluded.DatasetId,
                                BorrowersJson = excluded.BorrowersJson, EquipmentUpdatedAtUtc = excluded.EquipmentUpdatedAtUtc;
                            ";
                        metadata.Parameters.AddWithValue("$address", NormalizeAddress(apiAddress));
                        metadata.Parameters.AddWithValue("$dataset", response.DatasetId);
                        metadata.Parameters.AddWithValue("$borrowers", Serialize(response.Borrowers ?? new List<Borrower>()));
                        metadata.Parameters.AddWithValue("$updated", now);
                        metadata.ExecuteNonQuery();
                    }

                    foreach (var item in response.Items)
                    {
                        using (var command = connection.CreateCommand())
                        {
                            command.Transaction = transaction;
                            command.CommandText = @"
                                INSERT INTO EquipmentCache(ApiAddress, DatasetId, EquipmentId, ItemJson, LastSeenUtc)
                                VALUES($address, $dataset, $id, $json, $seen)
                                ON CONFLICT(ApiAddress, DatasetId, EquipmentId) DO UPDATE SET
                                    ItemJson = excluded.ItemJson, LastSeenUtc = excluded.LastSeenUtc;
                                ";
                            command.Parameters.AddWithValue("$address", NormalizeAddress(apiAddress));
                            command.Parameters.AddWithValue("$dataset", response.DatasetId);
                            command.Parameters.AddWithValue("$id", item.Id);
                            command.Parameters.AddWithValue("$json", Serialize(item));
                            command.Parameters.AddWithValue("$seen", now);
                            command.ExecuteNonQuery();
                        }
                    }

                    using (var trim = connection.CreateCommand())
                    {
                        trim.Transaction = transaction;
                        trim.CommandText = @"
                            DELETE FROM EquipmentCache WHERE ApiAddress = $address AND DatasetId = $dataset
                            AND EquipmentId NOT IN (
                                SELECT EquipmentId FROM EquipmentCache WHERE ApiAddress = $address AND DatasetId = $dataset
                                ORDER BY LastSeenUtc DESC, EquipmentId ASC LIMIT $limit);
                            ";
                        trim.Parameters.AddWithValue("$address", NormalizeAddress(apiAddress));
                        trim.Parameters.AddWithValue("$dataset", response.DatasetId);
                        trim.Parameters.AddWithValue("$limit", MaxEquipmentRows);
                        trim.ExecuteNonQuery();
                    }
                    transaction.Commit();
                }
            }
        }

        public CachedEquipmentSnapshot GetEquipmentPage(string apiAddress, string query, string status, int page, int pageSize)
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                using (var connection = Open())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT DatasetId, BorrowersJson, EquipmentUpdatedAtUtc FROM CacheMetadata WHERE ApiAddress = $address;";
                    command.Parameters.AddWithValue("$address", NormalizeAddress(apiAddress));
                    string datasetId;
                    string borrowersJson;
                    string updatedAt;
                    using (var reader = command.ExecuteReader())
                    {
                        if (!reader.Read()) return null;
                        datasetId = reader.GetString(0);
                        borrowersJson = reader.GetString(1);
                        updatedAt = reader.GetString(2);
                    }

                    var borrowers = Deserialize<List<Borrower>>(borrowersJson) ?? new List<Borrower>();
                    var items = ReadEquipment(connection, apiAddress, datasetId);
                    var matching = items.Where(item =>
                        (status == null || status.Length == 0 || status == EquipmentStates.All || item.Status == status) &&
                        Matches(item, query)).OrderBy(item => item.Id).ToList();
                    var total = matching.Count;
                    var boundedPageSize = Math.Min(100, Math.Max(1, pageSize));
                    var lastPage = Math.Max(1, (int)Math.Ceiling((double)total / boundedPageSize));
                    var resolvedPage = Math.Min(Math.Max(1, page), lastPage);
                    var response = new EquipmentListResponse
                    {
                        DatasetId = datasetId,
                        Items = matching.Skip((resolvedPage - 1) * boundedPageSize).Take(boundedPageSize).ToList(),
                        Borrowers = borrowers,
                        TotalCount = total,
                        Page = resolvedPage,
                        PageSize = boundedPageSize
                    };
                    return new CachedEquipmentSnapshot
                    {
                        Response = response,
                        CachedItemCount = items.Count,
                        LastUpdatedUtc = updatedAt,
                        HasSnapshot = true
                    };
                }
            }
        }

        public void SaveHistoryPage(string apiAddress, string datasetId, int equipmentId, LoanHistoryListResponse response)
        {
            if (response == null || response.Items == null) throw new ArgumentException("A valid history response is required.", nameof(response));
            lock (_sync)
            {
                ThrowIfDisposed();
                using (var connection = Open())
                using (var transaction = connection.BeginTransaction())
                {
                    if (GetDatasetId(connection, transaction, apiAddress) != datasetId) return;
                    var now = UtcNow();
                    using (var state = connection.CreateCommand())
                    {
                        state.Transaction = transaction;
                        state.CommandText = @"
                            INSERT INTO HistoryCacheState(ApiAddress, DatasetId, EquipmentId, ServerTotalCount, UpdatedAtUtc)
                            VALUES($address, $dataset, $equipment, $total, $updated)
                            ON CONFLICT(ApiAddress, DatasetId, EquipmentId) DO UPDATE SET
                                ServerTotalCount = excluded.ServerTotalCount, UpdatedAtUtc = excluded.UpdatedAtUtc;
                            ";
                        state.Parameters.AddWithValue("$address", NormalizeAddress(apiAddress));
                        state.Parameters.AddWithValue("$dataset", datasetId);
                        state.Parameters.AddWithValue("$equipment", equipmentId);
                        state.Parameters.AddWithValue("$total", response.TotalCount);
                        state.Parameters.AddWithValue("$updated", now);
                        state.ExecuteNonQuery();
                    }

                    foreach (var item in response.Items)
                    {
                        using (var command = connection.CreateCommand())
                        {
                            command.Transaction = transaction;
                            command.CommandText = @"
                                INSERT INTO HistoryCache(ApiAddress, DatasetId, EquipmentId, EntryId, OccurredAtUtc, EntryJson, FetchedAtUtc)
                                VALUES($address, $dataset, $equipment, $id, $occurred, $json, $fetched)
                                ON CONFLICT(ApiAddress, DatasetId, EquipmentId, EntryId) DO UPDATE SET
                                    OccurredAtUtc = excluded.OccurredAtUtc, EntryJson = excluded.EntryJson, FetchedAtUtc = excluded.FetchedAtUtc;
                                ";
                            command.Parameters.AddWithValue("$address", NormalizeAddress(apiAddress));
                            command.Parameters.AddWithValue("$dataset", datasetId);
                            command.Parameters.AddWithValue("$equipment", equipmentId);
                            command.Parameters.AddWithValue("$id", item.Id);
                            command.Parameters.AddWithValue("$occurred", item.OccurredAtUtc ?? "");
                            command.Parameters.AddWithValue("$json", Serialize(item));
                            command.Parameters.AddWithValue("$fetched", now);
                            command.ExecuteNonQuery();
                        }
                    }

                    using (var trim = connection.CreateCommand())
                    {
                        trim.Transaction = transaction;
                        trim.CommandText = @"
                            DELETE FROM HistoryCache WHERE ApiAddress = $address AND DatasetId = $dataset AND EquipmentId = $equipment
                            AND EntryId NOT IN (
                                SELECT EntryId FROM HistoryCache WHERE ApiAddress = $address AND DatasetId = $dataset AND EquipmentId = $equipment
                                ORDER BY OccurredAtUtc DESC, EntryId DESC LIMIT $limit);
                            ";
                        trim.Parameters.AddWithValue("$address", NormalizeAddress(apiAddress));
                        trim.Parameters.AddWithValue("$dataset", datasetId);
                        trim.Parameters.AddWithValue("$equipment", equipmentId);
                        trim.Parameters.AddWithValue("$limit", MaxHistoryRowsPerEquipment);
                        trim.ExecuteNonQuery();
                    }
                    transaction.Commit();
                }
            }
        }

        public CachedHistorySnapshot GetHistoryPage(string apiAddress, string datasetId, int equipmentId, int page, int pageSize)
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                using (var connection = Open())
                using (var transaction = connection.BeginTransaction())
                {
                    int cachedCount;
                    int serverTotalCount;
                    string updatedAt;
                    using (var state = connection.CreateCommand())
                    {
                        state.Transaction = transaction;
                        state.CommandText = @"
                            SELECT s.ServerTotalCount, s.UpdatedAtUtc,
                                (SELECT COUNT(*) FROM HistoryCache h WHERE h.ApiAddress = s.ApiAddress AND h.DatasetId = s.DatasetId AND h.EquipmentId = s.EquipmentId)
                            FROM HistoryCacheState s WHERE s.ApiAddress = $address AND s.DatasetId = $dataset AND s.EquipmentId = $equipment;
                            ";
                        state.Parameters.AddWithValue("$address", NormalizeAddress(apiAddress));
                        state.Parameters.AddWithValue("$dataset", datasetId);
                        state.Parameters.AddWithValue("$equipment", equipmentId);
                        using (var reader = state.ExecuteReader())
                        {
                            if (!reader.Read()) return null;
                            serverTotalCount = reader.GetInt32(0);
                            updatedAt = reader.GetString(1);
                            cachedCount = reader.GetInt32(2);
                        }
                    }

                    var boundedPageSize = Math.Min(100, Math.Max(1, pageSize));
                    var lastPage = Math.Max(1, (int)Math.Ceiling((double)cachedCount / boundedPageSize));
                    var resolvedPage = Math.Min(Math.Max(1, page), lastPage);
                    var entries = new List<LoanHistoryEntry>();
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = @"
                            SELECT EntryJson FROM HistoryCache
                            WHERE ApiAddress = $address AND DatasetId = $dataset AND EquipmentId = $equipment
                            ORDER BY OccurredAtUtc DESC, EntryId DESC LIMIT $limit OFFSET $offset;
                            ";
                        command.Parameters.AddWithValue("$address", NormalizeAddress(apiAddress));
                        command.Parameters.AddWithValue("$dataset", datasetId);
                        command.Parameters.AddWithValue("$equipment", equipmentId);
                        command.Parameters.AddWithValue("$limit", boundedPageSize);
                        command.Parameters.AddWithValue("$offset", (long)(resolvedPage - 1) * boundedPageSize);
                        using (var reader = command.ExecuteReader())
                            while (reader.Read()) entries.Add(Deserialize<LoanHistoryEntry>(reader.GetString(0)));
                    }
                    transaction.Commit();
                    return new CachedHistorySnapshot
                    {
                        Response = new LoanHistoryListResponse
                        {
                            Items = entries,
                            TotalCount = cachedCount,
                            Page = resolvedPage,
                            PageSize = boundedPageSize
                        },
                        CachedItemCount = cachedCount,
                        ServerTotalCountAtLastFetch = serverTotalCount,
                        LastUpdatedUtc = updatedAt,
                        HasSnapshot = true
                    };
                }
            }
        }

        private void Initialize()
        {
            using (var connection = Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
                    CREATE TABLE IF NOT EXISTS CacheMetadata(
                        ApiAddress TEXT PRIMARY KEY, DatasetId TEXT NOT NULL, BorrowersJson TEXT NOT NULL, EquipmentUpdatedAtUtc TEXT NOT NULL);
                    CREATE TABLE IF NOT EXISTS EquipmentCache(
                        ApiAddress TEXT NOT NULL, DatasetId TEXT NOT NULL, EquipmentId INTEGER NOT NULL,
                        ItemJson TEXT NOT NULL, LastSeenUtc TEXT NOT NULL,
                        PRIMARY KEY(ApiAddress, DatasetId, EquipmentId));
                    CREATE TABLE IF NOT EXISTS HistoryCacheState(
                        ApiAddress TEXT NOT NULL, DatasetId TEXT NOT NULL, EquipmentId INTEGER NOT NULL,
                        ServerTotalCount INTEGER NOT NULL, UpdatedAtUtc TEXT NOT NULL,
                        PRIMARY KEY(ApiAddress, DatasetId, EquipmentId));
                    CREATE TABLE IF NOT EXISTS HistoryCache(
                        ApiAddress TEXT NOT NULL, DatasetId TEXT NOT NULL, EquipmentId INTEGER NOT NULL,
                        EntryId TEXT NOT NULL, OccurredAtUtc TEXT NOT NULL, EntryJson TEXT NOT NULL, FetchedAtUtc TEXT NOT NULL,
                        PRIMARY KEY(ApiAddress, DatasetId, EquipmentId, EntryId));
                    CREATE INDEX IF NOT EXISTS IX_EquipmentCache_LastSeen
                        ON EquipmentCache(ApiAddress, DatasetId, LastSeenUtc DESC, EquipmentId ASC);
                    CREATE INDEX IF NOT EXISTS IX_HistoryCache_Recent
                        ON HistoryCache(ApiAddress, DatasetId, EquipmentId, OccurredAtUtc DESC, EntryId DESC);
                    ";
                command.ExecuteNonQuery();
            }
        }

        private List<EquipmentItem> ReadEquipment(SqliteConnection connection, string apiAddress, string datasetId)
        {
            var items = new List<EquipmentItem>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT ItemJson FROM EquipmentCache WHERE ApiAddress = $address AND DatasetId = $dataset;";
                command.Parameters.AddWithValue("$address", NormalizeAddress(apiAddress));
                command.Parameters.AddWithValue("$dataset", datasetId);
                using (var reader = command.ExecuteReader())
                    while (reader.Read()) items.Add(Deserialize<EquipmentItem>(reader.GetString(0)));
            }
            return items;
        }

        private static string GetDatasetId(SqliteConnection connection, SqliteTransaction transaction, string apiAddress)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT DatasetId FROM CacheMetadata WHERE ApiAddress = $address;";
                command.Parameters.AddWithValue("$address", NormalizeAddress(apiAddress));
                return command.ExecuteScalar() as string;
            }
        }

        private static void DeleteAddressRows(SqliteConnection connection, SqliteTransaction transaction, string apiAddress)
        {
            foreach (var table in new[] { "EquipmentCache", "HistoryCache", "HistoryCacheState" })
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "DELETE FROM " + table + " WHERE ApiAddress = $address;";
                    command.Parameters.AddWithValue("$address", NormalizeAddress(apiAddress));
                    command.ExecuteNonQuery();
                }
            }
        }

        private SqliteConnection Open()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA busy_timeout = 5000;";
                command.ExecuteNonQuery();
            }
            return connection;
        }

        private void Quarantine()
        {
            SqliteConnection.ClearAllPools();
            var suffix = ".corrupt-" + DateTime.UtcNow.Ticks;
            foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
                if (File.Exists(path)) File.Move(path, path + suffix);
        }

        private static bool Matches(EquipmentItem item, string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return true;
            var normalized = query.Trim();
            return Contains(item.Code, normalized) || Contains(item.Name, normalized) || Contains(item.Category, normalized);
        }

        private static bool Contains(string value, string query) =>
            (value ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

        private static string NormalizeAddress(string address) => (address ?? "").TrimEnd('/');
        private static string UtcNow() => DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

        private static string Serialize<T>(T value)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private static T Deserialize<T>(string json)
        {
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json ?? "")))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }

        private static bool IsCorrupt(SqliteException exception) => exception.SqliteErrorCode == 11 || exception.SqliteErrorCode == 26;
        private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(ReadCacheStore)); }

        public void Dispose()
        {
            lock (_sync) _disposed = true;
        }
    }
}
