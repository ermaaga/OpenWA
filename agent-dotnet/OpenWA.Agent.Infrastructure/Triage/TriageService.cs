using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using OpenWA.Agent.Core;

namespace OpenWA.Agent.Infrastructure.Triage;

/// <summary>
/// Triage workflow:
/// 1. a person writes to the bot: the message is stored and the person gets an acknowledgement;
/// 2. after <see cref="TriageSettings.DebounceSeconds"/> of quiet, the owner gets a summary with a code (#12);
/// 3. the owner answers with a command: create a GitHub issue, reply to the person, or ignore.
/// </summary>
public sealed partial class TriageService
{
    private const string FallbackAck = "Grazie per il messaggio! Ho preso in carico la tua richiesta, ti aggiorneremo appena possibile.";
    private const int MaxMessagesPerRequest = 50;
    private const int MaxNotifyFailures = 5;

    private readonly Kernel _kernel;
    private readonly TriageStore _store;
    private readonly TriageSettingsProvider _settings;
    private readonly WhatsAppSender _whatsApp;
    private readonly GitHubIssues _gitHub;
    private readonly SystemPromptProvider _prompts;
    private readonly ILogger<TriageService> _logger;

    public TriageService(Kernel kernel, TriageStore store, TriageSettingsProvider settings, WhatsAppSender whatsApp,
        GitHubIssues gitHub, SystemPromptProvider prompts, ILogger<TriageService> logger)
    {
        _kernel = kernel;
        _store = store;
        _settings = settings;
        _whatsApp = whatsApp;
        _gitHub = gitHub;
        _prompts = prompts;
        _logger = logger;
    }

    public bool IsEnabled
    {
        get
        {
            var s = _settings.Get();
            return s.Enabled && !string.IsNullOrWhiteSpace(s.Owner);
        }
    }

    // ------------------------------------------------------------------ incoming messages

