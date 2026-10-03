import os
import json
import time
import requests
from fastapi import FastAPI, Request, BackgroundTasks

app = FastAPI(title="OpenWA NVIDIA Agent")

# Configuration
OPENWA_URL = os.getenv("OPENWA_URL", "http://openwa-api:2785")
NVIDIA_API_KEY = os.getenv("NVIDIA_API_KEY", "")
NVIDIA_URL = "https://integrate.api.nvidia.com/v1/chat/completions"
MODEL_NAME = os.getenv("NVIDIA_MODEL", "meta/llama-3.2-11b-vision-instruct")
RULES_FILE = os.getenv("RULES_FILE", "rules.json")

def get_openwa_api_key():
    # 1. Environment variable
    env_key = os.getenv("OPENWA_API_KEY")
    if env_key:
        return env_key.strip()
    
    # 2. Try reading from shared data volume .api-key file
    key_file = "/app/data/.api-key"
    if os.path.exists(key_file):
        try:
            with open(key_file, "r") as f:
                return f.read().strip()
        except Exception as e:
            print(f"Warning: Could not read {key_file}: {e}")
    return ""

DEFAULT_SYSTEM_PROMPT = "Sei un assistente virtuale utile, gentile e professionale. Rispondi in italiano in modo chiaro e sintetico."

