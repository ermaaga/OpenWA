using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenWA.Agent.Core;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace OpenWA.Agent.Infrastructure;

public class SemanticKernelOrchestrator : IAgentOrchestrator
{
    private readonly Kernel _kernel;
    private readonly AgentOptions _options;
    private readonly ILogger<SemanticKernelOrchestrator> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private const string DefaultPrompt = "Sei un assistente virtuale su WhatsApp. Rispondi in italiano in modo chiaro e sintetico. Usa gli strumenti (tools) a tua disposizione per inviare messaggi o leggere lo storico.";

    public SemanticKernelOrchestrator(Kernel kernel, IOptions<AgentOptions> options, IHttpClientFactory httpClientFactory, ILogger<SemanticKernelOrchestrator> logger)
    {
        _kernel = kernel;
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private string GetSystemPrompt(string chatId)
    {
        var defaultPrompt = DefaultPrompt;
        if (File.Exists(_options.RulesFile))
        {
            try 
            {
                var content = File.ReadAllText(_options.RulesFile);
                var rules = JsonSerializer.Deserialize<JsonElement>(content);
                
                if (rules.TryGetProperty("default_system_prompt", out var defaultProp))
                {
                    defaultPrompt = defaultProp.GetString() ?? defaultPrompt;
                }
                
                if (rules.TryGetProperty("chat_rules", out var chatRules))
                {
                    if (chatRules.TryGetProperty(chatId, out var exactMatch))
                    {
                        return exactMatch.GetString() ?? defaultPrompt;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse rules file");
            }
        }
        return defaultPrompt;
    }

    public async Task<string> ProcessMessageAsync(IncomingWebhookDto webhook, CancellationToken cancellationToken = default)
    {
        var session = webhook.Session ?? webhook.SessionId ?? "default";
        var payload = webhook.Payload ?? webhook.Data;
        
        if (payload == null || payload.FromMe == true)
        {
            return string.Empty;
        }

        var chatId = payload.From ?? payload.ChatId ?? payload.Author ?? string.Empty;
        var textContent = payload.Body ?? payload.Text ?? payload.Caption ?? string.Empty;
        
        if (string.IsNullOrEmpty(chatId) || string.IsNullOrEmpty(textContent))
        {
            return string.Empty;
        }

        _logger.LogInformation("Inizializzo LLM con MCP Tools per la sessione {Session}", session);

        // 1. Clona il kernel per isolare la conversazione e aggiungere l'MCP Plugin dinamico
        var chatKernel = _kernel.Clone();
        var mcpHttpClient = _httpClientFactory.CreateClient("McpClient");
        var mcpPlugin = new OpenWaMcpPlugin(mcpHttpClient, session, _logger);
        chatKernel.Plugins.AddFromObject(mcpPlugin, "OpenWA_MCP");

        // 2. Prepara il prompt
        var systemPrompt = GetSystemPrompt(chatId);
        var chatHistory = new ChatHistory(systemPrompt);
        
        // Istruisce l'LLM sul nuovo evento
        var userPrompt = $"Ho appena ricevuto questo messaggio da {chatId}: '{textContent}'. Usa il tool 'get_chat_history' per leggere i messaggi precedenti e poi rispondi usando il tool 'send_message'.";
        chatHistory.AddUserMessage(userPrompt);

        // 3. Esegue l'LLM attivando il Tool Calling automatico (MCP)
        var chatCompletionService = chatKernel.GetRequiredService<IChatCompletionService>();
        
        var settings = new PromptExecutionSettings 
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
        };

        // L'LLM farà tutto da solo: chiamerà get_chat_history e poi send_message!
        await chatCompletionService.GetChatMessageContentAsync(
            chatHistory,
            executionSettings: settings,
            kernel: chatKernel,
            cancellationToken: cancellationToken
        );

        return string.Empty;
    }
}
