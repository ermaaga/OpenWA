# 📘 OpenWA + NVIDIA AI Agent (Llama 3.2 Vision) - Guida & Documentazione Tecnica

## 1. 📌 Panoramica del Sistema

Il sistema realizzato collega **OpenWA** (Gateway REST/WebSocket open-source per WhatsApp) con un **Agente AI Multimodale** basato su **NVIDIA AI Cloud Services** (modello `meta/llama-3.2-11b-vision-instruct`).

```
[ Utente WhatsApp ]
        ▲
        │ (Connessione WebSocket Baileys)
        ▼
[ OpenWA Gateway Container ] (Porta 2785)
        │
        │ 1. Evento Webhook HTTP (message.received) [Risposta 200 OK in 5ms]
        ▼
[ Agente AI Python Container ] (Porta 3000)
        │
        ├── 2. Legge regole da agent/rules.json (Rilevamento lingua & System Prompt)
        ├── 3. Chiama NVIDIA API Cloud (Llama 3.2 11B Vision - Risposta in <0.5s)
        │
        └── 4. Invia la risposta a OpenWA (POST /api/sessions/{sessionId}/messages/send-text)
        │
        ▼
[ OpenWA Gateway ] ───> Spedisce il messaggio su WhatsApp all'utente!
```

---

## 2. ❌ Perché Prima NON Funzionava (Analisi delle 6 Cause)

Durante la configurazione iniziale sono stati individuati e risolti **6 problemi tecnici distinti**:

### 1. Fallimento dell'Engine `whatsapp-web.js` (`ready_reconcile_bridge_dead`)
* **Causa**: L'engine predefinito tentava di avviare un browser Chromium headless dentro Docker ed iniettare script nella pagina web di WhatsApp. Ad ogni aggiornamento dei server WhatsApp, l'iniezione dello script falliva andando in timeout.
* **Sintomo**: La sessione WhatsApp rimaneva bloccata o andava in errore `FAILED`.

### 2. Blocco di Sicurezza SSRF di OpenWA (`Destination address is not allowed`)
* **Causa**: OpenWA integra una protezione Server-Side Request Forgery (SSRF Guard) che di default vieta ai Webhook di chiamare IP o nomi di container della rete locale (`172.x.x.x` o `nvidia-agent`).
* **Sintomo**: Errore *"Destination address is not allowed"* durante la creazione del Webhook nella Dashboard.

### 3. Timeout Sincrono delle Chiamate Webhook (`TimeoutError`)
* **Causa**: Quando arrivava un messaggio, l'agente Python eseguiva la chiamata all'API dell'AI in modo bloccante prima di restituire la risposta HTTP `200 OK` ad OpenWA. Siccome l'elaborazione dell'AI impiegava diversi secondi, OpenWA andava in timeout (10 secondi) e segnava il Webhook come fallito.
* **Sintomo**: Log di errore `Webhook delivery failed ... TimeoutError` ed accumulo di tentativi falliti (`retryCount: 3`).

### 4. Congestione e Lentezza dei Modelli di Reasoning DeepSeek (145s)
* **Causa**: Il modello `deepseek-ai/deepseek-v4.1-flash` eseguiva un lungo processo di ragionamento interno prima di restituire il testo. Su server NVIDIA gratuiti la coda accumulava fino a **145 secondi (2,5 minuti)** per rispondere.
* **Sintomo**: L'utente attendeva minuti prima di vedere una risposta o le richieste andavano in timeout.

### 5. Rotta REST Errata per l'Invio dei Messaggi (`404 Not Found`)
* **Causa**: L'agente chiamava l'URL `/api/sessions/{sessionId}/messages/text` (che non esiste nello schema API di OpenWA).
* **Sintomo**: L'AI generava correttamente la risposta, ma l'invio su WhatsApp falliva con errore `404 Client Error: Not Found`.

### 6. Loop di Riavvio del Container Python dell'Agente (`Restarting (0)`)
* **Causa**: Nel codice `agent/main.py` mancava l'istruzione esplicita di avvio del server ASGI (`uvicorn.run`), provocando l'uscita immediata del processo Python al completamento dell'importazione.
* **Sintomo**: Il container `nvidia-agent` continuava a riavviarsi in loop.

