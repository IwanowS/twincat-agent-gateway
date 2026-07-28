using System.Text.Json;
using TwinCatGateway.Ipc;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class GatewayJsonTests
{
    [Fact]
    public void SerializerKeepsUnicodeReadable()
    {
        string json = JsonSerializer.Serialize(
            new { Message = "Ошибка сборки" },
            GatewayJson.CreateSerializerOptions());

        Assert.Contains("Ошибка сборки", json);
        Assert.DoesNotContain("\\u", json);
        using JsonDocument parsed = JsonDocument.Parse(json);
        Assert.Equal(
            "Ошибка сборки",
            parsed.RootElement
                .GetProperty("message")
                .GetString());
    }
}
