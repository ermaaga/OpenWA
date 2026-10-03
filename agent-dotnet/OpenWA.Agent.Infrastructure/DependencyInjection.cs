using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using OpenWA.Agent.Core;
using Polly;

namespace OpenWA.Agent.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddAgentInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AgentOptions>(configuration.GetSection("Agent"));

        var options = configuration.GetSection("Agent").Get<AgentOptions>() ?? new AgentOptions();

        // Configure Semantic Kernel
        var kernelBuilder = Kernel.CreateBuilder();
        
        if (options.LlmProvider == "OpenAI" || options.LlmProvider == "NVIDIA")
        {
            #pragma warning disable SKEXP0070, SKEXP0010
            kernelBuilder.AddOpenAIChatCompletion(
                modelId: options.ModelId,
                apiKey: options.ApiKey,
                endpoint: new Uri(options.LlmEndpoint)
            );
            #pragma warning restore SKEXP0070, SKEXP0010
        }
        
        services.AddSingleton(kernelBuilder.Build());
        services.AddTransient<IAgentOrchestrator, SemanticKernelOrchestrator>();

        // Configura il client HTTP "MCP" generico verso OpenWA
        services.AddHttpClient("McpClient", client => 
        {
            client.BaseAddress = new Uri(options.McpEndpoint);
            
            var apiKey = options.McpToken;
            if (string.IsNullOrEmpty(apiKey) && File.Exists("/app/data/.api-key"))
            {
                try { apiKey = File.ReadAllText("/app/data/.api-key").Trim(); } catch { }
            }
            if (!string.IsNullOrEmpty(apiKey))
            {
                client.DefaultRequestHeaders.Add("X-API-Key", apiKey);
            }
        }).AddTransientHttpErrorPolicy(policyBuilder =>
                policyBuilder.WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))));

        return services;
    }
}
