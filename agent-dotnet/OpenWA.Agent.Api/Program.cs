using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenWA.Agent.Core;
using OpenWA.Agent.Infrastructure;
using OpenWA.Agent.Infrastructure.Triage;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAgentInfrastructure(builder.Configuration);
builder.Services.AddHostedService<TriageWorker>();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new { status = "ok", model = builder.Configuration["Agent:ModelId"] }));

app.MapPost("/webhook", (IncomingWebhookDto webhook, IAgentOrchestrator orchestrator, TriageService triage, ILogger<Program> logger) =>
{
    var eventName = webhook.Event ?? "";
    var session = webhook.Session ?? webhook.SessionId ?? "default";
    
    logger.LogInformation("Webhook Received Event: '{Event}' | Session: '{Session}'", eventName, session);
    
    if (eventName != "message" && !eventName.StartsWith("message."))
    {
         return Results.Ok(new { status = "ignored" });
    }
    
    var payload = webhook.Payload ?? webhook.Data;
    var isFromMe = payload?.FromMe == true || payload?.IsFromMe == true || payload?.Key?.FromMe == true;
    if (isFromMe || eventName.Contains("ack"))
    {
         return Results.Ok(new { status = "ignored" });
    }

    var chatId = payload?.From ?? payload?.ChatId ?? payload?.Author ?? "";
    if (string.IsNullOrEmpty(chatId))
    {
         return Results.Json(new { status = "error", message = "Missing sender_chat_id" }, statusCode: 400);
    }
    
    // Triage workflow: store, acknowledge and summarise for the owner instead of replying directly
    if (triage.IsEnabled)
    {
        _ = Task.Run(async () => {
            try {
                await triage.HandleIncomingAsync(session, payload!);
            } catch (Exception ex) {
                logger.LogError(ex, "Error in triage workflow");
            }
        });
        return Results.Ok(new { status = "triage" });
    }

    // Background execution to avoid webhook timeout
    _ = Task.Run(async () => {
        try {
            await orchestrator.ProcessMessageAsync(webhook);
        } catch (Exception ex) {
            logger.LogError(ex, "Error processing message in background");
        }
    });
    
    return Results.Ok(new { status = "success" });
});

app.Run();
