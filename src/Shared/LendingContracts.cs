using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace WpfDemo
{
    public static class EquipmentStates
    {
        public const string All = "전체";
        public const string Available = "사용 가능";
        public const string OnLoan = "대여 중";
        public const string Maintenance = "점검 중";
    }

    [DataContract]
    public sealed class Borrower
    {
        [DataMember(Name = "id", Order = 1)] public string Id { get; set; }
        [DataMember(Name = "displayName", Order = 2)] public string DisplayName { get; set; }
    }

    [DataContract]
    public sealed class ActiveLoan
    {
        [DataMember(Name = "id", Order = 1)] public string Id { get; set; }
        [DataMember(Name = "borrowerId", Order = 2)] public string BorrowerId { get; set; }
        [DataMember(Name = "borrowerName", Order = 3)] public string BorrowerName { get; set; }
        [DataMember(Name = "borrowedAtUtc", Order = 4)] public string BorrowedAtUtc { get; set; }
    }

    [DataContract]
    public sealed class EquipmentItem
    {
        [DataMember(Name = "id", Order = 1)] public int Id { get; set; }
        [DataMember(Name = "code", Order = 2)] public string Code { get; set; }
        [DataMember(Name = "name", Order = 3)] public string Name { get; set; }
        [DataMember(Name = "category", Order = 4)] public string Category { get; set; }
        [DataMember(Name = "status", Order = 5)] public string Status { get; set; }
        [DataMember(Name = "version", Order = 6)] public long Version { get; set; }
        [DataMember(Name = "activeLoan", Order = 7)] public ActiveLoan ActiveLoan { get; set; }
    }

    [DataContract]
    public sealed class EquipmentListResponse
    {
        [DataMember(Name = "datasetId", Order = 1)] public string DatasetId { get; set; }
        [DataMember(Name = "items", Order = 2)] public List<EquipmentItem> Items { get; set; }
        [DataMember(Name = "borrowers", Order = 3)] public List<Borrower> Borrowers { get; set; }
        [DataMember(Name = "totalCount", Order = 4)] public int TotalCount { get; set; }
        [DataMember(Name = "page", Order = 5)] public int Page { get; set; }
        [DataMember(Name = "pageSize", Order = 6)] public int PageSize { get; set; }
    }

    [DataContract]
    public sealed class LoanHistoryListResponse
    {
        [DataMember(Name = "items", Order = 1)] public List<LoanHistoryEntry> Items { get; set; }
        [DataMember(Name = "totalCount", Order = 2)] public int TotalCount { get; set; }
        [DataMember(Name = "page", Order = 3)] public int Page { get; set; }
        [DataMember(Name = "pageSize", Order = 4)] public int PageSize { get; set; }
    }

    [DataContract]
    public sealed class LoanHistoryEntry
    {
        [DataMember(Name = "id", Order = 1)] public string Id { get; set; }
        [DataMember(Name = "loanId", Order = 2)] public string LoanId { get; set; }
        [DataMember(Name = "operationId", Order = 3)] public string OperationId { get; set; }
        [DataMember(Name = "kind", Order = 4)] public string Kind { get; set; }
        [DataMember(Name = "borrowerName", Order = 5)] public string BorrowerName { get; set; }
        [DataMember(Name = "occurredAtUtc", Order = 6)] public string OccurredAtUtc { get; set; }
        [DataMember(Name = "note", Order = 7)] public string Note { get; set; }
    }

    [DataContract]
    public sealed class BorrowCommand
    {
        [DataMember(Name = "operationId", Order = 1)] public string OperationId { get; set; }
        [DataMember(Name = "datasetId", Order = 2)] public string DatasetId { get; set; }
        [DataMember(Name = "expectedVersion", Order = 3)] public long ExpectedVersion { get; set; }
        [DataMember(Name = "borrowerId", Order = 4)] public string BorrowerId { get; set; }
        [DataMember(Name = "note", Order = 5)] public string Note { get; set; }
    }

    [DataContract]
    public sealed class ReturnCommand
    {
        [DataMember(Name = "operationId", Order = 1)] public string OperationId { get; set; }
        [DataMember(Name = "datasetId", Order = 2)] public string DatasetId { get; set; }
        [DataMember(Name = "expectedVersion", Order = 3)] public long ExpectedVersion { get; set; }
        [DataMember(Name = "loanId", Order = 4)] public string LoanId { get; set; }
        [DataMember(Name = "note", Order = 5)] public string Note { get; set; }
    }

    [DataContract]
    public sealed class LoanOperationResponse
    {
        [DataMember(Name = "operationId", Order = 1)] public string OperationId { get; set; }
        [DataMember(Name = "success", Order = 2)] public bool Success { get; set; }
        [DataMember(Name = "code", Order = 3)] public string Code { get; set; }
        [DataMember(Name = "message", Order = 4)] public string Message { get; set; }
        [DataMember(Name = "equipment", Order = 5)] public EquipmentItem Equipment { get; set; }
        [DataMember(Name = "loanId", Order = 6)] public string LoanId { get; set; }
        [DataMember(Name = "eventId", Order = 7)] public string EventId { get; set; }
    }

    [DataContract]
    public sealed class ApiErrorResponse
    {
        [DataMember(Name = "code", Order = 1)] public string Code { get; set; }
        [DataMember(Name = "message", Order = 2)] public string Message { get; set; }
        [DataMember(Name = "equipment", Order = 3)] public EquipmentItem Equipment { get; set; }
    }

    [DataContract]
    public sealed class RequestDiagnosticEvent
    {
        [DataMember(Name = "requestId", Order = 1)] public string RequestId { get; set; }
        [DataMember(Name = "occurredAtUtc", Order = 2)] public string OccurredAtUtc { get; set; }
        [DataMember(Name = "method", Order = 3)] public string Method { get; set; }
        [DataMember(Name = "route", Order = 4)] public string Route { get; set; }
        [DataMember(Name = "statusCode", Order = 5)] public int StatusCode { get; set; }
        [DataMember(Name = "elapsedMilliseconds", Order = 6)] public long ElapsedMilliseconds { get; set; }
    }

    [DataContract]
    public sealed class DiagnosticsResponse
    {
        [DataMember(Name = "serverStartedAtUtc", Order = 1)] public string ServerStartedAtUtc { get; set; }
        [DataMember(Name = "isPartial", Order = 2)] public bool IsPartial { get; set; }
        [DataMember(Name = "events", Order = 3)] public List<RequestDiagnosticEvent> Events { get; set; }
    }

    [DataContract]
    public sealed class ClientDiagnosticEvent
    {
        [DataMember(Name = "requestId", Order = 1)] public string RequestId { get; set; }
        [DataMember(Name = "occurredAtUtc", Order = 2)] public string OccurredAtUtc { get; set; }
        [DataMember(Name = "operation", Order = 3)] public string Operation { get; set; }
        [DataMember(Name = "operationId", Order = 4)] public string OperationId { get; set; }
        [DataMember(Name = "statusCode", Order = 5)] public int StatusCode { get; set; }
        [DataMember(Name = "elapsedMilliseconds", Order = 6)] public long ElapsedMilliseconds { get; set; }
        [DataMember(Name = "result", Order = 7)] public string Result { get; set; }
    }
}
