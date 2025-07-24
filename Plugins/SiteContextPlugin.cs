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
            DBService dbService = new DBService(_config.WEBSITE_EASYAGENT_SITECONTEXT_DB_ENDPOINT, _credential, _config.WEBSITE_SITE_NAME, "base");

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
