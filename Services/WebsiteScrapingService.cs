using HtmlAgilityPack;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using static DBService;
using EasyAgent.Plugins;
using Microsoft.Extensions.Options;
using Azure.Identity;

namespace EasyAgent.Services
{
    public class WebsiteScrapingService : BackgroundService
    {
        private HttpClient httpClient;
        private HashSet<string> visitedUrls = new HashSet<string>();

        private DBService db;
        private SiteContextPlugin siteContextPlugin;
        private string starterUrl;

        public WebsiteScrapingService(IOptions<ChatbotConfiguration> config)
        {
            httpClient = new HttpClient();

            this.db = new DBService(config.Value.WEBSITE_EASYAGENT_SITECONTEXT_DB_ENDPOINT, new DefaultAzureCredential(), config.Value.WEBSITE_SITE_NAME, "base");
            this.starterUrl = config.Value.WEBSITE_HOSTNAME;
            this.siteContextPlugin = new SiteContextPlugin(config);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // This is to allow ExecuteAsync to go asynchronous, yielding the task back so that
            // StopAsync will not bomb out in the middle of the loop here.
            // By going async we'll also exit from a wait much faster, respecting the token cancellation.

            await Task.Yield();

            // This is the earliest we should perform anything that possibly takes a longer amount of time!
            await db.CreateFreshContainerAsync();

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        // Run this job if this is a multitenant stamp, the hosting configuration is on, and this instance has ownership
                        KickOffScraping(starterUrl);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Exception when attempting to scrape website: ", ex.ToString());
                    }

                    await Task.Delay(TimeSpan.FromHours(3), stoppingToken);
                }
                Console.WriteLine("Exiting WebsiteScrapingService ExecuteAsync because CancellationToken cancellation requested.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception inside WebsiteScrapingService ExecuteAsync: {ex}");
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            Console.WriteLine("Stopping the WebsiteScrapingService.");
            await base.StopAsync(cancellationToken);
        }

        public async Task KickOffScraping(string rootUrl, int maxDepth = 10)
        {
            Uri uri = new Uri(rootUrl);

            await ScrapeWebsiteAsync(rootUrl, maxDepth);
        }

        private async Task ScrapeWebsiteAsync(string url, int maxDepth, int currentDepth = 0)
        {
            if (visitedUrls.Contains(url) || currentDepth > maxDepth)
                return;

            visitedUrls.Add(url);

            Console.WriteLine($"**********Scraping {url} at depth {currentDepth}**********");

            try
            {
                var response = await httpClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Failed to retrieve {url}");
                    return;
                }

                var responseBody = await response.Content.ReadAsStringAsync();
                var htmlDocument = new HtmlDocument();
                htmlDocument.LoadHtml(responseBody);

                await ExtractChunks(url, htmlDocument.DocumentNode);

                var linkNodes = ExtractLinks(htmlDocument.DocumentNode, url); ;

                foreach (var link in linkNodes)
                {
                    await ScrapeWebsiteAsync(link, maxDepth, currentDepth + 1);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error scraping {url}: {ex.Message}");
            }
            finally
            {
            }
        }

        private IEnumerable<string> ExtractLinks(HtmlNode documentNode, string baseUrl)
        {
            var links = new List<string>();
            var linkNodes = documentNode.SelectNodes("//a[@href]");
            if (linkNodes != null)
            {
                foreach (var linkNode in linkNodes)
                {
                    var href = linkNode.GetAttributeValue("href", string.Empty);
                    if (!string.IsNullOrEmpty(href))
                    {
                        var absoluteUrl = GetAbsoluteUrl(baseUrl, href);
                        links.Add(absoluteUrl);
                    }
                }
            }
            return links;
        }

        private string GetAbsoluteUrl(string baseUrl, string relativeUrl)
        {
            if (Uri.TryCreate(relativeUrl, UriKind.Absolute, out var absoluteUri))
            {
                // If the URL is already absolute, return it as is  
                return absoluteUri.ToString();
            }

            // Otherwise, combine it with the base URL  
            var baseUri = new Uri(baseUrl);
            var combinedUri = new Uri(baseUri, relativeUrl);
            return combinedUri.ToString();
        }

        private async Task ExtractChunks(string url, HtmlNode node)
        {
            var ignoredTags = new HashSet<string> { "script", "style", "header", "footer", "nav" };
            var ignoredClassesAndIds = new HashSet<string> { "header", "footer", "nav", "toc", "table-of-contents" };

            if (ignoredTags.Contains(node.Name) ||
                (node.Attributes["class"] != null && ignoredClassesAndIds.Contains(node.Attributes["class"].Value.ToLower())) ||
                (node.Attributes["id"] != null && ignoredClassesAndIds.Contains(node.Attributes["id"].Value.ToLower())))
            {
                return;
            }

            var accumulatedText = new List<string>();

            async Task StoreAccumulatedChunksAsync()
            {
                if (accumulatedText.Count > 0)
                {
                    var combinedText = string.Join(" ", accumulatedText);
                    accumulatedText.Clear();

                    if (combinedText.Length > 7000 * 4)
                    {
                        Console.WriteLine($"Given chunk is too long, breaking down.");
                        int breakPoint = Math.Min(combinedText.IndexOf('.', 5000), 7000);
                        await StoreChunk(url, combinedText.Substring(0, breakPoint));
                        await StoreChunk(url, combinedText.Substring(breakPoint));
                    }
                    else
                    {
                        await StoreChunk(url, combinedText);
                    }
                }
            }

            async Task ProcessNodeAsync(HtmlNode currentNode)
            {
                if (ignoredTags.Contains(currentNode.Name) ||
                    (currentNode.Attributes["class"] != null && ignoredClassesAndIds.Contains(currentNode.Attributes["class"].Value.ToLower())) ||
                    (currentNode.Attributes["id"] != null && ignoredClassesAndIds.Contains(currentNode.Attributes["id"].Value.ToLower())))
                {
                    return;
                }

                if (currentNode.Name == "p" || currentNode.Name == "div")
                {
                    var text = HtmlEntity.DeEntitize(currentNode.InnerText.Trim());
                    if (!string.IsNullOrEmpty(text) && text.Any(char.IsLetterOrDigit))
                    {
                        var cleanedText = CleanWhitespace(text);
                        accumulatedText.Add(cleanedText);

                        var wordCount = accumulatedText.Sum(t => t.Split(' ').Length);
                        if (wordCount >= 200)
                        {
                            await StoreAccumulatedChunksAsync();
                        }
                    }
                }

                foreach (var childNode in currentNode.ChildNodes)
                {
                    await ProcessNodeAsync(childNode);
                }
            }

            await ProcessNodeAsync(node);

            // Final check to store any remaining accumulated text  
            await StoreAccumulatedChunksAsync();
        }

        private async Task StoreChunk(string url, string sentence)
        {
            var embedding = await siteContextPlugin.GenerateEmbedding(sentence);

            await db.AddEmbedding(new TextEmbeddingItem()
            {
                Id = Guid.NewGuid().ToString(),
                Url = url,
                TextHash = ComputeHash(sentence),
                Text = sentence,
                Embedding = embedding
            });

            Console.WriteLine($"Stored sentence: {sentence}.");
        }

        // Hash is used to check for duplicated text
        private string ComputeHash(string text)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(text));
                return BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
            }
        }

        private string CleanWhitespace(string input)
        {
            return Regex.Replace(input, @"\s+", " ").Trim();
        }
    }
}
