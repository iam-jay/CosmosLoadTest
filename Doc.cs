using Newtonsoft.Json;

namespace CosmosLoadTest;

// Document model stored in Cosmos. "id" and "pk" are required by the container.
public class Doc
{
    [JsonProperty("id")] public string Id { get; set; }
    [JsonProperty("pk")] public string Pk { get; set; }
    [JsonProperty("category")] public string Category { get; set; }
    [JsonProperty("seq")] public long Seq { get; set; }
    [JsonProperty("ts")] public long Ts { get; set; }
    // Filler padded so the serialized document hits the configured size.
    [JsonProperty("data")] public string Data { get; set; }
    // Set/updated by Patch and Replace operations.
    [JsonProperty("updatedAt")] public long UpdatedAt { get; set; }
}
