namespace OpenWA.Agent.Core;

public class AgentOptions
{
    public string LlmProvider { get; set; } = "OpenAI";
    public string ApiKey { get; set; } = string.Empty;
    public string ModelId { get; set; } = "meta/llama-3.2-11b-vision-instruct";
    public List<McpServerConfig> McpServers { get; set; } = new();
    public string RulesFile { get; set; } = "rules.json";
    public string LlmEndpoint { get; set; } = "https://integrate.api.nvidia.com/v1"; // For NVIDIA or OpenAI compatible
}
