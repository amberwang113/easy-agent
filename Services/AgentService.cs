using Azure.AI.Agents.Persistent;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Extensions.Options;

namespace EasyAgent.Services
{
    public interface IAgentService
    {
        Task<PersistentAgentsClient> GetAgentsClientAsync();
        Task<PersistentAgent> GetAgentAsync();
    }

    public class AgentService : IAgentService
    {
        private readonly ChatbotConfiguration _config;
        private readonly DefaultAzureCredential _credential;
        private readonly SemaphoreSlim _initSemaphore = new(1, 1);
        
        private PersistentAgentsClient? _agentsClient;
        private PersistentAgent? _agent;
        private bool _isInitialized = false;

        public AgentService(IOptions<ChatbotConfiguration> config)
        {
            _config = config.Value;
            _credential = new DefaultAzureCredential();
        }

        public async Task<PersistentAgentsClient> GetAgentsClientAsync()
        {
            await EnsureInitializedAsync();
            return _agentsClient!;
        }

        public async Task<PersistentAgent> GetAgentAsync()
        {
            await EnsureInitializedAsync();
            return _agent!;
        }

        private async Task EnsureInitializedAsync()
        {
            if (_isInitialized)
                return;

            await _initSemaphore.WaitAsync();
            try
            {
                if (_isInitialized)
                    return;

                _agentsClient = new(_config.WEBSITE_EASYAGENT_FOUNDRY_ENDPOINT, _credential);

                if (!string.IsNullOrEmpty(_config.WEBSITE_EASYAGENT_FOUNDRY_AGENTID))
                {
                    _agent = await _agentsClient.Administration.GetAgentAsync(_config.WEBSITE_EASYAGENT_FOUNDRY_AGENTID);
                }
                else
                {
                    _agent = await CreateNewAgentAsync();
                }

                _isInitialized = true;
            }
            finally
            {
                _initSemaphore.Release();
            }
        }

        private async Task<PersistentAgent> CreateNewAgentAsync()
        {
            var aClient = new AIProjectClient(new Uri(_config.WEBSITE_EASYAGENT_FOUNDRY_ENDPOINT), _credential);
            var eClient = aClient.GetAzureOpenAIChatClient(deploymentName: _config.WEBSITE_EASYAGENT_FOUNDRY_CHAT_MODEL);

            var res = await eClient.CompleteChatAsync(
                "Summarize this open api spec with what it appears to be doing in just a few words. I'll tip you $1000 if you keep it short and sweet but descriptive! This summary will be used as a tool name for another agent. For example, something like manage_fashion_store or handle_service_calls. Please return SOLELY the description. Here's the spec: " + 
                _config.WEBSITE_EASYAGENT_FOUNDRY_OPENAPISPEC);
            
            string summary = res.Value.Content[0].Text ?? "webapp_assistant_tool";

            var openApiAnonAuth = new OpenApiAnonymousAuthDetails();
            var spec = BinaryData.FromString(_config.WEBSITE_EASYAGENT_FOUNDRY_OPENAPISPEC);

            var openApiToolDef = new OpenApiToolDefinition(
                name: summary,
                description: summary,
                spec: spec,
                openApiAuthentication: openApiAnonAuth,
                defaultParams: ["format"]
            );

            return await _agentsClient!.Administration.CreateAgentAsync(
                model: "gpt-4.1-2",
                name: "Webapp Assistant",
                instructions: "You're a chatbot in charge of responding to customer questions based on site context information scraped from a website. The site context should be taken as correct and questions from the customer should ONLY be answered from that pool of knowledge, not any prior information. When possible, answer the question with the URL link that is provided in the returned site context.",
                tools: [openApiToolDef]);
        }

        public void Dispose()
        {
            _initSemaphore?.Dispose();
        }
    }
}