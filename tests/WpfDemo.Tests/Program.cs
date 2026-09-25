using WpfDemo;

var cases = new (string Name, Func<Task> Test)[]
{
    ("load and select", LoadAndSelect),
    ("search and status filter", SearchAndFilter),
    ("empty and load retry", EmptyAndLoadRetry),
    ("validation and cancel", ValidationAndCancel),
    ("save success", SaveSuccess),
    ("save failure and retry", SaveFailureAndRetry),
    ("loading is asynchronous", LoadingIsAsynchronous)
};

var failures = 0;
foreach (var test in cases)
{
    try
    {
        await test.Test();
        Console.WriteLine("PASS " + test.Name);
    }
    catch (Exception error)
    {
        failures++;
        Console.Error.WriteLine("FAIL " + test.Name + ": " + error);
    }
}
return failures == 0 ? 0 : 1;

static async Task LoadAndSelect()
{
    var vm = new WorklistViewModel(new FakeApi());
    await vm.LoadAsync();
    Check(vm.VisibleItems.Count == 3, "load count");
    Check(!vm.HasSelection, "initial selection");
    vm.SelectedItem = vm.VisibleItems[0];
    Check(vm.HasSelection && vm.EditedStatus == WorklistViewModel.Waiting, "selection");
    Check(!vm.IsDirty && !vm.CanSave, "clean draft");
}

static async Task SearchAndFilter()
{
    var vm = new WorklistViewModel(new FakeApi());
    await vm.LoadAsync();
    vm.SearchText = "FORM";
    Check(vm.VisibleItems.Count == 1 && vm.VisibleItems[0].Id == 2, "case-insensitive search");
    vm.SearchText = "";
    vm.StatusFilter = WorklistViewModel.Completed;
    Check(vm.VisibleItems.Count == 1 && vm.VisibleItems[0].Id == 3, "status filter");
    vm.SearchText = "no match";
    Check(vm.IsEmpty, "empty result");
}

static async Task EmptyAndLoadRetry()
{
    var api = new FakeApi { FailLoad = true };
    var vm = new WorklistViewModel(api);
    await vm.LoadAsync();
    Check(vm.HasLoadError && !vm.IsEmpty, "load error is distinct from empty");
    api.FailLoad = false;
    await vm.LoadAsync();
    Check(!vm.HasLoadError && vm.VisibleItems.Count == 3, "load retry");
}

static async Task ValidationAndCancel()
{
    var vm = new WorklistViewModel(new FakeApi());
    await vm.LoadAsync();
    vm.SelectedItem = vm.VisibleItems[0];
    vm.EditedNote = "   ";
    Check(vm.IsDirty && !vm.CanSave && vm.ValidationMessage != "", "whitespace validation");
    vm.EditedNote = new string('x', 201);
    Check(!vm.CanSave && vm.ValidationMessage.Contains("200"), "length validation");
    vm.EditedNote = "updated";
    Check(vm.CanSave && !vm.CanNavigate, "valid dirty draft locks navigation");
    vm.SelectedItem = vm.VisibleItems[1];
    Check(vm.SelectedItem?.Id == 1, "selection cannot change while dirty");
    vm.Cancel();
    Check(!vm.IsDirty && vm.EditedNote == "" && vm.CanNavigate, "cancel restores item");
}

static async Task SaveSuccess()
{
    var api = new FakeApi();
    var vm = new WorklistViewModel(api);
    await vm.LoadAsync();
    vm.SelectedItem = vm.VisibleItems[0];
    vm.EditedStatus = WorklistViewModel.InProgress;
    vm.EditedNote = "reviewed";
    await vm.SaveAsync();
    Check(!vm.IsDirty && !vm.HasSaveError, "save succeeded");
    Check(vm.SelectedItem?.Status == WorklistViewModel.InProgress, "list item refreshed");
    Check(api.Items[0].Note == "reviewed", "API received update");
}

static async Task SaveFailureAndRetry()
{
    var api = new FakeApi { FailSave = true };
    var vm = new WorklistViewModel(api);
    await vm.LoadAsync();
    vm.SelectedItem = vm.VisibleItems[0];
    vm.EditedNote = "keep this draft";
    await vm.SaveAsync();
    Check(vm.HasSaveError && vm.IsDirty && vm.EditedNote == "keep this draft", "failed save keeps draft");
    api.FailSave = false;
    await vm.SaveAsync();
    Check(!vm.HasSaveError && !vm.IsDirty && api.Items[0].Note == "keep this draft", "save retry");
}

static async Task LoadingIsAsynchronous()
{
    var pending = new TaskCompletionSource<IReadOnlyList<WorkItem>>();
    var api = new FakeApi { LoadOverride = () => pending.Task };
    var vm = new WorklistViewModel(api);
    var load = vm.LoadAsync();
    Check(vm.IsLoading && !load.IsCompleted, "load starts without blocking");
    pending.SetResult(api.Items);
    await load;
    Check(!vm.IsLoading && vm.VisibleItems.Count == 3, "load completes");
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

sealed class FakeApi : IWorkItemApi
{
    public List<WorkItem> Items { get; } = new()
    {
        new() { Id = 1, Title = "Morning list", Category = "Desk", Status = WorklistViewModel.Waiting, Note = "" },
        new() { Id = 2, Title = "Form review", Category = "Forms", Status = WorklistViewModel.InProgress, Note = "" },
        new() { Id = 3, Title = "Closing tasks", Category = "Desk", Status = WorklistViewModel.Completed, Note = "" }
    };
    public bool FailLoad { get; set; }
    public bool FailSave { get; set; }
    public Func<Task<IReadOnlyList<WorkItem>>> LoadOverride { get; set; }

    public Task<IReadOnlyList<WorkItem>> GetItemsAsync()
    {
        if (FailLoad) throw new InvalidOperationException("mock offline");
        return LoadOverride?.Invoke() ?? Task.FromResult<IReadOnlyList<WorkItem>>(Items.Select(Clone).ToArray());
    }

    public Task<WorkItem> UpdateItemAsync(int id, string status, string note)
    {
        if (FailSave) throw new InvalidOperationException("mock offline");
        var item = Items.Single(x => x.Id == id);
        item.Status = status;
        item.Note = note;
        return Task.FromResult(Clone(item));
    }

    private static WorkItem Clone(WorkItem item) => new()
    {
        Id = item.Id, Title = item.Title, Category = item.Category, Status = item.Status, Note = item.Note
    };
}
