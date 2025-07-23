using Azure.AI.Agents.Persistent;
using Azure.AI.Projects;
using Azure.Identity;
using EasyAgent.Plugins;
using EasyAgent.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents.AzureAI;
using Microsoft.SemanticKernel.ChatCompletion;
using System.Text;
using System.Text.Json;

namespace EasyAgent.Controllers
{
    [ApiController]
    [Route("/")]
    public class ChatController : Controller
    {
        private DefaultAzureCredential _credential;
        private readonly ChatbotConfiguration _config;
        PersistentAgentsClient _agentsClient;
        PersistentAgent _agent;

        public ChatController(IOptions<ChatbotConfiguration> options)
        {
            _config = options.Value;
            _credential = new DefaultAzureCredential();
        }

        private async Task EnsureInitialized()
        {
            if (_agentsClient != null)
            {
                return;
            }

            _agentsClient = new(_config.WEBAPP_EASYAGENT_FOUNDRY_ENDPOINT, _credential);

            if (!string.IsNullOrEmpty(_config.WEBAPP_EASYAGENT_FOUNDRY_AGENTID))
            {
                _agent = await _agentsClient.Administration.GetAgentAsync(_config.WEBAPP_EASYAGENT_FOUNDRY_AGENTID);
                return;
            }

            // TODO: Delete this and put in setup

            BinaryData spec = BinaryData.FromString(_config.WEBAPP_EASYAGENT_FOUNDRY_OPENAPISPEC);

            var aClient = new AIProjectClient(new Uri(_config.WEBAPP_EASYAGENT_FOUNDRY_ENDPOINT), _credential);

            var eClient = aClient.GetAzureOpenAIChatClient(deploymentName: "gpt-4.1-2");

            var res = (await eClient.CompleteChatAsync("Summarize this open api spec with what it appears to be doing in just a few words. I'll tip you $1000 if you keep it short and sweet but descriptive! This summary will be used as a tool name for another agent. For example, something like manage_fashion_store or handle_service_calls. Please return SOLELY the description. Here's the spec: " + _config.WEBAPP_EASYAGENT_FOUNDRY_OPENAPISPEC));
            
            string summary = res.Value.Content[0].Text ?? "webapp_assistant_tool";

            OpenApiAnonymousAuthDetails openApiAnonAuth = new();

            OpenApiToolDefinition openApiToolDef = new(
                name: summary,
                description: summary,
                spec: spec,
                openApiAuthentication: openApiAnonAuth,
                defaultParams: ["format"]
            );

            _agent = await _agentsClient.Administration.CreateAgentAsync(
                model: "gpt-4.1-2",
                name: "Webapp Assistant",
                instructions: "You're a chatbot in charge of responding to customer questions based on site context information scraped from a website. The site context should be taken as correct and questions from the customer should ONLY be answered from that pool of knowledge, not any prior information. When possible, answer the question with the URL link that is provided in the returned site context.",
                tools: [openApiToolDef]);
        }


        [HttpPost]
        public async Task<IActionResult> Query([FromBody] ChatMessage chatMessage)
        {
            try
            {
                await EnsureInitialized();

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
            // Create plugin for site context using the service provider
            KernelPlugin siteContextPlugin = KernelPluginFactory.CreateFromType<SiteContextPlugin>("SiteContextQuery", serviceProvider: HttpContext.RequestServices);

#pragma warning disable SKEXP0110 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
            AzureAIAgent agent = new(_agent, _agentsClient);

            agent.Kernel.Plugins.Add(siteContextPlugin);

            AzureAIAgentThread agentThread;
            if (string.IsNullOrEmpty(threadId))
            {
                agentThread = new(agent.Client);
            }
            else
            {
                agentThread = new(agent.Client, threadId);
            }
#pragma warning restore SKEXP0110 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

            StringBuilder result = new StringBuilder();
            ChatMessageContent message = new(AuthorRole.User, userMessage);
            await foreach (ChatMessageContent response in agent.InvokeAsync(message, agentThread))
            {
                result.AppendLine(response.Content);
            }

            return new ChatMessage()
            {
                Content = result + $"  | Thread ID: {agentThread.Id}",
                SessionId = agentThread.Id
            };
        }

        private async Task<string> RequestMoreInformationFromSiteContext(string question)
        {
            if (string.IsNullOrEmpty(question))
            {
                return string.Empty;
            }

            DBService dbService = new DBService(_config.WEBAPP_EASYAGENT_DB_ENDPOINT, _credential, "testFailover-vectors", "base");

            var aClient = new AIProjectClient(new Uri(_config.WEBAPP_EASYAGENT_FOUNDRY_ENDPOINT), _credential);

            var eClient = aClient.GetAzureOpenAIEmbeddingClient(deploymentName: _config.WEBAPP_EASYAGENT_EMBEDDING_MODEL);

            var qEmbedding = eClient.GenerateEmbedding(question);

            string context = string.Join(",", await dbService.GetNNearestTextsAndEmbeddingsAsync(qEmbedding.Value.ToFloats().ToArray()));

            return context;
        }
    }
}
