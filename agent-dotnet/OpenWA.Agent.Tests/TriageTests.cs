using OpenWA.Agent.Core;
using OpenWA.Agent.Infrastructure.Triage;
using Xunit;

namespace OpenWA.Agent.Tests;

public class OwnerCommandParserTests
{
    [Theory]
    [InlineData("#12 issue", OwnerAction.Issue, 12, "")]
    [InlineData("#12 issue auxilia aggiungere export PDF", OwnerAction.Issue, 12, "auxilia aggiungere export PDF")]
    [InlineData("crea una issue #7", OwnerAction.Issue, 7, "")]
    [InlineData("#3 rispondi: arrivo domani", OwnerAction.Reply, 3, "arrivo domani")]
    [InlineData("#3 Rispondigli che va bene", OwnerAction.Reply, 3, "che va bene")]
    [InlineData("#5 ignora", OwnerAction.Ignore, 5, null)]
    [InlineData("#5 dividi", OwnerAction.Split, 5, null)]
    [InlineData("#5 dividi 2", OwnerAction.Split, 5, "2")]
    [InlineData("#5 separa gli ultimi 3", OwnerAction.Split, 5, "3")]
    [InlineData("aperte", OwnerAction.List, null, null)]
    [InlineData("aiuto", OwnerAction.Help, null, null)]
    [InlineData("ok grazie", OwnerAction.Unknown, null, "ok grazie")]
    public void Parses_commands(string text, OwnerAction action, int? code, string? rest)
    {
        var cmd = OwnerCommandParser.Parse(text, null);
        Assert.Equal(action, cmd.Action);
        Assert.Equal(code, cmd.Code);
        Assert.Equal(rest, cmd.Text);
    }

    [Fact]
    public void Takes_the_code_from_the_quoted_summary()
    {
        var cmd = OwnerCommandParser.Parse("issue", "📥 Richiesta #42 da +391234567890\nChiede una modifica");
        Assert.Equal(OwnerAction.Issue, cmd.Action);
        Assert.Equal(42, cmd.Code);
    }

    [Fact]
    public void Splits_repository_only_when_allowed()
    {
        var repos = new Dictionary<string, string> { ["auxilia"] = "ermaaga/AuxiliaDigitale" };
        Assert.Equal(("ermaaga/AuxiliaDigitale", "urgente"), OwnerCommandParser.SplitRepo("auxilia urgente", repos));
        Assert.Equal(("ermaaga/AuxiliaDigitale", ""), OwnerCommandParser.SplitRepo("ermaaga/auxiliadigitale", repos));
        Assert.Equal(((string?)null, "altro/repo nota"), OwnerCommandParser.SplitRepo("altro/repo nota", repos));
    }

    [Theory]
    [InlineData("391234567890@c.us", "1234567890", true)]
    [InlineData("391234567890@c.us", "391234567890", true)]
    [InlineData("391234567890@c.us", "+39 123 456 7890", true)]
    [InlineData("391234567899@c.us", "1234567890", false)]
    [InlineData("391234567890-1600000000@g.us", "391234567890", false)]
    [InlineData("391234567890@c.us", "", false)]
    public void Recognises_the_owner(string chatId, string owner, bool expected) =>
        Assert.Equal(expected, OwnerCommandParser.IsOwner(chatId, owner));
}

public class TriageHelpersTests
{
    [Theory]
    [InlineData("1234567890", "391234567890@c.us")]
    [InlineData("+39 123 456 7890", "391234567890@c.us")]
    [InlineData("391234567890@c.us", "391234567890@c.us")]
    public void Builds_the_owner_chat_id(string owner, string expected) =>
        Assert.Equal(expected, TriageService.OwnerChatId(owner));

    [Fact]
    public void Ignores_groups_and_broadcasts()
    {
        var s = new TriageSettings { IgnoreGroups = true };
        Assert.True(TriageService.IsIgnoredChat("123-456@g.us", new WebhookPayload(), s));
        Assert.True(TriageService.IsIgnoredChat("status@broadcast", new WebhookPayload(), s));
        Assert.False(TriageService.IsIgnoredChat("391234567890@c.us", new WebhookPayload(), s));
        Assert.False(TriageService.IsIgnoredChat("123-456@g.us", new WebhookPayload(), new TriageSettings { IgnoreGroups = false }));
    }

    [Fact]
    public void Uses_a_placeholder_for_media_without_text() =>
        Assert.Equal("[image]", TriageService.MessageText(new WebhookPayload { Type = "image" }));

    [Theory]
    [InlineData("send_message(text=\"Ho ricevuto la tua richiesta.\")", "Ho ricevuto la tua richiesta.")]
    [InlineData("send_message(chatId=\"1@c.us\", text=\"Grazie, ti aggiorno.\")", "Grazie, ti aggiorno.")]
    [InlineData("\"Presa in carico!\"", "Presa in carico!")]
    [InlineData("Presa in carico, grazie.", "Presa in carico, grazie.")]
    [InlineData("send_message(chatId, text)", "")]
    public void Cleans_acknowledgements(string raw, string expected) =>
        Assert.Equal(expected, TriageService.CleanAck(raw));

    [Fact]
    public void Picks_the_latest_open_request_of_the_chat()
    {
        var requests = new List<TriageRequest>
        {
            new() { Code = 1, ChatId = "1@c.us", Status = TriageStatus.Pending },
            new() { Code = 2, ChatId = "1@c.us", Status = TriageStatus.Collecting },
            new() { Code = 3, ChatId = "1@c.us", Status = TriageStatus.Closed },
            new() { Code = 4, ChatId = "2@c.us", Status = TriageStatus.Pending },
        };
        Assert.Equal(2, TriageService.LatestOpen(requests, "1@c.us")!.Code);
        Assert.Null(TriageService.LatestOpen(requests, "3@c.us"));
    }

    [Fact]
    public void Extracts_json_from_model_output()
    {
        var json = TriageService.TryParseJson("Ecco:\n```json\n{\"summary\": \"Chiede X\", \"development\": true}\n```");
        Assert.NotNull(json);
        Assert.Equal("Chiede X", json!.Value.GetProperty("summary").GetString());
    }
}

public class TriageStoreTests
{
    [Fact]
    public async Task Persists_requests_and_codes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"triage-{Guid.NewGuid():N}.json");
        try
        {
            var store = new TriageStore(path);
            var code = await store.UpdateAsync(s =>
            {
                var r = new TriageRequest { Code = s.NextCode++, ChatId = "1@c.us" };
                s.Requests.Add(r);
                return r.Code;
            });
            var again = await new TriageStore(path).ReadAsync();
            Assert.Equal(1, code);
            Assert.Equal(2, again.NextCode);
            Assert.Single(again.Requests);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
