using System.Net.Http.Json;
using System.ComponentModel;
using System.Text.Json;
using Microsoft.SemanticKernel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenWA.Agent.Core;

namespace OpenWA.Agent.Infrastructure;

public class OpenWaMcpPlugin
{
    private readonly HttpClient _mcpHttpClient; // In a real scenario, this wraps the MCP JSON-RPC connection
    private readonly ILogger _logger;
    private readonly string _session;
    private bool _alreadySent = false;

    public OpenWaMcpPlugin(HttpClient httpClient, string session, ILogger logger)
    {
        _mcpHttpClient = httpClient;
        _session = session;
        _logger = logger;
    }

    [KernelFunction("get_chat_history")]
    [Description("Recupera lo storico recente dei messaggi di una specifica chat di WhatsApp per capire il contesto.")]
    public async Task<string> GetChatHistoryAsync(
        [Description("L'ID della chat di WhatsApp (es. numero di telefono con @s.whatsapp.net)")] string chatId,
        [Description("Numero massimo di messaggi da recuperare")] int limit = 6)
    {
        _logger.LogInformation("Tool Call (MCP): get_chat_history per {ChatId}", chatId);
        // Simulazione della chiamata RPC all'MCP Server di OpenWA
        limit = Math.Clamp(limit, 1, 20);
        var url = $"/api/sessions/{_session}/messages?chatId={Uri.EscapeDataString(chatId)}&limit={limit}";
        var response = await _mcpHttpClient.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            return "Nessuna cronologia disponibile.";
        }
        var raw = await response.Content.ReadAsStringAsync();
        var compact = CompactHistory(raw);
        _logger.LogInformation("get_chat_history: {Raw} caratteri da OpenWA, {Compact} passati al modello", raw.Length, compact.Length);
        return compact;
    }

    /// <summary>
    /// OpenWA restituisce i messaggi completi, media inclusi in base64 dentro "metadata"
    /// (una sola foto può pesare centinaia di KB). Al modello passiamo solo quello che
    /// serve per capire la conversazione: ora, mittente, tipo e testo.
    /// </summary>
    public static string CompactHistory(string raw)
    {
        const int MaxBody = 1000;
        const int MaxTotal = 8000;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var messages = root.ValueKind == JsonValueKind.Array ? root
                : root.TryGetProperty("messages", out var m) ? m
                : root.TryGetProperty("data", out var d) ? d
                : default;
            if (messages.ValueKind != JsonValueKind.Array)
            {
                return "Nessuna cronologia disponibile.";
            }

            var items = new List<object>();
            foreach (var msg in messages.EnumerateArray())
            {
                string? Str(string name) =>
                    msg.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

                var type = Str("type") ?? "text";
                var body = Str("body") ?? "";
                if (body.Length > MaxBody) body = body[..MaxBody] + "…";
                var text = type == "text" ? body : $"[{type}]" + (body.Length > 0 ? " " + body : "");
                var from = Str("direction") == "outgoing" ? "me" : (Str("author") ?? Str("from") ?? "contact");
                var time = msg.TryGetProperty("timestamp", out var ts) ? ts.ToString() : Str("createdAt");
                items.Add(new { time, from, text });
            }

            var json = JsonSerializer.Serialize(items);
            // Se anche così è troppo lungo, teniamo solo la parte finale.
            return json.Length <= MaxTotal ? json : "…" + json[^MaxTotal..];
        }
        catch (JsonException)
        {
            return raw.Length <= MaxTotal ? raw : "Nessuna cronologia disponibile.";
        }
    }

    /// <summary>True se in questo turno send_message è già stato chiamato.</summary>
    public bool MessageSent => _alreadySent;

    [KernelFunction("send_message")]
    [Description("Invia un messaggio di testo su WhatsApp a un destinatario specifico.")]
    public async Task<string> SendMessageAsync(
        [Description("L'ID della chat di WhatsApp a cui inviare il messaggio")] string chatId,
        [Description("Il testo del messaggio da inviare")] string text)
    {
        if (_alreadySent)
        {
            _logger.LogWarning("Blocked duplicate send_message call in the same turn for {ChatId}", chatId);
            return "Message already sent. Do not send more messages.";
        }
        _alreadySent = true;

        _logger.LogInformation("Tool Call (MCP): send_message a {ChatId} con testo: {Text}", chatId, text);
        // Simulazione della chiamata RPC all'MCP Server di OpenWA
        var payload = new { chatId, text };
        var url = $"/api/sessions/{_session}/messages/send-text";
        var response = await _mcpHttpClient.PostAsJsonAsync(url, payload);
        response.EnsureSuccessStatusCode();
        
        return "Messaggio inviato con successo.";
    }
}
