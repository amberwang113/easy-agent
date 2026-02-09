using EasyAgent.Plugins;
using EasyAgent.Models;
using EasyAgent.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents.AzureAI;
using Microsoft.SemanticKernel.ChatCompletion;
using System.Text;

namespace EasyAgent.Controllers
{
    [ApiController]
    [Route("/")]
    public class ChatController : Controller
    {
        private readonly IAgentService _agentService;

        public ChatController(IAgentService agentService)
        {
            _agentService = agentService;
        }

        [HttpPost]
        public async Task<IActionResult> Query([FromBody] ChatMessage chatMessage)
        {
            try
            {
                // Extract user token from EasyAuth header if present (for OBO flow)
                string? userToken = HttpContext.Request.Headers["X-MS-TOKEN-AAD-ACCESS-TOKEN"].FirstOrDefault();

                return Ok(await CallAIFoundryAgent(chatMessage.Content, chatMessage.SessionId, userToken));
            }
            catch (Exception e)
            {
                Console.WriteLine($"Exception during request: {e}");
                return Ok(new ChatMessage() { Content = $"Exception during request: {e}", SessionId = null });
            }
        }

        private async Task<ChatMessage> CallAIFoundryAgent(string userMessage, string threadId, string? userToken)
        {
            // Get the agent definition (cached) and a per-request client (OBO-aware)
            var agentsClient = await _agentService.GetAgentsClientAsync(userToken);
            var agent = await _agentService.GetAgentAsync();

            // Enrich the user message with site context before sending to the agent
            var siteContextPlugin = HttpContext.RequestServices.GetRequiredService<SiteContextPlugin>();
            string siteContext = await siteContextPlugin.RequestMoreInformation(userMessage);

            string enrichedMessage = string.IsNullOrEmpty(siteContext)
                ? userMessage
                : $"{userMessage}\n\n[Site Context: {siteContext}]";

#pragma warning disable SKEXP0110
            // Don't add Kernel plugins — let the agent use its own stored tools (with auth intact)
            AzureAIAgent azureAgent = new(agent, agentsClient);

            AzureAIAgentThread agentThread;
            if (string.IsNullOrEmpty(threadId))
            {
                agentThread = new(azureAgent.Client);
            }
            else
            {
                agentThread = new(azureAgent.Client, threadId);
            }
#pragma warning restore SKEXP0110

            StringBuilder result = new StringBuilder();
            ChatMessageContent message = new(AuthorRole.User, enrichedMessage);
            await foreach (ChatMessageContent response in azureAgent.InvokeAsync(message, agentThread))
            {
                result.AppendLine(response.Content);
            }

            return new ChatMessage()
            {
                Content = result + $"  | Thread ID: {agentThread.Id}",
                SessionId = agentThread.Id
            };
        }
    }
}
