using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenWA.Agent.Core;

namespace OpenWA.Agent.Infrastructure.Triage;

/// <summary>Reads triage.json, re-reading it only when the file changes.</summary>
public sealed class TriageSettingsProvider
{
    private readonly string _path;
    private readonly ILogger<TriageSettingsProvider> _logger;
    private readonly object _gate = new();
    private DateTime _stamp = DateTime.MinValue;
    private TriageSettings _cached = new();

    public TriageSettingsProvider(IOptions<AgentOptions> options, ILogger<TriageSettingsProvider> logger)
    {
        _path = options.Value.TriageFile;
        _logger = logger;
    }

    public TriageSettings Get()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return _cached = new TriageSettings();
                var stamp = File.GetLastWriteTimeUtc(_path);
                if (stamp != _stamp)
                {
                    _cached = JsonSerializer.Deserialize<TriageSettings>(File.ReadAllText(_path)) ?? new TriageSettings();
                    _stamp = stamp;
                    _logger.LogInformation("Triage settings loaded from {Path}: enabled={Enabled}, dry_run={DryRun}", _path, _cached.Enabled, _cached.DryRun);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                _logger.LogWarning(ex, "Cannot read {Path}: keeping the previous triage settings", _path);
            }
            return _cached;
        }
    }
}

/// <summary>Sends WhatsApp text messages through the OpenWA API.</summary>
public sealed class WhatsAppSender
{
    private readonly IHttpClientFactory _factory;
    private readonly ILogger<WhatsAppSender> _logger;

    public WhatsAppSender(IHttpClientFactory factory, ILogger<WhatsAppSender> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task SendAsync(string session, string chatId, string text, bool dryRun, CancellationToken ct = default)
    {
        if (dryRun)
        {
            _logger.LogInformation("[DRY RUN] WhatsApp to {ChatId}: {Text}", chatId, text);
            return;
        }
        var client = _factory.CreateClient("McpClient_OpenWA");
        var response = await client.PostAsJsonAsync($"/api/sessions/{Uri.EscapeDataString(session)}/messages/send-text", new { chatId, text }, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"OpenWA send-text failed ({(int)response.StatusCode}): {body[..Math.Min(body.Length, 200)]}");
        }
        _logger.LogInformation("WhatsApp message sent to {ChatId} ({Length} chars)", chatId, text.Length);
    }
}

/// <summary>Creates GitHub issues with the token stored in github.key.</summary>
public sealed partial class GitHubIssues
{
    private readonly IHttpClientFactory _factory;
    private readonly string _tokenFile;
    private readonly ILogger<GitHubIssues> _logger;

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepoRx();

    public GitHubIssues(IHttpClientFactory factory, IOptions<AgentOptions> options, ILogger<GitHubIssues> logger)
    {
        _factory = factory;
        _tokenFile = options.Value.GitHubTokenFile;
        _logger = logger;
    }

    public bool HasToken => File.Exists(_tokenFile) && new FileInfo(_tokenFile).Length > 0;

    public async Task<string> CreateAsync(string repo, string title, string body, bool dryRun, CancellationToken ct = default)
    {
        if (!RepoRx().IsMatch(repo)) throw new ArgumentException($"Repository non valido: {repo}");
        if (dryRun)
        {
            _logger.LogInformation("[DRY RUN] GitHub issue on {Repo}: {Title}\n{Body}", repo, title, body);
            return $"https://github.com/{repo}/issues/(dry-run)";
        }
        var token = (await File.ReadAllTextAsync(_tokenFile, ct)).Trim();
        var client = _factory.CreateClient("GitHub");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.github.com/repos/{repo}/issues")
        {
            Content = JsonContent.Create(new { title, body }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var hint = (int)response.StatusCode switch
            {
                401 => "token non valido o scaduto",
                403 => "il token non ha il permesso Issues: Read and write su questo repository",
                404 => "repository non trovato o non accessibile con questo token",
                410 => "le issue sono disattivate su questo repository",
                _ => text[..Math.Min(text.Length, 200)],
            };
            throw new InvalidOperationException($"GitHub {(int)response.StatusCode}: {hint}");
        }
        var url = JsonDocument.Parse(text).RootElement.GetProperty("html_url").GetString()!;
        _logger.LogInformation("GitHub issue created: {Url}", url);
        return url;
    }
}
