using System.Text.Json.Serialization;

namespace OpenWA.Agent.Core;

/// <summary>
/// Settings of the triage workflow, stored in triage.json (path: Agent:TriageFile) and
/// re-read when the file changes, so edits apply without restarting the agent.
/// </summary>
public class TriageSettings
{
    /// <summary>When false the agent keeps the previous behaviour (direct LLM replies).</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>Owner's WhatsApp number (digits, with country code) or full chat id.</summary>
    [JsonPropertyName("owner")]
    public string Owner { get; set; } = string.Empty;

    /// <summary>Quiet time after a person's last message before the owner gets the summary.</summary>
    [JsonPropertyName("debounce_seconds")]
    public int DebounceSeconds { get; set; } = 120;

    [JsonPropertyName("ignore_groups")]
    public bool IgnoreGroups { get; set; } = true;

    /// <summary>Closed requests are deleted this many days after being closed.</summary>
    [JsonPropertyName("retention_days")]
    public int RetentionDays { get; set; } = 30;

    /// <summary>Allowed repositories: short name -> "owner/repo".</summary>
    [JsonPropertyName("repos")]
    public Dictionary<string, string> Repos { get; set; } = new();

    /// <summary>Short name used when the owner does not name a repository.</summary>
    [JsonPropertyName("default_repo")]
    public string? DefaultRepo { get; set; }

    /// <summary>Log WhatsApp messages and GitHub issues instead of sending/creating them.</summary>
    [JsonPropertyName("dry_run")]
    public bool DryRun { get; set; }
}
