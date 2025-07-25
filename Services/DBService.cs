using Azure.Core;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;

public class DBService
{
    private CosmosClient client;
    private Database database;
    private Container container;

    public class TextEmbeddingItem
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        // Url and partitionKey for the CosmosDb
        public string Url { get; set; }

        public string Text { get; set; }

        public float[] Embedding { get; set; }

        public string TextHash { get; set; }

        public override string ToString()
        {
            return $"Id: {Id}, PartitionKey (Url): {Url}, TextHash: {TextHash}, Text: {Text.Substring(0, Math.Min(Text.Length, 100))}, Embedding: [{string.Join(", ", Embedding.Take(5).Select(e => e.ToString("F4")))}]";
        }
    }

    public DBService(string endpointUri, TokenCredential credential, string databaseId, string containerId)
    {
        this.client = new CosmosClient(endpointUri, credential);

        this.database = this.client.GetDatabase(databaseId);

        this.container = this.database.GetContainer(containerId);
    }

    public async Task<List<string>> GetNNearestTextsAndEmbeddingsAsync(float[] queryEmbedding, int topNResults = 5)
    {
        var queryDef = new QueryDefinition(
            query: "SELECT TOP @n c.Text, c.Url, VectorDistance(c.Embedding,@embedding) AS SimilarityScore FROM c ORDER BY VectorDistance(c.Embedding,@embedding)"
            ).WithParameter("@n", topNResults).WithParameter("@embedding", queryEmbedding);
        List<string> results = [];

        using FeedIterator<TextEmbeddingItem> feed = container.GetItemQueryIterator<TextEmbeddingItem>(
            queryDefinition: queryDef
        );

        while (feed.HasMoreResults)
        {
            FeedResponse<TextEmbeddingItem> response = await feed.ReadNextAsync();
            foreach (TextEmbeddingItem item in response)
            {
                results.Add(item.Text + "from URL: " + item.Url);
            }
        }

        return results;
    }
}