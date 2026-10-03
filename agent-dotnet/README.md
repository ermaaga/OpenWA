# OpenWA Enterprise Agent (.NET 10) 🚀

Questo progetto sostituisce il precedente script monolitico in Python con un'architettura **.NET 10 Enterprise-grade**, basata sui principi della *Clean Architecture* e progettata per l'orchestrazione avanzata tramite **Microsoft Semantic Kernel** e il protocollo **MCP (Model Context Protocol)**.

## 🏗️ Struttura dell'Architettura (Clean Architecture)

Il progetto è suddiviso in 4 moduli principali per garantire la massima manutenibilità e isolamento:

1. **`OpenWA.Agent.Core`**: Il cuore del dominio. Contiene esclusivamente Interfacce (contratti) e Modelli dati (DTO come `IncomingWebhookDto` e `McpServerConfig`). Non ha alcuna dipendenza verso librerie esterne.
2. **`OpenWA.Agent.Infrastructure`**: Lo strato implementativo. Qui risiede l'intelligenza vera e propria:
   - **`SemanticKernelOrchestrator`**: Inizializza l'IA (es. NVIDIA Llama) e le fornisce i Tool tramite plugin.
   - **`McpPlugin`**: Implementa nativamente l'interfaccia verso l'MCP di OpenWA per permettere all'LLM di leggere lo storico ed inviare messaggi in completa autonomia.
   - **Iniezione delle dipendenze**: Configura *Polly* per la gestione delle retry policy (es. riprovando in caso di timeout della rete).
3. **`OpenWA.Agent.Api`**: Il punto di ingresso (*Minimal API*). Espone l'endpoint `/webhook` che riceve i messaggi in ingresso da WhatsApp e smista il carico in background per evitare timeout.
4. **`OpenWA.Agent.Storage`**: Progetto predisposto per le implementazioni future (es. Entity Framework Core) per salvare i log delle chat, i lead o le analisi su un database relazionale.

## 🧠 Paradigma Agentic e Multi-MCP

A differenza del vecchio approccio in cui il codice eseguiva manualmente le chiamate REST, questa soluzione è **Agentic**:
- La configurazione `McpServers` permette di passare un **Array** di server MCP (non solo OpenWA, ma in futuro anche Calendar, DB aziendali, ecc.).
- Semantic Kernel (con `FunctionChoiceBehavior.Auto()`) valuta autonomamente la richiesta dell'utente e **decide da solo quali Tool chiamare** (es. `get_chat_history` per recuperare il contesto e `send_message` per rispondere).

> **⚠️ Nota sulle Performance (Latenza vs Autonomia)**:
> L'approccio Agentic (Tool Calling) prevede un "ciclo di ragionamento". L'IA fa chiamate multiple a NVIDIA per decidere l'uso dei tool, attendere il risultato e infine generare il testo. Questo porta a latenze leggermente maggiori (3-6 secondi) rispetto a una singola chiamata REST diretta. Se la velocità di risposta su WhatsApp è critica, è possibile disabilitare il Tool Calling a favore del recupero dello storico effettuato direttamente in C#.

## 📜 Regole Dinamiche (`rules.json`)

Il comportamento del bot non è hardcoded. Il file `rules.json` viene montato come volume Docker e **letto in tempo reale** ad ogni messaggio (senza bisogno di riavviare il container).
Permette di definire:
- Un `"default_system_prompt"` globale.
- Delle `"chat_rules"` specifiche per singolo numero di telefono o ID Chat (utile per configurare prompt diversi per utenti VIP o account business).

## ⚙️ Configurazione (`appsettings.json`)

L'ambiente si configura tramite `appsettings.json` o variabili d'ambiente (Docker `environment`):

```json
"Agent": {
  "LlmProvider": "NVIDIA",
  "ModelId": "meta/llama-3.2-11b-vision-instruct",
  "ApiKey": "LA_TUA_CHIAVE_NVIDIA",
  "LlmEndpoint": "https://integrate.api.nvidia.com/v1",
  "RulesFile": "/app/rules.json",
  "McpServers": [
    {
      "Name": "OpenWA",
      "Endpoint": "http://openwa-api:2785",
      "Token": "Il token di OpenWA (o vuoto se auto-caricato dal file .api-key)"
    }
  ]
}
```

## 🐳 Avvio Rapido (Docker)

Assicurati che la chiave NVIDIA sia impostata correttamente nel file `docker-compose.agent-dotnet.yml`, poi avvia l'ambiente:

```bash
docker compose -f docker-compose.agent-dotnet.yml up -d --build
```

Per consultare i log in tempo reale e vedere le decisioni prese dall'LLM:
```bash
docker logs -f dotnet-agent
```
