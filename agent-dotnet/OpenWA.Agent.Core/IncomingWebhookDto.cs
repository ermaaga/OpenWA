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

public class WebhookKey
{
    [JsonPropertyName("fromMe")]
    public bool? FromMe { get; set; }
}

public class WebhookPayload
{
    [JsonPropertyName("fromMe")]
    public bool? FromMe { get; set; }

    [JsonPropertyName("isFromMe")]
    public bool? IsFromMe { get; set; }

    [JsonPropertyName("key")]
    public WebhookKey? Key { get; set; }

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

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("isGroup")]
    public bool? IsGroup { get; set; }

    [JsonPropertyName("timestamp")]
    public long? Timestamp { get; set; }

    [JsonPropertyName("quotedMessage")]
    public WebhookQuotedMessage? QuotedMessage { get; set; }
}

public class WebhookQuotedMessage
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }
}
