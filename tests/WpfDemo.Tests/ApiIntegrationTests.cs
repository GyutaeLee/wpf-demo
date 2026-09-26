using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfDemo;

namespace WpfDemo.Tests;

[TestClass]
public sealed class ApiIntegrationTests
{
    [TestMethod]
    public async Task GetReturnsSeededWorkItems()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var items = await client.GetFromJsonAsync<WorkItem[]>("/api/work-items");
        Assert.IsNotNull(items);
        Assert.AreEqual(5, items.Length);
        Assert.AreEqual(1001, items[0].Id);
    }

    [TestMethod]
    public async Task PutUpdatesAnExistingItem()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var response = await client.PutAsJsonAsync("/api/work-items/1001", new
        {
            status = WorkStatus.InProgress,
            note = "Updated through HTTP"
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<WorkItem>();
        Assert.IsNotNull(updated);
        Assert.AreEqual(WorkStatus.InProgress, updated.Status);
        Assert.AreEqual("Updated through HTTP", updated.Note);

        var items = await client.GetFromJsonAsync<WorkItem[]>("/api/work-items");
        Assert.IsNotNull(items);
        var persisted = items.Single(item => item.Id == 1001);
        Assert.AreEqual("오전 요청 목록 확인", persisted.Title);
        Assert.AreEqual("운영", persisted.Category);
        Assert.AreEqual(WorkStatus.InProgress, persisted.Status);
        Assert.AreEqual("Updated through HTTP", persisted.Note);
    }

    [TestMethod]
    public async Task PutRejectsInvalidStatusWithoutChangingExistingItem()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var invalidStatus = await client.PutAsJsonAsync("/api/work-items/1001", new
        {
            status = "Unknown",
            note = "replacement note"
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidStatus.StatusCode);

        var items = await client.GetFromJsonAsync<WorkItem[]>("/api/work-items");
        Assert.IsNotNull(items);
        var unchanged = items.Single(item => item.Id == 1001);
        Assert.AreEqual("오전 요청 목록 확인", unchanged.Title);
        Assert.AreEqual("운영", unchanged.Category);
        Assert.AreEqual(WorkStatus.Waiting, unchanged.Status);
        Assert.AreEqual("", unchanged.Note);
    }

    [TestMethod]
    public async Task PutRejectsWhitespaceOnlyNoteWithoutChangingExistingItem()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var response = await client.PutAsJsonAsync("/api/work-items/1002", new
        {
            status = WorkStatus.Completed,
            note = "   "
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);

        var items = await client.GetFromJsonAsync<WorkItem[]>("/api/work-items");
        Assert.IsNotNull(items);
        var unchanged = items.Single(item => item.Id == 1002);
        Assert.AreEqual("예약 일정 변경 확인", unchanged.Title);
        Assert.AreEqual("일정", unchanged.Category);
        Assert.AreEqual(WorkStatus.InProgress, unchanged.Status);
        Assert.AreEqual("변경 요청 내용 검토", unchanged.Note);
    }

    [TestMethod]
    public async Task PutRejectsNoteOver200CharactersWithoutChangingExistingItem()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var response = await client.PutAsJsonAsync("/api/work-items/1002", new
        {
            status = WorkStatus.Completed,
            note = new string('x', 201)
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);

        var items = await client.GetFromJsonAsync<WorkItem[]>("/api/work-items");
        Assert.IsNotNull(items);
        var unchanged = items.Single(item => item.Id == 1002);
        Assert.AreEqual("예약 일정 변경 확인", unchanged.Title);
        Assert.AreEqual("일정", unchanged.Category);
        Assert.AreEqual(WorkStatus.InProgress, unchanged.Status);
        Assert.AreEqual("변경 요청 내용 검토", unchanged.Note);
    }

    [TestMethod]
    public async Task PutAcceptsMaximumLengthNoteAndGetReturnsIt()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var note = new string('x', 200);
        using var response = await client.PutAsJsonAsync("/api/work-items/1001", new
        {
            status = WorkStatus.Completed,
            note
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var items = await client.GetFromJsonAsync<WorkItem[]>("/api/work-items");
        Assert.IsNotNull(items);
        var updated = items.Single(item => item.Id == 1001);
        Assert.AreEqual("오전 요청 목록 확인", updated.Title);
        Assert.AreEqual("운영", updated.Category);
        Assert.AreEqual(WorkStatus.Completed, updated.Status);
        Assert.AreEqual(note, updated.Note);
    }

    [TestMethod]
    public async Task PutReturnsNotFoundForUnknownItem()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var response = await client.PutAsJsonAsync("/api/work-items/9999", new
        {
            status = WorkStatus.Waiting,
            note = "valid"
        });

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }
}