def load_rules():
    """Carica dinamicamente le regole da rules.json in tempo reale."""
    if os.path.exists(RULES_FILE):
        try:
            with open(RULES_FILE, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception as e:
            print(f"⚠️ Error reading {RULES_FILE}: {e}")
    return {
        "default_system_prompt": DEFAULT_SYSTEM_PROMPT,
        "chat_rules": {}
    }

def get_system_prompt_for_chat(chat_id: str) -> str:
    """Restituisce il prompt personalizzato per la chat o quello di default dal file rules.json."""
    rules = load_rules()
    default_prompt = rules.get("default_system_prompt", DEFAULT_SYSTEM_PROMPT)
    chat_rules = rules.get("chat_rules", {})
    
    if chat_id in chat_rules:
        print(f"🎯 Applicata regola personalizzata per la chat {chat_id}")
        return chat_rules[chat_id]
    
    phone_number = chat_id.split("@")[0] if chat_id else ""
    if phone_number:
        for key, prompt in chat_rules.items():
            if phone_number in key:
                print(f"🎯 Applicata regola personalizzata per il numero {phone_number}")
                return prompt
            
    return default_prompt

def fetch_chat_history(session: str, chat_id: str, limit: int = 6):
    """Recupera gli ultimi N messaggi scambiati in questa chat per dare memoria contestuale all'AI."""
    api_key = get_openwa_api_key()
    headers = {"X-API-Key": api_key}
    url = f"{OPENWA_URL}/api/sessions/{session}/messages"
    params = {"chatId": chat_id, "limit": limit}
    
    history_messages = []
    try:
        res = requests.get(url, headers=headers, params=params, timeout=5)
        if res.status_code == 200:
            items = res.json().get("items", [])
            # Reversiamo la lista per avere ordine cronologico (dal meno recente al più recente)
            items.reverse()
            for msg in items:
                body = msg.get("body", "").strip()
                if not body:
                    continue
                role = "assistant" if msg.get("fromMe") else "user"
                history_messages.append({"role": role, "content": body})
            print(f"📚 Memoria chat caricata ({len(history_messages)} messaggi precedenti)")
    except Exception as e:
        print(f"⚠️ Impossibile recuperare la memoria chat da OpenWA: {e}")
        
    return history_messages

def process_and_reply(session: str, sender_chat_id: str, text_content: str, media: dict):
    """Elaborazione asincrona in background per evitare il timeout del Webhook."""
    system_prompt = get_system_prompt_for_chat(sender_chat_id)
    
    # 1. Recupera lo storico recente della chat (Memoria Conversazionale)
    history = fetch_chat_history(session, sender_chat_id, limit=6)
    
    # 2. Costruisce la lista di messaggi per l'LLM con Memoria
    messages_payload = [
        {"role": "system", "content": system_prompt}
    ]
    
    # Aggiungiamo i messaggi storici precedenti (escludendo l'ultimo se è già il messaggio corrente)
    for h_msg in history:
        # Se il contenuto è identico al messaggio attuale appena arrivato, lo saltiamo per non duplicarlo
        if h_msg["role"] == "user" and h_msg["content"] == text_content:
            continue
        messages_payload.append(h_msg)
    
    # Costruiamo il contenuto del messaggio attuale (con eventuale immagine)
    content_list = []
    if text_content:
        content_list.append({"type": "text", "text": text_content})
    elif not media:
        content_list.append({"type": "text", "text": "Ciao!"})
        
    if media and isinstance(media, dict) and media.get("data"):
        mimetype = media.get("mimetype", "image/jpeg")
        base64_data = media.get("data")
        data_url = f"data:{mimetype};base64,{base64_data}"
        content_list.append({
            "type": "image_url",
            "image_url": {"url": data_url}
        })
        print(f"📸 Immagine allegata: {mimetype}")

    messages_payload.append({
        "role": "user",
        "content": content_list
    })

    if not NVIDIA_API_KEY:
        print("⚠️ ERROR: NVIDIA_API_KEY is missing!")
        return

    headers = {
        "Content-Type": "application/json",
        "Authorization": f"Bearer {NVIDIA_API_KEY}"
    }
    
    nvidia_payload = {
        "model": MODEL_NAME,
        "messages": messages_payload,
        "temperature": 0.7,
        "max_tokens": 1024
    }
    
    try:
        print(f"🧠 Calling NVIDIA LLM ({MODEL_NAME}) con memoria per {sender_chat_id}...")
        res = requests.post(NVIDIA_URL, json=nvidia_payload, headers=headers, timeout=60)
        res.raise_for_status()
        res_data = res.json()
        
        msg_obj = res_data["choices"][0]["message"]
        ai_reply = msg_obj.get("content") or msg_obj.get("reasoning_content") or "Ciao! Come posso aiutarti?"
        print(f"🤖 NVIDIA Reply ({len(ai_reply)} chars): {ai_reply}")

        api_key = get_openwa_api_key()
        openwa_headers = {
            "Content-Type": "application/json",
            "X-API-Key": api_key
        }
        
        send_url = f"{OPENWA_URL}/api/sessions/{session}/messages/send-text"
        send_body = {
            "chatId": sender_chat_id,
            "text": ai_reply
        }
        
        print(f"📤 Sending reply to OpenWA -> {send_url}")
        send_res = requests.post(send_url, json=send_body, headers=openwa_headers, timeout=30)
        send_res.raise_for_status()
        print("✅ Reply successfully sent to WhatsApp!")

    except Exception as e:
        print(f"❌ Error processing message: {e}")

@app.get("/")
def health_check():
    return {"status": "ok", "model": MODEL_NAME}

@app.post("/webhook")
async def handle_webhook(request: Request, background_tasks: BackgroundTasks):
    data = await request.json()
    
    event = str(data.get("event", ""))
    session = data.get("session") or data.get("sessionId", "default")
    
    # Extract payload flexibly
    payload = data.get("payload") if isinstance(data.get("payload"), dict) else (data.get("data") if isinstance(data.get("data"), dict) else data)
    
    print(f"🔔 [Webhook Received] Event: '{event}' | Session: '{session}'")
    
    if payload.get("fromMe") or "ack" in event:
        print("⏭️ Ignored: Message sent by me or ACK event")
        return {"status": "ignored"}
    
    if not (event == "message" or event.startswith("message.")):
        print(f"⏭️ Ignored non-message event: '{event}'")
        return {"status": "ignored"}
    
    sender_chat_id = payload.get("from") or payload.get("chatId") or payload.get("author") or ""
    text_content = payload.get("body") or payload.get("text") or payload.get("caption") or ""
    media = payload.get("media")
    
    print(f"📩 [Queued Async Message] Chat: '{sender_chat_id}' | Text: '{text_content}'")
    
    if not sender_chat_id:
        print(f"⚠️ Warning: sender_chat_id is empty! Full raw event: {data}")
        return {"status": "error", "message": "Missing sender_chat_id"}
    
    background_tasks.add_task(process_and_reply, session, sender_chat_id, text_content, media)
    
    return {"status": "success"}

if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=3000)
