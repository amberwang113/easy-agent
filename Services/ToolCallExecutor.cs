using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;
using System.Text;

namespace EasyAgent.Services
{
    /// <summary>
    /// Executes HTTP calls on behalf of the agent when a Foundry run enters
    /// the <c>RequiresAction</c> state. Because EasyAgent runs as a site extension
    /// on the same App Service, tool calls target the host site's own API.
    /// Authentication is handled by transparently forwarding all relevant headers
    /// (Cookie, EasyAuth, Authorization) from the original request so the call
    /// carries the customer's identity through to the host site.
    /// </summary>
    public sealed class ToolCallExecutor : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<ToolCallExecutor> _logger;

        /// <summary>
        /// Headers to forward from the incoming request to the target API.
        /// Matches EasyMCP's ApiProxyService allowlist.
        /// </summary>
        private static readonly HashSet<string> HeadersToForward = new(StringComparer.OrdinalIgnoreCase)
        {
            // Standard auth headers
            "Authorization",
            "Cookie",

            // Azure EasyAuth headers
            "X-MS-TOKEN-AAD-ACCESS-TOKEN",
            "X-MS-TOKEN-AAD-ID-TOKEN",
            "X-MS-TOKEN-AAD-REFRESH-TOKEN",
            "X-MS-CLIENT-PRINCIPAL",
            "X-MS-CLIENT-PRINCIPAL-ID",
            "X-MS-CLIENT-PRINCIPAL-NAME",
            "X-MS-CLIENT-PRINCIPAL-IDP",

            // Forwarding headers
            "X-Forwarded-For",
            "X-Forwarded-Host",
            "X-Forwarded-Proto",
            "X-Real-IP",
        };

        public ToolCallExecutor(HttpClient httpClient, ILogger<ToolCallExecutor> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        /// <summary>
        /// Executes a single tool call and returns the response body as a string
        /// the agent can reason about.
        /// </summary>
        /// <param name="toolName">The function tool name (operationId).</param>
        /// <param name="argumentsJson">JSON string of arguments provided by the agent.</param>
        /// <param name="toolMap">Mapping from tool name to HTTP details.</param>
        /// <param name="incomingHeaders">The headers from the original inbound request, forwarded transparently.</param>
        public async Task<string> ExecuteAsync(
            string toolName,
            string argumentsJson,
            IReadOnlyDictionary<string, ToolHttpInfo> toolMap,
            IHeaderDictionary? incomingHeaders = null)
        {
            if (!toolMap.TryGetValue(toolName, out var httpInfo))
            {
                _logger.LogWarning("Tool '{ToolName}' not found in tool map", toolName);
                return $"Error: unknown tool '{toolName}'.";
            }

            try
            {
                var arguments = string.IsNullOrWhiteSpace(argumentsJson)
                    ? new JObject()
                    : JObject.Parse(argumentsJson);

                string url = BuildUrl(httpInfo, arguments);

                using var request = new HttpRequestMessage(new HttpMethod(httpInfo.HttpMethod), url);

                // Forward all relevant auth headers from the original request.
                // This mirrors EasyMCP's ApiProxyService approach: forward Cookie,
                // all X-MS-* EasyAuth headers, and Authorization transparently.
                // If no Authorization header exists but we have an EasyAuth access
                // token, promote it to a Bearer Authorization header.
                bool hasAuthorizationHeader = false;
                string? easyAuthAccessToken = null;

                if (incomingHeaders != null)
                {
                    hasAuthorizationHeader = incomingHeaders.ContainsKey("Authorization");
                    easyAuthAccessToken = incomingHeaders["X-MS-TOKEN-AAD-ACCESS-TOKEN"].FirstOrDefault();

                    foreach (var header in incomingHeaders)
                    {
                        if (HeadersToForward.Contains(header.Key) || header.Key.StartsWith("X-MS-", StringComparison.OrdinalIgnoreCase))
                        {
                            request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                            _logger.LogDebug("Forwarding header: {HeaderName}", header.Key);
                        }
                    }

                    // If no Authorization header but we have EasyAuth AAD token, add it as Bearer.
                    // This handles browser cookie-based EasyAuth sessions where the target API
                    // expects Bearer token authentication.
                    if (!hasAuthorizationHeader && !string.IsNullOrEmpty(easyAuthAccessToken))
                    {
                        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {easyAuthAccessToken}");
                        _logger.LogDebug("Added Authorization header from X-MS-TOKEN-AAD-ACCESS-TOKEN");
                    }
                }

                // For methods that accept a body, send remaining arguments as JSON
                if (httpInfo.HttpMethod is "POST" or "PUT" or "PATCH")
                {
                    var bodyArgs = RemovePathParameters(httpInfo.UrlTemplate, arguments);
                    if (bodyArgs.HasValues)
                        request.Content = new StringContent(bodyArgs.ToString(), Encoding.UTF8, "application/json");
                }

                _logger.LogInformation("Executing tool '{ToolName}': {Method} {Url}", toolName, httpInfo.HttpMethod, url);

                using var response = await _httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Tool '{ToolName}' returned {StatusCode}: {Body}",
                        toolName, (int)response.StatusCode, responseBody);
                    return $"HTTP {(int)response.StatusCode}: {responseBody}";
                }

                return string.IsNullOrWhiteSpace(responseBody) ? "(empty response)" : responseBody;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing tool '{ToolName}'", toolName);
                return $"Error calling {toolName}: {ex.Message}";
            }
        }

        /// <summary>
        /// Constructs the full URL by substituting path parameters and appending query parameters.
        /// </summary>
        private static string BuildUrl(ToolHttpInfo httpInfo, JObject arguments)
        {
            string path = httpInfo.UrlTemplate;

            // Substitute path parameters (e.g., {id})
            foreach (var prop in arguments.Properties().ToList())
            {
                string placeholder = $"{{{prop.Name}}}";
                if (path.Contains(placeholder, StringComparison.OrdinalIgnoreCase))
                {
                    path = path.Replace(placeholder, Uri.EscapeDataString(prop.Value.ToString()));
                }
            }

            string baseUrl = httpInfo.BaseUrl.TrimEnd('/');
            string fullUrl = $"{baseUrl}{path}";

            // For GET/DELETE/HEAD, append unused arguments as query parameters
            if (httpInfo.HttpMethod is "GET" or "DELETE" or "HEAD" or "OPTIONS")
            {
                var queryArgs = RemovePathParameters(httpInfo.UrlTemplate, arguments);
                if (queryArgs.HasValues)
                {
                    var queryParts = queryArgs.Properties()
                        .Select(p => $"{Uri.EscapeDataString(p.Name)}={Uri.EscapeDataString(p.Value.ToString())}");
                    fullUrl += "?" + string.Join("&", queryParts);
                }
            }

            return fullUrl;
        }

        /// <summary>
        /// Returns a copy of <paramref name="arguments"/> with path-parameter keys removed.
        /// </summary>
        private static JObject RemovePathParameters(string urlTemplate, JObject arguments)
        {
            var result = new JObject();
            foreach (var prop in arguments.Properties())
            {
                string placeholder = $"{{{prop.Name}}}";
                if (!urlTemplate.Contains(placeholder, StringComparison.OrdinalIgnoreCase))
                    result[prop.Name] = prop.Value.DeepClone();
            }
            return result;
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }
    }
}
