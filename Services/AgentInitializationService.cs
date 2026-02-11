namespace EasyAgent.Services
{
    /// <summary>
    /// Triggers eager initialization of the <see cref="IAgentService"/> at application startup
    /// so that the Foundry agent and OpenAPI tool map are ready before the first chat request.
    /// </summary>
    public sealed class AgentInitializationService : BackgroundService
    {
        private readonly IAgentService _agentService;
        private readonly ILogger<AgentInitializationService> _logger;

        public AgentInitializationService(IAgentService agentService, ILogger<AgentInitializationService> logger)
        {
            _agentService = agentService;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                _logger.LogInformation("Eagerly initializing AgentService...");
                await _agentService.GetAgentAsync();
                _logger.LogInformation("AgentService initialization complete.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize AgentService at startup.");
            }
        }
    }
}
