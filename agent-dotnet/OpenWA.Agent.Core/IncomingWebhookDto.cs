using System.Text.Json.Serialization;

namespace OpenWA.Agent.Core;

public class IncomingWebhookDto
{
    [JsonPropertyName("event")]
    public string? Event { get; set; }

    [JsonPropertyName("session")]
    public string? Session { get; set; }
    
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    [JsonPropertyName("payload")]
    public WebhookPayload? Payload { get; set; }
    
    [JsonPropertyName("data")]
    public WebhookPayload? Data { get; set; }
}

public class WebhookPayload
{
    [JsonPropertyName("fromMe")]
    public bool? FromMe { get; set; }

    [JsonPropertyName("from")]
    public string? From { get; set; }
    
    [JsonPropertyName("chatId")]
    public string? ChatId { get; set; }
    
    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }
    
    [JsonPropertyName("text")]
    public string? Text { get; set; }
    
    [JsonPropertyName("caption")]
    public string? Caption { get; set; }
}