    public async Task HandleIncomingAsync(string session, WebhookPayload payload, CancellationToken ct = default)
    {
        var s = _settings.Get();
        var chatId = payload.ChatId ?? payload.From ?? string.Empty;
        if (chatId.Length == 0) return;

        if (OwnerCommandParser.IsOwner(chatId, s.Owner))
        {
            await HandleOwnerAsync(session, chatId, payload, s, ct);
            return;
        }
        if (IsIgnoredChat(chatId, payload, s))
        {
            _logger.LogInformation("Triage: message from {ChatId} ignored (group or broadcast)", chatId);
            return;
        }

        var text = MessageText(payload);

        // A request already summarised to the owner gets the new message only if it is the same topic;
        // otherwise a new request starts. Messages before the summary are always grouped (burst).
        var latest = LatestOpen((await _store.ReadAsync()).Requests, chatId);
        var newTopic = latest is { NotifiedAt: not null } && !await SameTopicAsync(latest, text, ct);

        var now = DateTimeOffset.Now;
        var (code, ackNeeded) = await _store.UpdateAsync(state =>
        {
            var r = LatestOpen(state.Requests, chatId);
            // Another message of the same burst may have opened the new request meanwhile: join it.
            var startNew = r is null || (newTopic && r.Code == latest!.Code);
            if (startNew)
            {
                r = new TriageRequest { Code = state.NextCode++, ChatId = chatId, CreatedAt = now };
                state.Requests.Add(r);
            }
            r = r!;
            r.Messages.Add(new TriageMessage { At = now, Type = payload.Type ?? "text", Text = text });
            if (r.Messages.Count > MaxMessagesPerRequest)
            {
                r.Messages.RemoveAt(0);
                r.SummarizedCount = Math.Max(0, r.SummarizedCount - 1);
            }
            r.Session = session;
            r.Status = TriageStatus.Collecting;
            r.UpdatedAt = now;
            r.NotifyFailures = 0;
            // Marked before sending, so a burst of messages gets a single acknowledgement.
            var ack = !r.AckSent;
            r.AckSent = true;
            return (r.Code, ack);
        });
        _logger.LogInformation("Triage: message stored in request #{Code} from {ChatId}{NewTopic}", code, chatId,
            newTopic ? " (new topic)" : string.Empty);

        if (ackNeeded)
        {
            try
            {
                await _whatsApp.SendAsync(session, chatId, await AcknowledgementAsync(chatId, text, ct), s.DryRun, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Triage: acknowledgement to {ChatId} failed", chatId);
            }
        }
    }

    public static TriageRequest? LatestOpen(IEnumerable<TriageRequest> requests, string chatId) =>
        requests.Where(x => x.ChatId == chatId && x.Status != TriageStatus.Closed).MaxBy(x => x.Code);

    /// <summary>Asks the model whether a new message continues the request. When unsure: new topic.</summary>
    private async Task<bool> SameTopicAsync(TriageRequest r, string text, CancellationToken ct)
    {
        try
        {
            var answer = await CompleteAsync(
                "Decidi se un nuovo messaggio WhatsApp continua la stessa richiesta o ne apre una diversa. " +
                "Stessa richiesta: aggiunge dettagli, precisa, sollecita, ringrazia o risponde a qualcosa della richiesta. " +
                "Richiesta diversa: chiede o segnala un'altra cosa (un altro problema, un'altra funzionalità, un altro argomento). " +
                "Rispondi SOLO con JSON {\"same\": true|false}.",
                $"Richiesta già aperta: {r.Summary ?? FormatMessages(r)}\n\nNuovo messaggio: {Truncate(text, 1500)}", ct);
            if (TryParseJson(answer) is { } json && json.TryGetProperty("same", out var same) &&
                same.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return same.GetBoolean();
            }
            _logger.LogWarning("Triage: topic check for #{Code} was not valid JSON, treating it as a new request", r.Code);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Triage: topic check for #{Code} failed, treating it as a new request", r.Code);
        }
        return false;
    }

    private async Task<string> AcknowledgementAsync(string chatId, string text, CancellationToken ct)
    {
        try
        {
            var reply = CleanAck(await CompleteAsync(
                _prompts.GetSystemPrompt(chatId) + "\n\nIn questo momento NON hai strumenti: scrivi direttamente il testo del messaggio, " +
                "senza chiamate a funzioni, virgolette o spiegazioni.",
                $"Hai ricevuto questo messaggio su WhatsApp: \"{Truncate(text, 1000)}\".\n" +
                "Scrivi SOLO il testo di una breve risposta (una o due frasi) che conferma di aver preso in carico la richiesta " +
                "e che la persona verrà aggiornata appena possibile. Non promettere tempi né soluzioni e non fare domande. " +
                "Segui la lingua e lo stile del tuo prompt di sistema.", ct));
            return reply.Length is > 0 and < 600 ? reply : FallbackAck;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Triage: LLM acknowledgement failed, using the default text");
            return FallbackAck;
        }
    }

    // ------------------------------------------------------------------ periodic work

    /// <summary>Sends the owner the summaries that are due and deletes old closed requests.</summary>
    public async Task ProcessDueAsync(CancellationToken ct)
    {
        var s = _settings.Get();
        if (!s.Enabled || string.IsNullOrWhiteSpace(s.Owner)) return;

        var now = DateTimeOffset.Now;
        var debounce = TimeSpan.FromSeconds(Math.Max(0, s.DebounceSeconds));
        var due = (await _store.ReadAsync()).Requests
            .Where(r => r.Status == TriageStatus.Collecting && now - r.LastMessageAt >= debounce && r.NotifyFailures < MaxNotifyFailures)
            .ToList();

        foreach (var r in due)
        {
            var count = r.Messages.Count;
            try
            {
                var (summary, development) = await SummarizeAsync(r, ct);
                await _whatsApp.SendAsync(r.Session, OwnerChatId(s.Owner), Notification(r, summary, development, s), s.DryRun, ct);
                await _store.UpdateAsync(state =>
                {
                    var cur = state.Requests.FirstOrDefault(x => x.Code == r.Code);
                    if (cur is null) return 0;
                    cur.Summary = summary;
                    cur.LooksLikeDevelopment = development;
                    if (cur.NotifiedAt is null) cur.SummarizedCount = count;
                    cur.NotifiedAt = DateTimeOffset.Now;
                    // New messages arrived while summarising: stay in "collecting" so an update follows.
                    if (cur.Status == TriageStatus.Collecting && cur.Messages.Count == count) cur.Status = TriageStatus.Pending;
                    return 0;
                });
                _logger.LogInformation("Triage: summary of request #{Code} sent to the owner", r.Code);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Triage: summary of request #{Code} failed", r.Code);
                await _store.UpdateAsync(state =>
                {
                    var cur = state.Requests.FirstOrDefault(x => x.Code == r.Code);
                    if (cur is not null) cur.NotifyFailures++;
                    return 0;
                });
            }
        }

        var cutoff = now.AddDays(-Math.Max(1, s.RetentionDays));
        var removed = await _store.UpdateAsync(state =>
            state.Requests.RemoveAll(x => x.Status == TriageStatus.Closed && x.ClosedAt < cutoff));
        if (removed > 0) _logger.LogInformation("Triage: deleted {Count} requests closed more than {Days} days ago", removed, s.RetentionDays);
    }

    private async Task<(string Summary, bool Development)> SummarizeAsync(TriageRequest r, CancellationToken ct)
    {
        var fallback = Truncate(string.Join(" / ", r.Messages.Select(m => m.Text)), 500);
        try
        {
            var answer = await CompleteAsync(
                "Riassumi per il titolare le richieste arrivate su WhatsApp. Rispondi SOLO con un oggetto JSON " +
                "{\"summary\": \"...\", \"development\": true|false}. summary: in italiano, al massimo 3 frasi: cosa chiede " +
                "la persona e cosa serve fare. Non inventare nulla. development: true se riguarda sviluppo software " +
                "(nuove funzionalità, bug, modifiche a un'app, a un sito o a un sistema).",
                FormatMessages(r), ct);
            if (TryParseJson(answer) is { } json)
            {
                var summary = json.TryGetProperty("summary", out var sp) ? sp.GetString() : null;
                var dev = json.TryGetProperty("development", out var dp) && dp.ValueKind == JsonValueKind.True;
                if (!string.IsNullOrWhiteSpace(summary)) return (summary.Trim(), dev);
            }
            _logger.LogWarning("Triage: summary of #{Code} was not valid JSON, using the raw messages", r.Code);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Triage: LLM summary of #{Code} failed, using the raw messages", r.Code);
        }
        return (fallback, false);
    }

    private static string Notification(TriageRequest r, string summary, bool development, TriageSettings s)
    {
        var repo = DefaultRepo(s);
        var sb = new StringBuilder();
        sb.AppendLine(r.NotifiedAt is null
            ? $"📥 Richiesta #{r.Code} da {Phone(r.ChatId)}"
            : $"🔄 Aggiornamento richiesta #{r.Code} da {Phone(r.ChatId)}");
        sb.AppendLine(summary);
        var all = string.Join("\n", r.Messages.Select(m => m.Text));
        if (all.Length <= 400) sb.AppendLine().AppendLine("Messaggi:").AppendLine(string.Join("\n", r.Messages.Select(m => $"› {m.Text}")));
        sb.AppendLine($"({r.Messages.Count} {(r.Messages.Count == 1 ? "messaggio" : "messaggi")}, ultimo alle {r.LastMessageAt:HH:mm})");
        sb.AppendLine();
        sb.AppendLine("Rispondi citando questo messaggio, oppure scrivi #" + r.Code + " seguito da:");
        sb.AppendLine(repo is null
            ? "• issue <repository> — creo una issue su GitHub"
            : $"• issue — creo una issue su {repo}{(development ? " (sembra sviluppo)" : "")}");
        sb.AppendLine("• rispondi: <testo> — lo mando alla persona");
        if (r.NotifiedAt is not null) sb.AppendLine("• dividi — se i nuovi messaggi sono un'altra richiesta");
        sb.Append("• ignora — archivio la richiesta");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ owner commands

    private async Task HandleOwnerAsync(string session, string ownerChat, WebhookPayload payload, TriageSettings s, CancellationToken ct)
    {
        var text = MessageText(payload);
        var cmd = OwnerCommandParser.Parse(text, payload.QuotedMessage?.Body);
        var open = (await _store.ReadAsync()).Requests.Where(r => r.Status != TriageStatus.Closed).OrderBy(r => r.Code).ToList();
        _logger.LogInformation("Triage: owner command {Action} (code {Code})", cmd.Action, cmd.Code);

        if (cmd.Action == OwnerAction.Unknown)
        {
            cmd = await InterpretAsync(cmd, text, open, ct);
        }

        async Task Say(string message) => await _whatsApp.SendAsync(session, ownerChat, message, s.DryRun, ct);

        try
        {
            switch (cmd.Action)
            {
                case OwnerAction.List:
                    await Say(open.Count == 0
                        ? "Nessuna richiesta aperta."
                        : "Richieste aperte:\n" + string.Join("\n", open.Select(r =>
                            $"#{r.Code} {Phone(r.ChatId)} — {Truncate(r.Summary ?? r.Messages.LastOrDefault()?.Text ?? "", 120)}" +
                            (r.Status == TriageStatus.Collecting ? " (in arrivo)" : ""))));
                    return;
                case OwnerAction.Help:
                    await Say(HelpText(s));
                    return;
                case OwnerAction.Unknown:
                    await Say("Non ho capito cosa fare. " + HelpText(s));
                    return;
            }

            var request = cmd.Code is { } code
                ? open.FirstOrDefault(r => r.Code == code)
                : open.Count == 1 ? open[0] : null;
            if (request is null)
            {
                await Say(cmd.Code is { } c
                    ? $"Non trovo una richiesta aperta #{c}. Scrivi \"aperte\" per vederle."
                    : open.Count == 0 ? "Non ci sono richieste aperte." : "A quale richiesta ti riferisci? Rispondi citando il riepilogo o scrivi il codice, es. #" + open[^1].Code + ".");
                return;
            }

            switch (cmd.Action)
            {
                case OwnerAction.Issue:
                    await CreateIssueAsync(request, cmd.Text, s, Say, ct);
                    break;
                case OwnerAction.Reply:
                    if (string.IsNullOrWhiteSpace(cmd.Text))
                    {
                        await Say($"Cosa devo scrivere? Es. #{request.Code} rispondi: arrivo domani");
                        return;
                    }
                    await _whatsApp.SendAsync(request.Session, request.ChatId, cmd.Text!, s.DryRun, ct);
                    await CloseAsync(request.Code, "replied", null);
                    await Say($"✅ Inviato a {Phone(request.ChatId)} e richiesta #{request.Code} chiusa.");
                    break;
                case OwnerAction.Split:
                    await SplitAsync(request, cmd.Text, Say);
                    break;
                case OwnerAction.Ignore:
                    await CloseAsync(request.Code, "ignored", null);
                    await Say($"✅ Richiesta #{request.Code} archiviata.");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Triage: owner command {Action} failed", cmd.Action);
            await Say($"⚠️ Non ci sono riuscito: {ex.Message}");
        }
    }

    private async Task CreateIssueAsync(TriageRequest request, string? words, TriageSettings s, Func<string, Task> say, CancellationToken ct)
    {
        var (repo, note) = OwnerCommandParser.SplitRepo(words, s.Repos);
        repo ??= DefaultRepo(s);
        if (repo is null)
        {
            await say("Su quale repository? Disponibili: " + (s.Repos.Count == 0 ? "nessuno, aggiungili dalla dashboard" : string.Join(", ", s.Repos.Keys)));
            return;
        }
        if (!s.DryRun && !_gitHub.HasToken)
        {
            await say("⚠️ Manca il token GitHub: aggiungilo dall'app Credenziali (voce collegata a GitHub).");
            return;
        }

        var (title, body) = await DraftIssueAsync(request, note, ct);
        body += $"\n\n---\nRichiesta #{request.Code} arrivata via WhatsApp da {Phone(request.ChatId)} il {request.CreatedAt:dd/MM/yyyy HH:mm}." +
                "\n\n<details><summary>Messaggi originali</summary>\n\n" +
                string.Join("\n", request.Messages.Select(m => $"> [{m.At:dd/MM HH:mm}] {m.Text.Replace("\n", "\n> ")}")) +
                "\n</details>";
        var url = await _gitHub.CreateAsync(repo, title, body, s.DryRun, ct);
        await CloseAsync(request.Code, "issue", url);
        await say($"✅ Issue creata per #{request.Code} su {repo}:\n{url}");
    }

    private async Task<(string Title, string Body)> DraftIssueAsync(TriageRequest r, string note, CancellationToken ct)
    {
        var fallbackTitle = Truncate(r.Summary ?? r.Messages.FirstOrDefault()?.Text ?? $"Richiesta #{r.Code}", 80);
        var fallbackBody = $"## Richiesta\n{r.Summary}\n" + (note.Length > 0 ? $"\n## Note\n{note}\n" : "");
        try
        {
            var answer = await CompleteAsync(
                "Scrivi una issue GitHub in italiano per uno sviluppatore, basandoti SOLO sulle informazioni fornite. " +
                "Rispondi SOLO con un oggetto JSON {\"title\": \"...\", \"body\": \"...\"}. title: al massimo 80 caratteri, " +
                "chiaro e specifico. body: markdown con le sezioni \"## Richiesta\", \"## Dettagli\" e, se deducibili, " +
                "\"## Criteri di accettazione\". Non inventare requisiti.",
                $"Riepilogo: {r.Summary}\n\nMessaggi della persona:\n{FormatMessages(r)}" +
                (note.Length > 0 ? $"\n\nIndicazioni del titolare: {note}" : ""), ct);
            if (TryParseJson(answer) is { } json &&
                json.TryGetProperty("title", out var t) && json.TryGetProperty("body", out var b) &&
                !string.IsNullOrWhiteSpace(t.GetString()) && !string.IsNullOrWhiteSpace(b.GetString()))
            {
                return (Truncate(t.GetString()!.Trim(), 120), b.GetString()!.Trim() + (note.Length > 0 ? $"\n\n## Note del titolare\n{note}" : ""));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Triage: LLM issue draft for #{Code} failed, using the summary", r.Code);
        }
        return (fallbackTitle, fallbackBody);
    }

    private async Task<OwnerCommand> InterpretAsync(OwnerCommand cmd, string text, List<TriageRequest> open, CancellationToken ct)
    {
        try
        {
            var answer = await CompleteAsync(
                "Sei l'assistente del titolare. Interpreta il suo messaggio e rispondi SOLO con JSON " +
                "{\"action\": \"issue|reply|ignore|split|list|help|none\", \"code\": numero o null, \"text\": stringa o null}. " +
                "issue: creare una issue di sviluppo (text = repository e note, se indicati); reply: rispondere alla persona " +
                "(text = il testo esatto da inviarle); ignore: archiviare la richiesta; split: separare gli ultimi messaggi in una nuova richiesta (text = quanti, se indicato); list: elencare le richieste aperte; " +
                "none: non è un comando. code: il numero della richiesta, se si capisce.",
                $"Richieste aperte:\n{string.Join("\n", open.Select(r => $"#{r.Code} {Phone(r.ChatId)}: {r.Summary ?? r.Messages.LastOrDefault()?.Text}"))}\n\nMessaggio del titolare: {text}",
                ct);
            if (TryParseJson(answer) is { } json)
            {
                var action = json.TryGetProperty("action", out var a) ? a.GetString() : null;
                int? code = json.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : cmd.Code;
                var t = json.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String ? tx.GetString() : null;
                return action switch
                {
                    "issue" => new(OwnerAction.Issue, code, t),
                    "reply" => new(OwnerAction.Reply, code, t),
                    "ignore" => new(OwnerAction.Ignore, code, null),
                    "split" => new(OwnerAction.Split, code, t),
                    "list" => new(OwnerAction.List, code, null),
                    "help" => new(OwnerAction.Help, code, null),
                    _ => cmd,
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Triage: LLM interpretation of the owner message failed");
        }
        return cmd;
    }

    /// <summary>
    /// Moves the latest messages of a request into a new request: by default those that arrived after the
    /// first summary, or the last N if the owner gives a number. Both requests get a fresh summary.
    /// </summary>
    private async Task SplitAsync(TriageRequest request, string? howMany, Func<string, Task> say)
    {
        var (newCode, moved, error) = await _store.UpdateAsync(state =>
        {
            var r = state.Requests.FirstOrDefault(x => x.Code == request.Code && x.Status != TriageStatus.Closed);
            if (r is null) return (0, 0, $"La richiesta #{request.Code} non è più aperta.");
            var n = int.TryParse(howMany, out var asked) ? asked : r.Messages.Count - Math.Min(r.SummarizedCount, r.Messages.Count);
            if (n <= 0)
                return (0, 0, $"Tutti i messaggi della #{r.Code} erano già nel primo riepilogo: indica quanti spostarne, es. #{r.Code} dividi 2.");
            if (n >= r.Messages.Count)
                return (0, 0, $"La #{r.Code} ha {r.Messages.Count} messaggi: posso spostarne al massimo {r.Messages.Count - 1}.");

            var now = DateTimeOffset.Now;
            var tail = r.Messages.GetRange(r.Messages.Count - n, n);
            r.Messages.RemoveRange(r.Messages.Count - n, n);
            r.SummarizedCount = Math.Min(r.SummarizedCount, r.Messages.Count);
            r.Status = TriageStatus.Collecting;       // re-summarise without the moved messages
            r.NotifyFailures = 0;
            r.UpdatedAt = now;
            var created = new TriageRequest
            {
                Code = state.NextCode++, Session = r.Session, ChatId = r.ChatId, Messages = tail,
                Status = TriageStatus.Collecting, AckSent = true, CreatedAt = tail[0].At, UpdatedAt = now,
            };
            state.Requests.Add(created);
            return (created.Code, n, (string?)null);
        });
        if (error is not null)
        {
            await say(error);
            return;
        }
        // Sent at the next worker tick: the messages are older than the debounce time.
        await say($"✅ {(moved == 1 ? "Spostato 1 messaggio" : $"Spostati {moved} messaggi")} dalla #{request.Code} alla nuova richiesta #{newCode}. Ti mando i riepiloghi aggiornati.");
    }

    private Task CloseAsync(int code, string outcome, string? issueUrl) =>
        _store.UpdateAsync(state =>
        {
            var r = state.Requests.FirstOrDefault(x => x.Code == code);
            if (r is null) return 0;
            r.Status = TriageStatus.Closed;
            r.Outcome = outcome;
            r.IssueUrl = issueUrl;
            r.ClosedAt = DateTimeOffset.Now;
            r.UpdatedAt = DateTimeOffset.Now;
            return 0;
        });

    private static string HelpText(TriageSettings s)
    {
        var repo = DefaultRepo(s);
        return "Comandi (cita il riepilogo oppure metti il codice, es. #12):\n" +
               $"• #12 issue{(repo is null ? " <repository>" : "")} [note] — crea una issue{(repo is null ? "" : $" su {repo}")}\n" +
               "• #12 rispondi: <testo> — lo mando alla persona\n" +
               "• #12 ignora — archivio la richiesta\n" +
               "• #12 dividi [quanti] — sposto gli ultimi messaggi in una nuova richiesta\n" +
               "• aperte — elenco delle richieste aperte";
    }

    // ------------------------------------------------------------------ helpers

    private async Task<string> CompleteAsync(string system, string user, CancellationToken ct)
    {
        var chat = _kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory(system);
        history.AddUserMessage(user);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(120));
        var result = await chat.GetChatMessageContentAsync(history, cancellationToken: cts.Token);
        return ThinkRx().Replace(result.Content ?? string.Empty, string.Empty).Trim();
    }

    /// <summary>Unwraps answers written as a fake tool call, e.g. send_message(text="..."), and strips quotes.</summary>
    public static string CleanAck(string reply)
    {
        reply = reply.Trim();
        if (FakeCallRx().Match(reply) is { Success: true } m) reply = Regex.Unescape(m.Groups[1].Value).Trim();
        if (reply.Length >= 2 && (reply[0] == '"' && reply[^1] == '"' || reply[0] == '«' && reply[^1] == '»')) reply = reply[1..^1].Trim();
        return reply.Contains("send_message", StringComparison.OrdinalIgnoreCase) || reply.Contains("chatId") ? string.Empty : reply;
    }

    [GeneratedRegex(@"^\w+\s*\((?:.*?\btext\s*[=:]\s*)?""((?:[^""\\]|\\.)*)"".*\)\s*;?$", RegexOptions.Singleline)]
    private static partial Regex FakeCallRx();

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline)]
    private static partial Regex ThinkRx();

    public static JsonElement? TryParseJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string FormatMessages(TriageRequest r) =>
        string.Join("\n", r.Messages.Select(m => $"[{m.At:dd/MM HH:mm}] {m.Text}"));

    public static string MessageText(WebhookPayload p)
    {
        var text = (p.Body ?? p.Text ?? p.Caption ?? string.Empty).Trim();
        if (text.Length > 0) return Truncate(text, 4000);
        return $"[{p.Type ?? "messaggio"}]";
    }

    public static bool IsIgnoredChat(string chatId, WebhookPayload p, TriageSettings s)
    {
        if (chatId.EndsWith("@broadcast", StringComparison.OrdinalIgnoreCase) || chatId.EndsWith("@newsletter", StringComparison.OrdinalIgnoreCase))
            return true;
        return s.IgnoreGroups && (p.IsGroup == true || chatId.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase));
    }

    public static string OwnerChatId(string owner)
    {
        if (owner.Contains('@')) return owner.Trim();
        var digits = OwnerCommandParser.Digits(owner);
        return (digits.Length == 10 ? "39" + digits : digits) + "@c.us";
    }

    private static string? DefaultRepo(TriageSettings s)
    {
        if (!string.IsNullOrEmpty(s.DefaultRepo) && s.Repos.TryGetValue(s.DefaultRepo, out var full)) return full;
        return s.Repos.Count == 1 ? s.Repos.Values.First() : null;
    }

    private static string Phone(string chatId) =>
        chatId.EndsWith("@c.us") || chatId.EndsWith("@s.whatsapp.net") ? "+" + OwnerCommandParser.Digits(chatId) : chatId;

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
