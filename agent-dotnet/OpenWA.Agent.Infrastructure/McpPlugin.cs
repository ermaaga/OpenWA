using System.Net.Http.Json;
using System.ComponentModel;
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
        var url = $"/api/sessions/{_session}/messages?chatId={chatId}&limit={limit}";
        var response = await _mcpHttpClient.GetAsync(url);
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadAsStringAsync();
        }
        return "Nessuna cronologia disponibile.";
    }

    [KernelFunction("send_message")]
    [Description("Invia un messaggio di testo su WhatsApp a un destinatario specifico.")]
    public async Task<string> SendMessageAsync(
        [Description("L'ID della chat di WhatsApp a cui inviare il messaggio")] string chatId,
        [Description("Il testo del messaggio da inviare")] string text)
    {
        _logger.LogInformation("Tool Call (MCP): send_message a {ChatId} con testo: {Text}", chatId, text);
        // Simulazione della chiamata RPC all'MCP Server di OpenWA
        var payload = new { chatId, text };
        var url = $"/api/sessions/{_session}/messages/send-text";
        var response = await _mcpHttpClient.PostAsJsonAsync(url, payload);
        response.EnsureSuccessStatusCode();
        
        return "Messaggio inviato con successo.";
    }
}
