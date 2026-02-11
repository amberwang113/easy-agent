using Azure.AI.Agents.Persistent;
using Azure.AI.Projects;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;

namespace EasyAgent.Services
{
    public interface IAgentService
    {
        /// <summary>
        /// Gets a <see cref="PersistentAgentsClient"/> authenticated with managed identity.
        /// </summary>
        Task<PersistentAgentsClient> GetAgentsClientAsync();

        /// <summary>
        /// Returns the cached Foundry agent definition (created or updated on first call).
        /// </summary>
        Task<PersistentAgent> GetAgentAsync();

        /// <summary>
        /// Returns the mapping from function-tool name to HTTP details built from the
        /// OpenAPI spec. Available after initialization completes.
        /// </summary>
        IReadOnlyDictionary<string, ToolHttpInfo> ToolMap { get; }
    }

    public sealed class AgentService : IAgentService, IDisposable
    {
        private readonly ChatbotConfiguration _config;
        private readonly ILogger<AgentService> _logger;
        private readonly TokenCredential _defaultCredential;
        private readonly SemaphoreSlim _initSemaphore = new(1, 1);

        private PersistentAgent? _agent;
        private IReadOnlyDictionary<string, ToolHttpInfo>? _toolMap;
        private bool _isInitialized;

        private const string SystemPrompt =
            "You're an agent in charge of responding to customer questions and performing actions. " +
            "You may use site context information to help if necessary. The site context should be taken " +
            "as correct and questions from the customer should ONLY be answered from that pool of knowledge, " +
            "not any prior information. When providing URL links from site context, always choose the most " +
            "specific page available. For example, if information about London appears on both a /destinations " +
            "page and a /destinations/london page, link to /destinations/london. Prefer deeper, topic-specific " +
            "URLs over general overview or listing pages. Return your answers with proper whitespace like " +
            "newlines -- it will NOT be rendered to markdown.";

        public AgentService(IOptions<ChatbotConfiguration> config, ILogger<AgentService> logger)
        {
            _config = config.Value;
            _logger = logger;
            _defaultCredential = !string.IsNullOrEmpty(_config.WEBSITE_MANAGED_CLIENT_ID)
                ? new ManagedIdentityCredential(_config.WEBSITE_MANAGED_CLIENT_ID)
                : new DefaultAzureCredential();
        }

        public IReadOnlyDictionary<string, ToolHttpInfo> ToolMap
        {
            get
            {
                if (!_isInitialized)
                    throw new InvalidOperationException("AgentService has not been initialized yet. Call GetAgentAsync() first.");
                return _toolMap!;
            }
        }

        public async Task<PersistentAgentsClient> GetAgentsClientAsync()
        {
            await EnsureInitializedAsync();
            return new PersistentAgentsClient(_config.WEBSITE_EASYAGENT_FOUNDRY_ENDPOINT, _defaultCredential);
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

                _logger.LogInformation("Initializing AgentService...");

                var defaultClient = new PersistentAgentsClient(
                    _config.WEBSITE_EASYAGENT_FOUNDRY_ENDPOINT, _defaultCredential);

                // Convert the OpenAPI spec into function tool definitions so we can execute
                // the HTTP calls ourselves instead of relying on Foundry callbacks.
                IReadOnlyList<ToolDefinition> toolDefinitions;
                if (!string.IsNullOrEmpty(_config.WEBSITE_EASYAGENT_FOUNDRY_OPENAPISPEC))
                {
                    var (tools, toolMap) = OpenApiToolConverter.Convert(_config.WEBSITE_EASYAGENT_FOUNDRY_OPENAPISPEC);
                    _toolMap = toolMap;
                    toolDefinitions = tools.Cast<ToolDefinition>().ToList();

                    _logger.LogInformation("Converted OpenAPI spec into {Count} function tools: {Names}",
                        tools.Count, string.Join(", ", toolMap.Keys));
                }
                else
                {
                    _toolMap = new Dictionary<string, ToolHttpInfo>();
                    toolDefinitions = Array.Empty<ToolDefinition>();

                    _logger.LogInformation("No OpenAPI spec configured. Agent will have no API tools");
                }

                // Build the agent instructions. When an OpenAPI spec is present, ask the LLM for
                // a short summary to include in the instructions so the agent understands its tools.
                string instructions = SystemPrompt;
                if (toolDefinitions.Count > 0)
                {
                    try
                    {
                        var projectClient = new AIProjectClient(
                            new Uri(_config.WEBSITE_EASYAGENT_FOUNDRY_ENDPOINT), _defaultCredential);
                        var chatClient = projectClient.GetAzureOpenAIChatClient(
                            deploymentName: _config.WEBSITE_EASYAGENT_FOUNDRY_CHAT_MODEL);

                        var summaryResponse = await chatClient.CompleteChatAsync(
                            "Summarize this OpenAPI spec with what it appears to be doing in just a few words. " +
                            "Keep it short and descriptive � this will be appended to an agent's instructions. " +
                            "Return SOLELY the description. Here's the spec: " +
                            _config.WEBSITE_EASYAGENT_FOUNDRY_OPENAPISPEC);

                        string summary = summaryResponse.Value.Content[0].Text ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(summary))
                            instructions += $"\n\nYou also have tools that let you {summary}.";
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to generate spec summary � continuing without it");
                    }
                }

                if (!string.IsNullOrEmpty(_config.WEBSITE_EASYAGENT_FOUNDRY_AGENTID))
                {
                    _agent = await defaultClient.Administration.UpdateAgentAsync(
                        assistantId: _config.WEBSITE_EASYAGENT_FOUNDRY_AGENTID,
                        model: _config.WEBSITE_EASYAGENT_FOUNDRY_CHAT_MODEL,
                        name: "Webapp Assistant",
                        instructions: instructions,
                        tools: toolDefinitions);

                    _logger.LogInformation("Updated existing agent {AgentId}", _agent.Id);
                }
                else
                {
                    _agent = await defaultClient.Administration.CreateAgentAsync(
                        model: _config.WEBSITE_EASYAGENT_FOUNDRY_CHAT_MODEL,
                        name: "Webapp Assistant",
                        instructions: instructions,
                        tools: toolDefinitions);

                    _logger.LogInformation("Created new agent {AgentId}", _agent.Id);
                }

                _isInitialized = true;
            }
            finally
            {
                _initSemaphore.Release();
            }
        }

        public void Dispose()
        {
            _initSemaphore.Dispose();
        }
    }
}