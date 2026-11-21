using System.Text.Json;
using OpenWA.Agent.Infrastructure;
using Xunit;

namespace OpenWA.Agent.Tests;

public class CompactHistoryTests
{
    [Fact]
    public void Drops_media_base64_and_keeps_text()
    {
        var hugeMedia = new string('A', 500_000);
        var raw = JsonSerializer.Serialize(new
        {
            messages = new object[]
            {
                new { type = "text", direction = "incoming", from = "39333@c.us", body = "Ciao", timestamp = 1, metadata = new { } },
                new { type = "image", direction = "incoming", from = "39333@c.us", body = "", timestamp = 2, metadata = new { media = hugeMedia } },
                new { type = "text", direction = "outgoing", from = "me", body = "Eccomi", timestamp = 3, metadata = new { } },
            },
            total = 3,
        });

        var compact = OpenWaMcpPlugin.CompactHistory(raw);

        Assert.True(compact.Length < 1000, $"troppo lungo: {compact.Length}");
        Assert.DoesNotContain("AAAA", compact);
        Assert.Contains("Ciao", compact);
        Assert.Contains("[image]", compact);
        Assert.Contains("\"from\":\"me\"", compact);
    }

    [Fact]
    public void Caps_total_length()
    {
        var many = Enumerable.Range(0, 50).Select(i => new { type = "text", direction = "incoming", body = new string('x', 900), timestamp = i });
        var compact = OpenWaMcpPlugin.CompactHistory(JsonSerializer.Serialize(new { messages = many }));
        Assert.True(compact.Length <= 8001);
    }

    [Fact]
    public void Non_json_is_not_passed_when_huge()
    {
        Assert.Equal("Nessuna cronologia disponibile.", OpenWaMcpPlugin.CompactHistory(new string('<', 20_000)));
    }
}
