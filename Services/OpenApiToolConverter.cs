using Azure.AI.Agents.Persistent;
using Newtonsoft.Json.Linq;

namespace EasyAgent.Services
{
    /// <summary>
    /// Describes the HTTP details for a single tool derived from an OpenAPI operation.
    /// </summary>
    public sealed class ToolHttpInfo
    {
        public string HttpMethod { get; init; } = string.Empty;
        public string UrlTemplate { get; init; } = string.Empty;
        public string BaseUrl { get; init; } = string.Empty;
    }

    /// <summary>
    /// Converts an OpenAPI specification into <see cref="FunctionToolDefinition"/>s that can be
    /// registered on a Foundry agent, plus a mapping from tool name to HTTP details so
    /// <see cref="ToolCallExecutor"/> can execute the calls locally.
    /// </summary>
    public static class OpenApiToolConverter
    {
        /// <summary>
        /// Parses the provided OpenAPI spec JSON and produces function tool definitions
        /// along with a dictionary that maps each tool name to its HTTP info.
        /// </summary>
        public static (IReadOnlyList<FunctionToolDefinition> Tools, IReadOnlyDictionary<string, ToolHttpInfo> ToolMap) Convert(string openApiSpecJson)
        {
            var spec = JObject.Parse(openApiSpecJson);
            var tools = new List<FunctionToolDefinition>();
            var toolMap = new Dictionary<string, ToolHttpInfo>(StringComparer.OrdinalIgnoreCase);

            string baseUrl = ResolveBaseUrl(spec);

            // Pre-resolve all $ref pointers so downstream code sees fully expanded schemas
            ResolveRefs(spec, spec);

            var paths = spec["paths"] as JObject;
            if (paths == null)
                return (tools, toolMap);

            foreach (var pathProperty in paths.Properties())
            {
                string pathTemplate = pathProperty.Name; // e.g. "/alerts/{id}"
                var pathItem = pathProperty.Value as JObject;
                if (pathItem == null)
                    continue;

                foreach (var methodProperty in pathItem.Properties())
                {
                    string httpMethod = methodProperty.Name.ToUpperInvariant();
                    if (!IsHttpMethod(httpMethod))
                        continue;

                    var operation = methodProperty.Value as JObject;
                    if (operation == null)
                        continue;

                    string operationId = operation["operationId"]?.ToString()
                        ?? GenerateOperationId(httpMethod, pathTemplate);

                    string description = operation["summary"]?.ToString()
                        ?? operation["description"]?.ToString()
                        ?? operationId;

                    var parametersSchema = BuildParametersSchema(operation, pathTemplate);

                    var toolDef = new FunctionToolDefinition(
                        name: operationId,
                        description: description,
                        parameters: BinaryData.FromString(parametersSchema.ToString()));

                    tools.Add(toolDef);
                    toolMap[operationId] = new ToolHttpInfo
                    {
                        HttpMethod = httpMethod,
                        UrlTemplate = pathTemplate,
                        BaseUrl = baseUrl
                    };
                }
            }

            return (tools, toolMap);
        }

        private static string ResolveBaseUrl(JObject spec)
        {
            // OpenAPI 3.x — servers[0].url
            var servers = spec["servers"] as JArray;
            if (servers?.Count > 0)
            {
                string? url = servers[0]?["url"]?.ToString();
                if (!string.IsNullOrEmpty(url))
                    return url.TrimEnd('/');
            }

            // Swagger 2.0 — host + basePath
            string? host = spec["host"]?.ToString();
            if (!string.IsNullOrEmpty(host))
            {
                string basePath = spec["basePath"]?.ToString()?.TrimEnd('/') ?? string.Empty;
                string scheme = (spec["schemes"] as JArray)?.FirstOrDefault()?.ToString() ?? "https";
                return $"{scheme}://{host}{basePath}";
            }

            return string.Empty;
        }

