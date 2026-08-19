using Landbridge.Classifier;

namespace Landbridge.Classifier.Tests;

public sealed class ChatClientsTests
{
    [Fact]
    public void Constructs_an_openai_compatible_client_against_the_gateway()
    {
        var client = ChatClients.Create("http://127.0.0.1:4000/v1", "sk-test", "anthropic/claude-haiku-4-5-20251001");
        Assert.NotNull(client);
    }
}
