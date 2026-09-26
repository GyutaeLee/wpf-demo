using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfDemo;

namespace WpfDemo.Tests;

[TestClass]
public sealed class WorklistViewModelTests
{
    [TestMethod]
    public async Task LoadAndSelect()
    {
        var vm = new WorklistViewModel(new FakeApi());
        await vm.LoadAsync();
        Assert.AreEqual(3, vm.VisibleItems.Count);
        Assert.IsFalse(vm.HasSelection);
        vm.SelectedItem = vm.VisibleItems[0];
        Assert.IsTrue(vm.HasSelection);
        Assert.AreEqual(WorklistViewModel.Waiting, vm.EditedStatus);
        Assert.IsFalse(vm.IsDirty);
        Assert.IsFalse(vm.CanSave);
    }

    [TestMethod]
    public async Task SearchAndStatusFilter()
    {
        var vm = new WorklistViewModel(new FakeApi());
        await vm.LoadAsync();
        vm.SearchText = "FORM";
        Assert.AreEqual(1, vm.VisibleItems.Count);
        Assert.AreEqual(2, vm.VisibleItems[0].Id);
        vm.SearchText = "";
        vm.StatusFilter = WorklistViewModel.Completed;
        Assert.AreEqual(1, vm.VisibleItems.Count);
        Assert.AreEqual(3, vm.VisibleItems[0].Id);
        vm.SearchText = "no match";
        Assert.IsTrue(vm.IsEmpty);
    }

    [TestMethod]
    public async Task LoadErrorIsDistinctFromEmptyAndCanRetry()
    {
        var api = new FakeApi { FailLoad = true };
        var vm = new WorklistViewModel(api);
        await vm.LoadAsync();
        Assert.IsTrue(vm.HasLoadError);
        Assert.IsFalse(vm.IsEmpty);
        api.FailLoad = false;
        await vm.LoadAsync();
        Assert.IsFalse(vm.HasLoadError);
        Assert.AreEqual(3, vm.VisibleItems.Count);
    }

    [TestMethod]
    public async Task ValidationCancelAndNavigationGuard()
    {
        var vm = new WorklistViewModel(new FakeApi());
        await vm.LoadAsync();
        vm.SelectedItem = vm.VisibleItems[0];
        vm.EditedStatus = "unknown";
        Assert.IsFalse(vm.CanSave);
        StringAssert.Contains(vm.ValidationMessage, "상태");
        vm.EditedStatus = WorklistViewModel.Waiting;
        vm.EditedNote = new string('x', 200);
        Assert.IsTrue(vm.CanSave);
        Assert.AreEqual("", vm.ValidationMessage);
        vm.EditedNote = "   ";
        Assert.IsTrue(vm.IsDirty);
        Assert.IsFalse(vm.CanSave);
        Assert.IsTrue(vm.ValidationMessage.Length > 0);
        vm.EditedNote = new string('x', 201);
        Assert.IsFalse(vm.CanSave);
        StringAssert.Contains(vm.ValidationMessage, "200");
        vm.EditedNote = "updated";
        Assert.IsTrue(vm.CanSave);
        Assert.IsFalse(vm.CanNavigate);
        vm.SelectedItem = vm.VisibleItems[1];
        Assert.AreEqual(1, vm.SelectedItem?.Id);
        vm.Cancel();
        Assert.IsFalse(vm.IsDirty);
        Assert.AreEqual("", vm.EditedNote);
        Assert.IsTrue(vm.CanNavigate);
    }

    [TestMethod]
    public async Task SaveSuccessUpdatesSelectedItem()
    {
        var api = new FakeApi();
        var vm = new WorklistViewModel(api);
        await vm.LoadAsync();
        vm.SelectedItem = vm.VisibleItems[0];
        vm.EditedStatus = WorklistViewModel.InProgress;
        vm.EditedNote = "reviewed";
        await vm.SaveAsync();
        Assert.IsFalse(vm.IsDirty);
        Assert.IsFalse(vm.HasSaveError);
        Assert.AreEqual(WorklistViewModel.InProgress, vm.SelectedItem?.Status);
        Assert.AreEqual("reviewed", api.Items[0].Note);
    }

