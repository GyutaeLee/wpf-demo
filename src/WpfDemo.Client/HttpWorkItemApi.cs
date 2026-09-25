using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;

namespace WpfDemo
{
    public sealed class HttpWorkItemApi : IWorkItemApi
    {
        private const string BaseUrl = "http://127.0.0.1:5187/api/work-items";
        private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };

        public async Task<IReadOnlyList<WorkItem>> GetItemsAsync()
        {
            using (var response = await Client.GetAsync(BaseUrl))
            {
                response.EnsureSuccessStatusCode();
                using (var stream = await response.Content.ReadAsStreamAsync())
                {
                    return (WorkItem[])new DataContractJsonSerializer(typeof(WorkItem[])).ReadObject(stream);
                }
            }
        }

        public async Task<WorkItem> UpdateItemAsync(int id, string status, string note)
        {
            var request = new UpdateItemRequest { Status = status, Note = note };
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(UpdateItemRequest)).WriteObject(stream, request);
                var bytes = stream.ToArray();
                using (var content = new ByteArrayContent(bytes))
                {
                    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                    using (var response = await Client.PutAsync(BaseUrl + "/" + id, content))
                    {
                        response.EnsureSuccessStatusCode();
                        using (var body = await response.Content.ReadAsStreamAsync())
                        {
                            return (WorkItem)new DataContractJsonSerializer(typeof(WorkItem)).ReadObject(body);
                        }
                    }
                }
            }
        }

        [DataContract]
        private sealed class UpdateItemRequest
        {
            [DataMember(Name = "status")]
            public string Status { get; set; }

            [DataMember(Name = "note")]
            public string Note { get; set; }
        }
    }
}
