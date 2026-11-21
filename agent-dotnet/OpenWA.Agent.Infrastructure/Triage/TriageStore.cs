using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenWA.Agent.Infrastructure.Triage;

public static class TriageStatus
{
    /// <summary>Messages are still arriving; the owner has not been told yet (or must get an update).</summary>
    public const string Collecting = "collecting";
    /// <summary>Summary sent to the owner, waiting for instructions.</summary>
    public const string Pending = "pending";
    public const string Closed = "closed";
}

public sealed class TriageMessage
{
    public DateTimeOffset At { get; set; }
    public string Type { get; set; } = "text";
    public string Text { get; set; } = string.Empty;
}

public sealed class TriageRequest
{
    public int Code { get; set; }
    public string Session { get; set; } = string.Empty;
    public string ChatId { get; set; } = string.Empty;
    public string Status { get; set; } = TriageStatus.Collecting;
    public List<TriageMessage> Messages { get; set; } = new();
    public string? Summary { get; set; }
    public bool LooksLikeDevelopment { get; set; }
    public bool AckSent { get; set; }
    /// <summary>Messages covered by the first summary sent to the owner (the rest arrived later).</summary>
    public int SummarizedCount { get; set; }
    public int NotifyFailures { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? NotifiedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    /// <summary>issue | replied | ignored</summary>
    public string? Outcome { get; set; }
    public string? IssueUrl { get; set; }

    [JsonIgnore]
    public DateTimeOffset LastMessageAt => Messages.Count > 0 ? Messages[^1].At : CreatedAt;
}

public sealed class TriageState
{
    public int NextCode { get; set; } = 1;
    public List<TriageRequest> Requests { get; set; } = new();
}

/// <summary>
/// JSON file store for triage requests. Volumes are small (a handful of requests a day), so the
/// whole state is loaded, changed and written back atomically under a lock.
/// </summary>
public sealed class TriageStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public TriageStore(string path) => _path = path;

    public async Task<T> UpdateAsync<T>(Func<TriageState, T> change)
    {
        await _lock.WaitAsync();
        try
        {
            var state = Load();
            var result = change(state);
            Save(state);
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<TriageState> ReadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return Load();
        }
        finally
        {
            _lock.Release();
        }
    }

    private TriageState Load()
    {
        if (!File.Exists(_path))
        {
            return new TriageState();
        }
        try
        {
            return JsonSerializer.Deserialize<TriageState>(File.ReadAllText(_path), Json) ?? new TriageState();
        }
        catch (JsonException)
        {
            // Keep the unreadable file for inspection and start over instead of failing every message.
            File.Copy(_path, $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);
            return new TriageState();
        }
    }

    private void Save(TriageState state)
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, Json));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        File.Move(tmp, _path, overwrite: true);
    }
}
