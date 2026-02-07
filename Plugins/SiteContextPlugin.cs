using Azure.AI.Projects;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace EasyAgent.Plugins
{
    public class SiteContextPlugin
    {
        private TokenCredential _credential;
        private ChatbotConfiguration _config;

        public SiteContextPlugin(IOptions<ChatbotConfiguration> config)
        {
            TokenCredential credential = !string.IsNullOrEmpty(config.Value.WEBSITE_MANAGED_CLIENT_ID)
            ? new ManagedIdentityCredential(config.Value.WEBSITE_MANAGED_CLIENT_ID)
            : new DefaultAzureCredential();
            this._credential = credential;
            this._config = config.Value;
        }

        [KernelFunction("request_more_information_from_site_context")]
        public async Task<string> RequestMoreInformation(string question)
        {
            if(string.IsNullOrEmpty(question))
            {
                return string.Empty;
            }

            // TODO: Don't hardcode "base" as container name
            string dbName = string.IsNullOrEmpty(_config.WEBSITE_EASYAGENT_SITECONTEXT_DB_NAME) ? _config.WEBSITE_SITE_NAME + "-EasyAgent" : _config.WEBSITE_EASYAGENT_SITECONTEXT_DB_NAME;
            DBService dbService = new DBService(_config.WEBSITE_EASYAGENT_SITECONTEXT_DB_ENDPOINT, _credential, dbName, "base");

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
