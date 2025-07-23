using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using EasyAgent;

namespace EasyAgent.Plugins
{
    public class SiteContextPlugin
    {
        private DefaultAzureCredential _credential;
        private ChatbotConfiguration _config;

        public SiteContextPlugin(IOptions<ChatbotConfiguration> config)
        {
            this._credential = new DefaultAzureCredential();
            this._config = config.Value;
        }

        [KernelFunction("request_more_information_from_site_context")]
        public async Task<string> RequestMoreInformation(string question)
        {
            if(string.IsNullOrEmpty(question))
            {
                return string.Empty;
            }

            // TODO: Don't hardcode the db name (site)
            DBService dbService = new DBService(_config.WEBAPP_EASYAGENT_DB_ENDPOINT, _credential, "testFailover-vectors", "base");

            var aClient = new AIProjectClient(new Uri(_config.WEBAPP_EASYAGENT_FOUNDRY_ENDPOINT), _credential);

            var eClient = aClient.GetAzureOpenAIEmbeddingClient(deploymentName: _config.WEBAPP_EASYAGENT_EMBEDDING_MODEL);

            var qEmbedding = eClient.GenerateEmbedding(question);

            string context = string.Join(",", await dbService.GetNNearestTextsAndEmbeddingsAsync(qEmbedding.Value.ToFloats().ToArray()));

            return context;
        }
    }
}
