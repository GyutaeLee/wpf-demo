using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Data.Sqlite;
using WpfDemo;
using WpfDemo.Api;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5187");
builder.Services.Configure<JsonOptions>(options => options.SerializerOptions.PropertyNameCaseInsensitive = true);
var dataDirectory = builder.Configuration["WPFDEMO_SERVER_DATA_DIR"];
if (string.IsNullOrWhiteSpace(dataDirectory))
    dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wpf-demo", "server");
var store = new LendingStore(dataDirectory);
var dropNextBorrowResponse = string.Equals(builder.Configuration["WPFDEMO_DEMO_DROP_NEXT_BORROW_RESPONSE"], "true", StringComparison.OrdinalIgnoreCase);
var faultUsed = 0;
var app = builder.Build();

app.Use(async (context, next) =>
{
    var watch = Stopwatch.StartNew();
    try { await next(); }
    finally
    {
        if (context.Request.Path.Value?.EndsWith("/borrow", StringComparison.Ordinal) == true ||
            context.Request.Path.Value?.EndsWith("/return", StringComparison.Ordinal) == true)
            app.Logger.LogInformation("Operation {OperationId} finished with {StatusCode} in {ElapsedMilliseconds} ms",
                context.Request.Headers["Idempotency-Key"].ToString(), context.Response.StatusCode, watch.ElapsedMilliseconds);
    }
});

app.MapGet("/api/equipment", () => Results.Ok(store.GetEquipment()));
app.MapGet("/api/equipment/{id:int}", (int id) =>
{
    var item = store.GetEquipment().Items.FirstOrDefault(x => x.Id == id);
    return item == null ? Results.NotFound() : Results.Ok(item);
});
app.MapGet("/api/borrowers", () => Results.Ok(store.GetBorrowers()));
app.MapGet("/api/equipment/{id:int}/history", (int id) => Results.Ok(store.GetHistory(id)));
app.MapPost("/api/equipment/{id:int}/borrow", async (int id, HttpContext context) =>
{
    var request = await ReadCommand<BorrowCommand>(context.Request);
    var operationId = ReadOperationId(context.Request);
    if (request == null || operationId == null || !string.Equals(request.OperationId, operationId, StringComparison.Ordinal))
        return Results.Json(new ApiErrorResponse { Code = "InvalidCommand", Message = "요청 정보가 올바르지 않습니다." }, statusCode: 400);
    request.Note ??= "";
    if (!ValidIdentity(request.OperationId, request.DatasetId, request.ExpectedVersion) ||
        !store.GetBorrowers().Any(x => x.Id == request.BorrowerId))
        return Results.Json(new ApiErrorResponse { Code = "InvalidCommand", Message = "요청 정보가 올바르지 않습니다." }, statusCode: 400);
    if (!ValidNote(request.Note))
        return Results.Json(new ApiErrorResponse { Code = "InvalidNote", Message = "메모는 공백만 입력할 수 없으며 200자 이하여야 합니다." }, statusCode: 400);

    var fingerprint = LendingStore.RequestFingerprint("Borrow", id, request.DatasetId,
        request.ExpectedVersion, request.BorrowerId, null, request.Note);
    OperationExecution execution;
    try { execution = store.Borrow(id, request, operationId, fingerprint, DateTime.UtcNow); }
    catch (SqliteException ex)
    {
        app.Logger.LogWarning("Operation {OperationId} could not be committed because the database was busy: {ErrorCode}", operationId, ex.SqliteErrorCode);
        return Results.Json(new ApiErrorResponse { Code = "StorageUnavailable", Message = "저장소가 사용 중입니다. 같은 요청을 다시 시도해 주세요." }, statusCode: 503);
    }
    if (dropNextBorrowResponse && execution.StatusCode == 200 && !execution.Replayed && Interlocked.Exchange(ref faultUsed, 1) == 0)
    {
        context.Abort();
        return Results.Empty;
    }
    return Results.Json(execution.Response, statusCode: execution.StatusCode);
});
app.MapPost("/api/equipment/{id:int}/return", async (int id, HttpContext context) =>
{
    var request = await ReadCommand<ReturnCommand>(context.Request);
    var operationId = ReadOperationId(context.Request);
    if (request == null || operationId == null || !string.Equals(request.OperationId, operationId, StringComparison.Ordinal))
        return Results.Json(new ApiErrorResponse { Code = "InvalidCommand", Message = "요청 정보가 올바르지 않습니다." }, statusCode: 400);
    request.Note ??= "";
    if (!ValidIdentity(request.OperationId, request.DatasetId, request.ExpectedVersion) || string.IsNullOrWhiteSpace(request.LoanId))
        return Results.Json(new ApiErrorResponse { Code = "InvalidCommand", Message = "요청 정보가 올바르지 않습니다." }, statusCode: 400);
    if (!ValidNote(request.Note))
        return Results.Json(new ApiErrorResponse { Code = "InvalidNote", Message = "메모는 공백만 입력할 수 없으며 200자 이하여야 합니다." }, statusCode: 400);

    var fingerprint = LendingStore.RequestFingerprint("Return", id, request.DatasetId,
        request.ExpectedVersion, null, request.LoanId, request.Note);
    OperationExecution execution;
    try { execution = store.Return(id, request, operationId, fingerprint, DateTime.UtcNow); }
    catch (SqliteException ex)
    {
        app.Logger.LogWarning("Operation {OperationId} could not be committed because the database was busy: {ErrorCode}", operationId, ex.SqliteErrorCode);
        return Results.Json(new ApiErrorResponse { Code = "StorageUnavailable", Message = "저장소가 사용 중입니다. 같은 요청을 다시 시도해 주세요." }, statusCode: 503);
    }
    return Results.Json(execution.Response, statusCode: execution.StatusCode);
});

app.Run();

static async Task<T> ReadCommand<T>(HttpRequest request) where T : class
{
    try { return await JsonSerializer.DeserializeAsync<T>(request.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
    catch (JsonException) { return null; }
}

static string ReadOperationId(HttpRequest request)
{
    var header = request.Headers["Idempotency-Key"].ToString();
    if (!Guid.TryParse(header, out var id)) return null;
    return id.ToString("D");
}

static bool ValidNote(string note) => note == null || (note.Length <= 200 && (note.Length == 0 || !string.IsNullOrWhiteSpace(note)));

static bool ValidIdentity(string operationId, string datasetId, long expectedVersion) =>
    Guid.TryParse(operationId, out _) && Guid.TryParse(datasetId, out _) && expectedVersion > 0;

public partial class Program { }
