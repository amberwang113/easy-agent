using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

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

            // TODO: Don't hardcode the container name "base"
            string dbName = _config.WEBSITE_EASYAGENT_SITECONTEXT_DB_NAME;
            DBService dbService = new DBService(string.IsNullOrEmpty(dbName) ? _config.WEBSITE_EASYAGENT_SITECONTEXT_DB_ENDPOINT : dbName, _credential, _config.WEBSITE_SITE_NAME, "base");

            var qEmbedding = await GenerateEmbedding(question);

            string context = string.Join(",", await dbService.GetNNearestTextsAndEmbeddingsAsync(qEmbedding));

            return context;
        }

        public async Task<float[]> GenerateEmbedding(string sentence)
        {
            var aClient = new AIProjectClient(new Uri(_config.WEBSITE_EASYAGENT_FOUNDRY_ENDPOINT), _credential);

            var eClient = aClient.GetAzureOpenAIEmbeddingClient(deploymentName: _config.WEBSITE_EASYAGENT_FOUNDRY_EMBEDDING_MODEL);

            var embedding = eClient.GenerateEmbedding(sentence);

            return embedding.Value.ToFloats().ToArray();
        }
    }
}
