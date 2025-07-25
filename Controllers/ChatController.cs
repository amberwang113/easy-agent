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
                return Ok(await CallAIFoundryAgent(chatMessage.Content, chatMessage.SessionId));
            }
            catch (Exception e)
            {
                Console.WriteLine($"Exception during request: {e}");
                return Ok(new ChatMessage() { Content = $"Exception during request: {e}", SessionId = null });
            }
        }

        private async Task<ChatMessage> CallAIFoundryAgent(string userMessage, string threadId)
        {
            // Get the agent and client from the service (thread-safe)
            var agentsClient = await _agentService.GetAgentsClientAsync();
            var agent = await _agentService.GetAgentAsync();

            // Create plugin for site context using the service provider
            KernelPlugin siteContextPlugin = KernelPluginFactory.CreateFromType<SiteContextPlugin>("SiteContextQuery", serviceProvider: HttpContext.RequestServices);

#pragma warning disable SKEXP0110 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
            AzureAIAgent azureAgent = new(agent, agentsClient);

            azureAgent.Kernel.Plugins.Add(siteContextPlugin);

            AzureAIAgentThread agentThread;
            if (string.IsNullOrEmpty(threadId))
            {
                agentThread = new(azureAgent.Client);
            }
            else
            {
                agentThread = new(azureAgent.Client, threadId);
            }
#pragma warning restore SKEXP0110 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

            StringBuilder result = new StringBuilder();
            ChatMessageContent message = new(AuthorRole.User, userMessage);
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
