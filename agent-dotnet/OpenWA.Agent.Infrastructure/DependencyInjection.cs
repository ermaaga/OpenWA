using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using OpenWA.Agent.Core;
using OpenWA.Agent.Infrastructure.Triage;
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
        services.AddSingleton<SystemPromptProvider>();

        // Triage workflow (summaries to the owner, GitHub issues)
        services.AddSingleton(new TriageStore(options.TriageStateFile));
        services.AddSingleton<TriageSettingsProvider>();
        services.AddSingleton<WhatsAppSender>();
        services.AddSingleton<GitHubIssues>();
        services.AddSingleton<TriageService>();
        services.AddHttpClient("GitHub", client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("openwa-agent");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // Registra un client HTTP per ogni server MCP configurato
        foreach (var server in options.McpServers)
        {
            services.AddHttpClient($"McpClient_{server.Name}", client => 
            {
                client.BaseAddress = new Uri(server.Endpoint);
                
                var apiKey = server.Token;
                if (string.IsNullOrEmpty(apiKey) && server.Name == "OpenWA" && File.Exists("/app/data/.api-key"))
                {
                    try { apiKey = File.ReadAllText("/app/data/.api-key").Trim(); } catch { }
                }
                
                if (!string.IsNullOrEmpty(apiKey))
                {
                    // L'header cambia a seconda del server? Per ora usiamo X-API-Key (o Bearer)
                    client.DefaultRequestHeaders.Add("X-API-Key", apiKey);
                }
            }).AddTransientHttpErrorPolicy(policyBuilder =>
                    policyBuilder.WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))));
        }

        return services;
    }
}
