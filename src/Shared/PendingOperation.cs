using System.Runtime.Serialization;

namespace WpfDemo
{
    [DataContract]
    public sealed class PendingOperation
    {
        [DataMember(Name = "operationId", Order = 1)] public string OperationId { get; set; }
        [DataMember(Name = "kind", Order = 2)] public string Kind { get; set; }
        [DataMember(Name = "equipmentId", Order = 3)] public int EquipmentId { get; set; }
        [DataMember(Name = "datasetId", Order = 4)] public string DatasetId { get; set; }
        [DataMember(Name = "apiAddress", Order = 5)] public string ApiAddress { get; set; }
        [DataMember(Name = "relativePath", Order = 6)] public string RelativePath { get; set; }
        [DataMember(Name = "bodyJson", Order = 7)] public string BodyJson { get; set; }
    }
}
