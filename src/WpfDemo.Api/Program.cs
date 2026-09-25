using WpfDemo;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5187");
var app = builder.Build();

var gate = new object();
var items = new List<WorkItem>
{
    new() { Id = 1001, Title = "오전 요청 목록 확인", Category = "운영", Status = WorkStatus.Waiting, Note = "" },
    new() { Id = 1002, Title = "예약 일정 변경 확인", Category = "일정", Status = WorkStatus.InProgress, Note = "변경 요청 내용 검토" },
    new() { Id = 1003, Title = "회의실 안내 문구 점검", Category = "안내", Status = WorkStatus.Waiting, Note = "" },
    new() { Id = 1004, Title = "일일 업무 목록 정리", Category = "운영", Status = WorkStatus.Completed, Note = "예시 항목" },
    new() { Id = 1005, Title = "서류 전달 상태 확인", Category = "서류", Status = WorkStatus.Waiting, Note = "" }
};

app.MapGet("/api/work-items", () =>
{
    lock (gate)
    {
        return items.Select(Clone).ToArray();
    }
});

app.MapPut("/api/work-items/{id:int}", (int id, UpdateItemRequest request) =>
{
    if (request.Status != WorkStatus.Waiting &&
        request.Status != WorkStatus.InProgress &&
        request.Status != WorkStatus.Completed)
        return Results.BadRequest(new { message = "유효하지 않은 상태입니다." });

    var note = request.Note ?? "";
    if (note.Length > 200 || (note.Length > 0 && string.IsNullOrWhiteSpace(note)))
        return Results.BadRequest(new { message = "메모는 공백만 입력할 수 없으며 200자 이하여야 합니다." });

    lock (gate)
    {
        var item = items.FirstOrDefault(x => x.Id == id);
        if (item == null) return Results.NotFound();
        item.Status = request.Status;
        item.Note = note.Trim();
        return Results.Ok(Clone(item));
    }
});

app.Run();

static WorkItem Clone(WorkItem item) => new()
{
    Id = item.Id,
    Title = item.Title,
    Category = item.Category,
    Status = item.Status,
    Note = item.Note
};

public sealed class UpdateItemRequest
{
    public string Status { get; set; }
    public string Note { get; set; }
}