    [TestMethod]
    public async Task SavingBlocksNavigationLoadingDuplicateSaveAndCancelUntilResponseArrives()
    {
        var pendingUpdate = new TaskCompletionSource<WorkItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new FakeApi { UpdateOverride = (id, status, note) => pendingUpdate.Task };
        var vm = new WorklistViewModel(api);
        await vm.LoadAsync();
        var selected = vm.VisibleItems[0];
        vm.SelectedItem = selected;
        vm.EditedStatus = WorklistViewModel.InProgress;
        vm.EditedNote = "save this";

        var save = vm.SaveAsync();

        Assert.IsTrue(vm.IsSaving);
        Assert.IsFalse(vm.CanNavigate);
        Assert.IsFalse(vm.CanEdit);
        Assert.IsFalse(vm.CanSave);
        Assert.IsFalse(vm.LoadCommand.CanExecute(null));
        Assert.IsFalse(vm.SaveCommand.CanExecute(null));
        Assert.IsFalse(vm.CancelCommand.CanExecute(null));
        Assert.AreEqual(1, api.LoadCalls);
        Assert.AreEqual(1, api.UpdateCalls);

        vm.SelectedItem = vm.VisibleItems[1];
        vm.SearchText = "FORM";
        vm.StatusFilter = WorklistViewModel.Completed;
        await vm.LoadAsync();
        await vm.SaveAsync();
        vm.Cancel();

        Assert.AreSame(selected, vm.SelectedItem);
        Assert.AreEqual("", vm.SearchText);
        Assert.AreEqual(WorklistViewModel.AllStatuses, vm.StatusFilter);
        Assert.AreEqual(WorklistViewModel.InProgress, vm.EditedStatus);
        Assert.AreEqual("save this", vm.EditedNote);
        Assert.IsTrue(vm.IsDirty);
        Assert.AreEqual(1, api.LoadCalls);
        Assert.AreEqual(1, api.UpdateCalls);
        Assert.IsFalse(save.IsCompleted);

        pendingUpdate.SetResult(new WorkItem
        {
            Id = 1, Title = "Morning list", Category = "Desk", Status = WorklistViewModel.InProgress, Note = "save this"
        });
        await save;

        Assert.IsFalse(vm.IsSaving);
        Assert.IsTrue(vm.CanNavigate);
        Assert.IsTrue(vm.CanEdit);
        Assert.IsFalse(vm.IsDirty);
        Assert.AreSame(selected, vm.SelectedItem);
        Assert.AreEqual(1, api.LoadCalls);
        Assert.AreEqual(1, api.UpdateCalls);
    }

    [TestMethod]
    public async Task SaveFailureKeepsDraftAndCanRetry()
    {
        var api = new FakeApi { FailSave = true };
        var vm = new WorklistViewModel(api);
        await vm.LoadAsync();
        vm.SelectedItem = vm.VisibleItems[0];
        vm.EditedNote = "keep this draft";
        await vm.SaveAsync();
        Assert.IsTrue(vm.HasSaveError);
        Assert.IsTrue(vm.IsDirty);
        Assert.AreEqual("keep this draft", vm.EditedNote);
        api.FailSave = false;
        await vm.SaveAsync();
        Assert.IsFalse(vm.HasSaveError);
        Assert.IsFalse(vm.IsDirty);
        Assert.AreEqual("keep this draft", api.Items[0].Note);
    }

    [TestMethod]
    public async Task LoadingDoesNotBlockCaller()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<WorkItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new FakeApi { LoadOverride = () => pending.Task };
        var vm = new WorklistViewModel(api);
        var load = vm.LoadAsync();
        Assert.IsTrue(vm.IsLoading);
        Assert.IsFalse(load.IsCompleted);
        pending.SetResult(api.Items);
        await load;
        Assert.IsFalse(vm.IsLoading);
        Assert.AreEqual(3, vm.VisibleItems.Count);
    }

    private sealed class FakeApi : IWorkItemApi
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
        public Func<int, string, string, Task<WorkItem>> UpdateOverride { get; set; }
        public int LoadCalls { get; private set; }
        public int UpdateCalls { get; private set; }

        public Task<IReadOnlyList<WorkItem>> GetItemsAsync()
        {
            LoadCalls++;
            if (FailLoad) throw new InvalidOperationException("mock offline");
            return LoadOverride?.Invoke() ?? Task.FromResult<IReadOnlyList<WorkItem>>(Items.Select(Clone).ToArray());
        }

        public Task<WorkItem> UpdateItemAsync(int id, string status, string note)
        {
            UpdateCalls++;
            if (FailSave) throw new InvalidOperationException("mock offline");
            if (UpdateOverride != null) return UpdateOverride(id, status, note);
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
}
