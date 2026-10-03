namespace OpenWA.Agent.Core;

public interface IAgentOrchestrator
{
    Task<string> ProcessMessageAsync(IncomingWebhookDto webhook, CancellationToken cancellationToken = default);
}
