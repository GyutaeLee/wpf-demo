using System.Collections.Generic;
using System.Threading.Tasks;

namespace WpfDemo
{
    public interface IWorkItemApi
    {
        Task<IReadOnlyList<WorkItem>> GetItemsAsync();
        Task<WorkItem> UpdateItemAsync(int id, string status, string note);
    }
}
