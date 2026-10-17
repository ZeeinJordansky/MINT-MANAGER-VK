using System.Text.Json.Serialization;

namespace MintVkBot.Models;

public class VkCallbackDto
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("group_id")]
    public long GroupId { get; set; }

    [JsonPropertyName("secret")]
    public string? Secret { get; set; }

    [JsonPropertyName("object")]
    public System.Text.Json.JsonElement? Object { get; set; }
}