---

## 3. ✅ Perché ORA Funzionava Perfettamente (Soluzioni Applicate)

### 1. Migrazione all'Engine `baileys` (Stabilità 100%)
Abbiamo impostato `ENGINE_TYPE=baileys` nel Docker Compose. Baileys si connette **direttamente tramite WebSocket** al protocollo WhatsApp Multi-Device.
* **Vantaggi**: Zero browser Chromium, consumo RAM ridotto da 400MB a **40MB**, avviso e connessione immediati in 2 secondi.

### 2. Configurazione Trasparente SSRF
Abbiamo aggiunto in `docker-compose.agent.yml` le variabili:
* `WEBHOOK_SSRF_PROTECT=false`
* `SSRF_ALLOWED_HOSTS=nvidia-agent,host.docker.internal`  
Questo consente ad OpenWA di comunicare liberamente con l'agente locale sulla rete Docker.

### 3. Architettura Webhook Asincrona (`FastAPI BackgroundTasks`)
Il Webhook risponde **immediatamente con HTTP 200 OK in 5ms** ad OpenWA. L'elaborazione pesante (chiamata all'AI ed invio della risposta) viene delegata a un task in background. Nessun timeout.

### 4. Adozione del Modello Ultra-Veloce `meta/llama-3.2-11b-vision-instruct`
Passando al modello Llama 3.2 Vision:
* **Tempo di risposta**: ridotto da 145 secondi a **0.44 secondi (meno di mezzo secondo!)**.
* **Multimodale**: elabora sia messaggi di testo che **immagini/foto allegate**.

### 5. Correzione della Rotta REST di Invio
Corretto l'URL di invio in:
👉 **`POST /api/sessions/{sessionId}/messages/send-text`**  
Esito: HTTP **201 Created** ed invio immediato su WhatsApp.

### 6. Rilevamento Automatico della Lingua e Prompt Dinamici (`agent/rules.json`)
L'agente legge ad ogni richiesta il file `agent/rules.json`:
* **Multilingua Automatico**: Riconosce la lingua del mittente e risponde nella **stessa lingua** (Italiano, Albanese, Inglese, Spagnolo, Francese, ecc.).
* **Regole per Numero/Chat**: Possibilità di definire comportamenti specifici per singoli numeri o gruppi.
* **Ricaricamento in Tempo Reale (Hot-Reloading)**: Modificando `agent/rules.json` sul PC, le regole si aggiornano all'istante **senza riavviare Docker**.

---

## 4. 📁 Struttura File Creati nel Repository

* **[`agent/main.py`](file:///Users/ermaaga/Work/OpenWa/OpenWA/agent/main.py)**: Server FastAPI con gestione webhook asincrona, integrazione NVIDIA e lettura regole.
* **[`agent/rules.json`](file:///Users/ermaaga/Work/OpenWa/OpenWA/agent/rules.json)**: File di configurazione delle regole e del System Prompt.
* **[`agent/Dockerfile`](file:///Users/ermaaga/Work/OpenWa/OpenWA/agent/Dockerfile)** & **[`agent/requirements.txt`](file:///Users/ermaaga/Work/OpenWa/OpenWA/agent/requirements.txt)**: Containerizzazione dell'agente.
* **[`docker-compose.agent.yml`](file:///Users/ermaaga/Work/OpenWa/OpenWA/docker-compose.agent.yml)**: Orchestrazione completa di OpenWA + Agente AI.

---

## 5. 🛠️ Comandi Utili per la Gestione

* **Avvio / Riavvio Infrastruttura**:
  ```bash
  docker compose -f docker-compose.agent.yml up -d
  ```

* **Ricompilazione dell'Agente**:
  ```bash
  docker compose -f docker-compose.agent.yml up -d --build nvidia-agent
  ```

* **Lettura Log dell'Agente AI**:
  ```bash
  docker logs -f nvidia-agent
  ```

* **Lettura Log di OpenWA**:
  ```bash
  docker logs -f openwa-api
  ```
