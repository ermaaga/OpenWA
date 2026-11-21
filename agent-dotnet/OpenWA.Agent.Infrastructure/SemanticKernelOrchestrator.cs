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
    private readonly SystemPromptProvider _prompts;

    public SemanticKernelOrchestrator(Kernel kernel, IOptions<AgentOptions> options, IHttpClientFactory httpClientFactory,
        SystemPromptProvider prompts, ILogger<SemanticKernelOrchestrator> logger)
    {
        _kernel = kernel;
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _prompts = prompts;
        _logger = logger;
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

        // 1. Clona il kernel per isolare la conversazione e aggiungere i Plugin MCP
        var chatKernel = _kernel.Clone();
        OpenWaMcpPlugin? openWa = null;

        foreach (var mcpServer in _options.McpServers)
        {
            var mcpHttpClient = _httpClientFactory.CreateClient($"McpClient_{mcpServer.Name}");
            
            // Attualmente simuliamo i plugin hardcoded, ma con una libreria MCP vera qui si farebbe un connect
            // dinamico che scarica la lista dei tool JSON-RPC esposti.
            if (mcpServer.Name == "OpenWA")
            {
                var openWaPlugin = new OpenWaMcpPlugin(mcpHttpClient, session, _logger);
                openWa = openWaPlugin;
                chatKernel.Plugins.AddFromObject(openWaPlugin, "OpenWA_MCP");
                _logger.LogInformation("Caricato MCP Plugin per: {Name}", mcpServer.Name);
            }
            else
            {
                // In futuro: GenericMcpPlugin.Connect(mcpHttpClient);
                _logger.LogInformation("Trovato configurazione MCP aggiuntiva: {Name} (predisposta per il futuro)", mcpServer.Name);
            }
        }

        // 2. Prepara il prompt
        var systemPrompt = _prompts.GetSystemPrompt(chatId);
        var chatHistory = new ChatHistory(systemPrompt);
        
        // Istruisce l'LLM sul nuovo evento in modo neutrale per evitare interferenze di lingua
        var userPrompt = $"New message received from {chatId}: '{textContent}'.\nTask: If needed, use 'get_chat_history' (chatId='{chatId}') for context, then call 'send_message' EXACTLY ONCE to reply to '{chatId}'. Follow your system prompt persona and language rules strictly.";
        chatHistory.AddUserMessage(userPrompt);

        // 3. Esegue l'LLM attivando il Tool Calling automatico (MCP)
        var chatCompletionService = chatKernel.GetRequiredService<IChatCompletionService>();
        
        var settings = new PromptExecutionSettings 
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
        };

        // L'LLM farà tutto da solo: chiamerà get_chat_history e poi send_message!
        var result = await chatCompletionService.GetChatMessageContentAsync(
            chatHistory,
            executionSettings: settings,
            kernel: chatKernel,
            cancellationToken: cancellationToken
        );
        var finalText = result.Content?.Trim() ?? string.Empty;

        if (openWa is null || openWa.MessageSent)
        {
            _logger.LogInformation("Turno concluso per {ChatId}: risposta inviata con send_message", chatId);
            return string.Empty;
        }

        // Il modello ha chiuso con un testo senza chiamare send_message: prima andava perso
        // in silenzio. Lo inviamo noi, così il contatto riceve comunque la risposta.
        if (string.IsNullOrEmpty(finalText))
        {
            _logger.LogWarning("Turno concluso per {ChatId} senza risposta: il modello non ha chiamato send_message né restituito testo", chatId);
            return string.Empty;
        }
        _logger.LogWarning("Il modello non ha chiamato send_message per {ChatId}: invio il suo testo finale come risposta", chatId);
        await openWa.SendMessageAsync(chatId, finalText);
        return finalText;
    }
}
