using System.Text.RegularExpressions;

namespace OpenWA.Agent.Infrastructure.Triage;

public enum OwnerAction
{
    /// <summary>Free text the parser does not recognise: the LLM interprets it.</summary>
    Unknown,
    Issue,
    Reply,
    Ignore,
    List,
    Help,
    /// <summary>Move the latest messages of a request into a new one. Text: how many (optional).</summary>
    Split,
}

/// <param name="Code">Request code (#12) written in the message or found in the quoted summary.</param>
/// <param name="Text">Issue: words after the keyword (may start with a repository name). Reply: the text to send.</param>
public sealed record OwnerCommand(OwnerAction Action, int? Code, string? Text);

/// <summary>Recognises the owner's short commands, e.g. "#12 issue", "#12 rispondi: ok", "#12 ignora".</summary>
public static partial class OwnerCommandParser
{
    [GeneratedRegex(@"#\s?(\d{1,6})\b")]
    private static partial Regex CodeRx();

    [GeneratedRegex(@"^(?:aperte|lista|elenco|elenca|richieste)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ListRx();

    [GeneratedRegex(@"^(?:aiuto|help|comandi|\?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex HelpRx();

    [GeneratedRegex(@"^(?:crea\s+(?:una\s+|la\s+)?)?(?:issue|sviluppo|ticket)\b[\s:.\-–]*(.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex IssueRx();

    [GeneratedRegex(@"^(?:rispondi|rispondigli|rispondile|scrivi|scrivigli|scrivile)\b[\s:.\-–]*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ReplyRx();

    [GeneratedRegex(@"^(?:dividi|separa|dividila|separala)\b\D*(\d{1,3})?", RegexOptions.IgnoreCase)]
    private static partial Regex SplitRx();

    [GeneratedRegex(@"^(?:ignora|chiudi|archivia|lascia\s+stare)\b", RegexOptions.IgnoreCase)]
    private static partial Regex IgnoreRx();

    public static OwnerCommand Parse(string text, string? quotedBody)
    {
        text = (text ?? string.Empty).Trim();
        int? code = null;
        var rest = text;

        var m = CodeRx().Match(text);
        if (m.Success)
        {
            code = int.Parse(m.Groups[1].Value);
            rest = (text[..m.Index] + " " + text[(m.Index + m.Length)..]).Trim();
        }
        else if (!string.IsNullOrEmpty(quotedBody) && CodeRx().Match(quotedBody) is { Success: true } q)
        {
            code = int.Parse(q.Groups[1].Value);
        }

        if (ListRx().IsMatch(rest)) return new(OwnerAction.List, code, null);
        if (HelpRx().IsMatch(rest)) return new(OwnerAction.Help, code, null);
        if (IssueRx().Match(rest) is { Success: true } issue) return new(OwnerAction.Issue, code, issue.Groups[1].Value.Trim());
        if (ReplyRx().Match(rest) is { Success: true } reply) return new(OwnerAction.Reply, code, reply.Groups[1].Value.Trim());
        if (SplitRx().Match(rest) is { Success: true } split)
            return new(OwnerAction.Split, code, split.Groups[1].Success ? split.Groups[1].Value : null);
        if (IgnoreRx().IsMatch(rest)) return new(OwnerAction.Ignore, code, null);
        return new(OwnerAction.Unknown, code, rest);
    }

    /// <summary>
    /// Splits the words after "issue" into repository and note. The first word is the repository
    /// only if it names an allowed one (short name or "owner/repo"); otherwise everything is the note.
    /// </summary>
    public static (string? Repo, string Note) SplitRepo(string? text, IReadOnlyDictionary<string, string> repos)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Length == 0) return (null, string.Empty);
        var first = text.Split((char[])[' ', '\n', ':', ','], 2)[0].Trim().TrimEnd('.');
        var rest = text.Length > first.Length ? text[first.Length..].TrimStart(' ', ':', ',', '-', '\n').Trim() : string.Empty;
        foreach (var (shortName, fullName) in repos)
        {
            if (first.Equals(shortName, StringComparison.OrdinalIgnoreCase) || first.Equals(fullName, StringComparison.OrdinalIgnoreCase))
            {
                return (fullName, rest);
            }
        }
        return (null, text);
    }

    /// <summary>Digits of a WhatsApp id ("393331234567@c.us" -> "393331234567").</summary>
    public static string Digits(string? id) =>
        new((id ?? string.Empty).Split('@')[0].Where(char.IsDigit).ToArray());

    /// <summary>True if the chat id belongs to the owner. "1234567890" also matches "391234567890" (Italian prefix).</summary>
    public static bool IsOwner(string? chatId, string? owner)
    {
        var a = Digits(chatId);
        var b = Digits(owner);
        if (a.Length == 0 || b.Length == 0) return false;
        if (chatId!.Contains("@g.us")) return false;
        return a == b || (b.Length == 10 && a == "39" + b);
    }
}
