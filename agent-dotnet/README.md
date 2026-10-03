# OpenWA Enterprise Agent (.NET 10)

Questo è il servizio pluggable in .NET 10 (strutturato con *Clean Architecture*) per sostituire l'attuale agente Python.

## Struttura

- **OpenWA.Agent.Core**: Modelli (DTO) e Interfacce. Niente dipendenze esterne.
- **OpenWA.Agent.Infrastructure**: Implementazione concreta. Contiene Microsoft Semantic Kernel e il Client WhatsApp con policy di retry (Polly).
- **OpenWA.Agent.Storage**: (In lavorazione) Qui metteremo Entity Framework Core per salvare lo stato su DB (es. SQLite o PostgreSQL).
- **OpenWA.Agent.Api**: Il punto d'ingresso (Minimal API). Legge la configurazione e definisce l'endpoint Webhook.

## Configurazione

Puoi usare il file `appsettings.json` o variabili d'ambiente (grazie al sistema di configurazione nativo di .NET).

Esempio variabili d'ambiente:
- `Agent__ApiKey`="la-tua-chiave-openai"
- `Agent__OpenWaEndpoint`="http://url-openwa"

## Integrazione MCP e DB

Attualmente l'agente usa **Semantic Kernel** per l'orchestrazione. Il codice è già pronto (`SemanticKernelOrchestrator.cs`) per accogliere i tool MCP passando la lista di plugin al Kernel. In futuro lo storage dei thread verrà implementato in `Storage`.

## Test

È stato incluso un progetto di test unitari basato su **xUnit** e **Moq** (`OpenWA.Agent.Tests`).
I test coprono attualmente:
- La serializzazione corretta e l'uso di HttpMessageHandler nel `WhatsAppClient`.
- Il mock di Semantic Kernel nell'`IAgentOrchestrator` per verificare che la cronologia venga processata senza chiamare le API vere.

Per lanciare i test, basterà eseguire:
```bash
dotnet test OpenWA.Agent.Tests
```