        /// <summary>
        /// Builds a JSON Schema object describing all parameters (path, query, header, and request body)
        /// for a single OpenAPI operation.
        /// </summary>
        private static JObject BuildParametersSchema(JObject operation, string pathTemplate)
        {
            var properties = new JObject();
            var required = new JArray();

            // Path, query, header parameters
            var parameters = operation["parameters"] as JArray;
            if (parameters != null)
            {
                foreach (var param in parameters)
                {
                    string? name = param["name"]?.ToString();
                    if (string.IsNullOrEmpty(name))
                        continue;

                    var paramSchema = param["schema"] as JObject;
                    var propDef = new JObject();

                    if (paramSchema != null)
                    {
                        propDef["type"] = paramSchema["type"]?.ToString() ?? "string";
                        if (paramSchema["format"] != null)
                            propDef["format"] = paramSchema["format"]!.ToString();
                        if (paramSchema["enum"] != null)
                            propDef["enum"] = paramSchema["enum"]!.DeepClone();
                    }
                    else
                    {
                        // Swagger 2.0 — type is on the parameter itself
                        propDef["type"] = param["type"]?.ToString() ?? "string";
                        if (param["format"] != null)
                            propDef["format"] = param["format"]!.ToString();
                        if (param["enum"] != null)
                            propDef["enum"] = param["enum"]!.DeepClone();
                    }

                    string? desc = param["description"]?.ToString();
                    if (!string.IsNullOrEmpty(desc))
                        propDef["description"] = desc;

                    properties[name] = propDef;

                    bool isRequired = param["required"]?.Value<bool>() == true;
                    if (isRequired)
                        required.Add(name);
                }
            }

            // Request body (OpenAPI 3.x)
            var requestBody = operation["requestBody"] as JObject;
            if (requestBody != null)
            {
                var content = requestBody["content"] as JObject;
                var jsonContent = content?["application/json"] as JObject;
                var bodySchema = jsonContent?["schema"] as JObject;

                if (bodySchema != null)
                {
                    // Inline the body schema properties directly into the parameter schema.
                    // This flattening keeps the function tool interface simple for the LLM.
                    var bodyProps = bodySchema["properties"] as JObject;
                    if (bodyProps != null)
                    {
                        foreach (var bp in bodyProps.Properties())
                        {
                            properties[bp.Name] = bp.Value.DeepClone();
                        }

                        var bodyRequired = bodySchema["required"] as JArray;
                        if (bodyRequired != null)
                        {
                            foreach (var r in bodyRequired)
                                required.Add(r.ToString());
                        }
                    }
                    else
                    {
                        // Non-object body — wrap as a single "body" parameter
                        properties["body"] = bodySchema.DeepClone();
                        if (requestBody["required"]?.Value<bool>() == true)
                            required.Add("body");
                    }
                }
            }

            // Swagger 2.0 — "in": "body" parameters
            if (parameters != null)
            {
                foreach (var param in parameters)
                {
                    if (param["in"]?.ToString() != "body")
                        continue;

                    var bodySchema = param["schema"] as JObject;
                    if (bodySchema == null)
                        continue;

                    var bodyProps = bodySchema["properties"] as JObject;
                    if (bodyProps != null)
                    {
                        foreach (var bp in bodyProps.Properties())
                            properties[bp.Name] = bp.Value.DeepClone();

                        var bodyRequired = bodySchema["required"] as JArray;
                        if (bodyRequired != null)
                        {
                            foreach (var r in bodyRequired)
                                required.Add(r.ToString());
                        }
                    }
                    else
                    {
                        string paramName = param["name"]?.ToString() ?? "body";
                        properties[paramName] = bodySchema.DeepClone();
                        if (param["required"]?.Value<bool>() == true)
                            required.Add(paramName);
                    }
                }
            }

            var schema = new JObject
            {
                ["type"] = "object",
                ["properties"] = properties
            };

            if (required.Count > 0)
                schema["required"] = required;

            return schema;
        }

        /// <summary>
        /// Recursively walks the JSON tree and replaces any <c>{"$ref": "#/..."}</c> objects
        /// with a deep clone of the target they point to within <paramref name="root"/>.
        /// </summary>
        private static void ResolveRefs(JToken token, JObject root)
        {
            if (token is JObject obj)
            {
                // Collect properties that are $ref objects to replace in-place
                var refProps = obj.Properties()
                    .Where(p => p.Value is JObject child && child["$ref"] != null)
                    .ToList();

                foreach (var prop in refProps)
                {
                    var refPath = ((JObject)prop.Value)["$ref"]!.ToString();
                    var resolved = ResolveJsonPointer(root, refPath);
                    if (resolved != null)
                        prop.Value = resolved.DeepClone();
                }

                // Also handle the case where the object itself is a $ref (e.g. inside an array)
                // — handled by the parent. Now recurse into children.
                foreach (var prop in obj.Properties().ToList())
                    ResolveRefs(prop.Value, root);
            }
            else if (token is JArray arr)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is JObject item && item["$ref"] != null)
                    {
                        var resolved = ResolveJsonPointer(root, item["$ref"]!.ToString());
                        if (resolved != null)
                            arr[i] = resolved.DeepClone();
                    }
                    else
                    {
                        ResolveRefs(arr[i], root);
                    }
                }
            }
        }

        /// <summary>
        /// Resolves a JSON Pointer like <c>#/components/schemas/NewAlert</c> within the spec root.
        /// </summary>
        private static JToken? ResolveJsonPointer(JObject root, string pointer)
        {
            if (!pointer.StartsWith("#/"))
                return null;

            string[] segments = pointer[2..].Split('/');
            JToken current = root;
            foreach (var segment in segments)
            {
                current = current[segment]!;
                if (current == null)
                    return null;
            }
            return current;
        }

        private static bool IsHttpMethod(string value)
        {
            return value is "GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS";
        }

        private static string GenerateOperationId(string method, string path)
        {
            // Produce a readable fallback like "get_alerts_id" from "GET /alerts/{id}"
            string cleaned = path
                .Replace("{", "")
                .Replace("}", "")
                .Replace("/", "_")
                .Trim('_');

            return $"{method.ToLowerInvariant()}_{cleaned}";
        }
    }
}
