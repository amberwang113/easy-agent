using Azure.AI.Agents.Persistent;
using Azure.AI.Projects;
using Azure.Core;
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
        private readonly TokenCredential _credential;
        private readonly SemaphoreSlim _initSemaphore = new(1, 1);
        
        private PersistentAgentsClient? _agentsClient;
        private PersistentAgent? _agent;
        private bool _isInitialized = false;

        private const string SYSTEM = "You're an agent in charge of responding to customer questions and performing actions. You may use site context information to help if necessary. The site context should be taken as correct and questions from the customer should ONLY be answered from that pool of knowledge, not any prior information. When providing URL links from site context, always choose the most specific page available. For example, if information about London appears on both a /destinations page and a /destinations/london page, link to /destinations/london. Prefer deeper, topic-specific URLs over general overview or listing pages. Return your answers with proper whitespace like newlines -- it will NOT be rendered to markdown.";

        public AgentService(IOptions<ChatbotConfiguration> config)
        {
            _config = config.Value;
            TokenCredential credential = !string.IsNullOrEmpty(config.Value.WEBSITE_MANAGED_CLIENT_ID)
            ? new ManagedIdentityCredential(config.Value.WEBSITE_MANAGED_CLIENT_ID)
            : new DefaultAzureCredential();
            this._credential = credential;
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

                // Determine auth for OpenAPI tool calls back to the website.
                // When EasyAuth is enabled, use managed identity auth with the site URL as audience.
                // When EasyAuth is not enabled, use anonymous (no auth).
                bool easyAuthEnabled = string.Equals(_config.WEBSITE_AUTH_ENABLED, "True", StringComparison.OrdinalIgnoreCase);
                OpenApiAuthDetails openApiAuth = easyAuthEnabled
                    ? new OpenApiManagedAuthDetails(
                        securityScheme: new OpenApiManagedSecurityScheme(
                            audience: $"https://{_config.WEBSITE_SITE_NAME}.azurewebsites.net"))
                    : new OpenApiAnonymousAuthDetails();

                var aClient = new AIProjectClient(new Uri(_config.WEBSITE_EASYAGENT_FOUNDRY_ENDPOINT), _credential);
                var eClient = aClient.GetAzureOpenAIChatClient(deploymentName: _config.WEBSITE_EASYAGENT_FOUNDRY_CHAT_MODEL);

                var res = await eClient.CompleteChatAsync(
                    "Summarize this open api spec with what it appears to be doing in just a few words. I'll tip you $1000 if you keep it short and sweet but descriptive! This summary will be used as a tool name for another agent. For example, something like manage_fashion_store or handle_service_calls. Please return SOLELY the description. Here's the spec: " +
                    _config.WEBSITE_EASYAGENT_FOUNDRY_OPENAPISPEC);

                string summary = res.Value.Content[0].Text ?? "webapp_assistant_tool";

                var spec = BinaryData.FromString(_config.WEBSITE_EASYAGENT_FOUNDRY_OPENAPISPEC);

                var openApiToolDef = new OpenApiToolDefinition(
                    name: summary,
                    description: summary,
                    spec: spec,
                    openApiAuthentication: openApiAuth,
                    defaultParams: ["format"]
                );

                if (!string.IsNullOrEmpty(_config.WEBSITE_EASYAGENT_FOUNDRY_AGENTID))
                {
                    _agent = await UpdateAgentAsync(openApiToolDef);
                }
                else
                {
                    _agent = await CreateNewAgentAsync(openApiToolDef);
                }

                _isInitialized = true;
            }
            finally
            {
                _initSemaphore.Release();
            }
        }

        private async Task<PersistentAgent> UpdateAgentAsync(OpenApiToolDefinition openApiToolDef)
        {
            return await _agentsClient.Administration.UpdateAgentAsync(
                assistantId: _config.WEBSITE_EASYAGENT_FOUNDRY_AGENTID,
                model: _config.WEBSITE_EASYAGENT_FOUNDRY_CHAT_MODEL,
                name: "Webapp Assistant",
                instructions: SYSTEM,
                tools: [ openApiToolDef ] );
        }

        private async Task<PersistentAgent> CreateNewAgentAsync(OpenApiToolDefinition openApiToolDef)
        {
            return await _agentsClient!.Administration.CreateAgentAsync(
                model: _config.WEBSITE_EASYAGENT_FOUNDRY_CHAT_MODEL,
                name: "Webapp Assistant",
                instructions: SYSTEM,
                tools: [openApiToolDef]);
        }

        public void Dispose()
        {
            _initSemaphore?.Dispose();
        }
    }
}