using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenWA.Agent.Core;

namespace OpenWA.Agent.Infrastructure;

/// <summary>Resolves the system prompt for a chat from rules.json (re-read on every call).</summary>
public sealed class SystemPromptProvider
{
    public const string DefaultPrompt = "Sei un assistente virtuale su WhatsApp. Rispondi in italiano in modo chiaro e sintetico. Usa gli strumenti (tools) a tua disposizione per inviare messaggi o leggere lo storico.";

    private readonly AgentOptions _options;
    private readonly ILogger<SystemPromptProvider> _logger;

    public SystemPromptProvider(IOptions<AgentOptions> options, ILogger<SystemPromptProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public string GetSystemPrompt(string chatId)
    {
        var defaultPrompt = DefaultPrompt;

        // Risolvi il path del rules.json cercando in diverse posizioni
        var candidatePaths = new[]
        {
            _options.RulesFile,
            Path.Combine(AppContext.BaseDirectory, "rules.json"),
            "rules.json",
            "agent-dotnet/rules.json",
            "/app/rules.json"
        };

        var rulesPath = candidatePaths.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));

        if (!string.IsNullOrEmpty(rulesPath))
        {
            try
            {
                var content = File.ReadAllText(rulesPath);
                var rules = JsonSerializer.Deserialize<JsonElement>(content);

                if (rules.TryGetProperty("default_system_prompt", out var defaultProp))
                {
                    defaultPrompt = defaultProp.GetString() ?? defaultPrompt;
                }

                if (rules.TryGetProperty("chat_rules", out var chatRules))
                {
                    var cleanChatId = ExtractDigits(chatId);

                    foreach (var prop in chatRules.EnumerateObject())
                    {
                        var ruleKey = prop.Name;
                        var cleanRuleKey = ExtractDigits(ruleKey);

                        // Match esatto, per cifre (es. 391234567890) o per sottostringa
                        if (ruleKey.Equals(chatId, StringComparison.OrdinalIgnoreCase) ||
                            (!string.IsNullOrEmpty(cleanChatId) && cleanRuleKey.Equals(cleanChatId, StringComparison.OrdinalIgnoreCase)) ||
                            (!string.IsNullOrEmpty(cleanRuleKey) && chatId.Contains(cleanRuleKey)))
                        {
                            _logger.LogInformation("Matched system prompt rule key '{RuleKey}' for chatId '{ChatId}' from '{Path}'", ruleKey, chatId, rulesPath);
                            return prop.Value.GetString() ?? defaultPrompt;
                        }
                    }
                }
                _logger.LogInformation("No specific chat rule matched for '{ChatId}'. Using default prompt from '{Path}'", chatId, rulesPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse rules file from {Path}", rulesPath);
            }
        }
        else
        {
            _logger.LogWarning("Rules file not found in any candidate path ({Paths}). Using hardcoded fallback.", string.Join(", ", candidatePaths));
        }

        return defaultPrompt;
    }

    private static string ExtractDigits(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        var noSuffix = input.Split('@')[0];
        return new string(noSuffix.Where(char.IsDigit).ToArray());
    }
}
