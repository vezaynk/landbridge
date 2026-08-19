using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Landbridge.Classifier;

/// <summary>
/// One OpenAI-compatible client pointed at the local LiteLLM gateway.
/// The model string is the full <c>provider/model</c> slug.
/// </summary>
internal static class ChatClients
{
    public static IChatClient Create(string endpoint, string apiKey, string model) =>
        new OpenAIClient(
                new ApiKeyCredential(apiKey),
                new OpenAIClientOptions { Endpoint = new Uri(endpoint) })
            .GetChatClient(model)
            .AsIChatClient();
}
