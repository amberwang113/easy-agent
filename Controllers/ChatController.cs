using AIChatbotWithRag.Models;
using Azure.AI.Agents.Persistent;
using Azure.AI.Projects;
using Azure.Identity;
using EasyAgent;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenAI.Embeddings;
using System.ClientModel;
using System.Text.Json;

namespace AIChatbotWithRag.Controllers
{
    [ApiController]
    [Route("/")]
    public class ChatController : Controller
    {
        private DefaultAzureCredential _credential;
        private readonly ChatbotConfiguration _config;
        PersistentAgentsClient _agentsClient;
        PersistentAgent _agent;

        public ChatController(IOptions<ChatbotConfiguration> options)
        {
            _config = options.Value;
            _credential = new DefaultAzureCredential();
        }

        private async Task EnsureInitialized()
        {
            if (_agentsClient != null)
            {
                return;
            }

            _agentsClient = new(_config.WEBAPP_EASYAGENT_FOUNDRY_ENDPOINT, _credential);

            if (!string.IsNullOrEmpty(_config.WEBAPP_EASYAGENT_FOUNDRY_AGENTID))
            {
                _agent = await _agentsClient.Administration.GetAgentAsync(_config.WEBAPP_EASYAGENT_FOUNDRY_AGENTID);
                return;
            }

            // TODO: Delete this and put in setup
            FunctionToolDefinition requestMoreInformationTool = new(
                name: "requestMoreInformationFromSiteContext",
                description: "Get information from site context with vector similarity search for a provided question.",
                parameters: BinaryData.FromObjectAsJson(
                    new
                    {
                        Type = "object",
                        Properties = new
                        {
                            Question = new
                            {
                                Type = "string",
                                Description = "Question or phrase set to search on."
                            }
                        },
                        Required = new[] { "question" }
                    },
                    new JsonSerializerOptions() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

            BinaryData spec = BinaryData.FromString(_config.WEBAPP_EASYAGENT_FOUNDRY_OPENAPISPEC);

            OpenApiAnonymousAuthDetails openApiAnonAuth = new();

            OpenApiToolDefinition openApiToolDef = new(
                name: "manage_fashion_store",
                description: "Manage fashion store inventory",
                spec: spec,
                openApiAuthentication: openApiAnonAuth,
                defaultParams: ["format"]
            );

            _agent = await _agentsClient.Administration.CreateAgentAsync(
                model: "gpt-4.1-2",
                name: "Open API Tool Calling Agent",
                instructions: "You're a chatbot in charge of responding to customer questions based on site context information scraped from a website. If necessary, call the provided RequestMoreInformationFromSiteContext tool to get necessary site information using the provided question. The site information provided should be taken as correct and questions from the customer should ONLY be answered from that pool of knowledge, not any prior information. When possible, answer the question with the URL link that is provided in the site context.",
                tools: [openApiToolDef, requestMoreInformationTool]);
        }


        [HttpPost]
        public async Task<IActionResult> Query([FromBody] ChatMessage chatMessage)
        {
            try
            {
                await EnsureInitialized();

                return Ok(await CallAIFoundryAgent(chatMessage.Content, chatMessage.SessionId));
            }
            catch (Exception e)
            {
                Console.WriteLine($"Exception during request: {e}");
                return Ok(new ChatMessage() { Content = $"Exception during request: {e}", SessionId = null });
            }
        }

        private async Task<ChatMessage> CallAIFoundryAgent(string userMessage, string threadId)
        {
            PersistentAgentThread thread = threadId == null ? await _agentsClient.Threads.CreateThreadAsync() : await _agentsClient.Threads.GetThreadAsync(threadId);

            PersistentThreadMessage message = await _agentsClient.Messages.CreateMessageAsync(
                thread.Id,
                MessageRole.User,
                userMessage);

            List<ToolOutput> toolOutputs = [];
            ThreadRun streamRun = default;

            AsyncCollectionResult<StreamingUpdate> stream = _agentsClient.Runs.CreateRunStreamingAsync(thread.Id, _agent.Id);

            do
            {
                toolOutputs.Clear();
                await foreach (StreamingUpdate streamingUpdate in stream)
                {
                    if (streamingUpdate.UpdateKind == StreamingUpdateReason.RunCreated)
                    {
                        Console.WriteLine($" --- Run started for thread id {thread.Id} _agent id {_agent.Id} ---");
                    }
                    else if (streamingUpdate is RequiredActionUpdate submitToolOutputsUpdate)
                    {
                        RequiredActionUpdate newActionUpdate = submitToolOutputsUpdate;
                        toolOutputs.Add(await
                            GetResolvedToolOutput(
                                newActionUpdate.FunctionName,
                                newActionUpdate.ToolCallId,
                                newActionUpdate.FunctionArguments,
                                _credential
                        ));
                        streamRun = submitToolOutputsUpdate.Value;
                    }
                    else if (streamingUpdate.UpdateKind == StreamingUpdateReason.RunCompleted)
                    {
                        Console.WriteLine();
                        Console.WriteLine("--- Run completed! ---");
                    }
                    else if (streamingUpdate.UpdateKind == StreamingUpdateReason.Error && streamingUpdate is RunUpdate errorStep)
                    {
                        Console.WriteLine($"Error: {errorStep.Value.LastError}");
                    }
                }

                if (toolOutputs.Count > 0)
                {
                    stream = _agentsClient.Runs.SubmitToolOutputsToStreamAsync(streamRun, toolOutputs);
                }
            } while (toolOutputs.Count > 0);

            var messages = _agentsClient.Messages.GetMessages(thread.Id, streamRun?.Id);

            var tmp = messages.First().ContentItems[0] as MessageTextContent;
            string result = tmp == null ? $"Latest message {messages.First()?.Id} returned in run {streamRun?.Id} was not a MessageTextContent" : $"{tmp.Text}\n\n | Thread ID: {thread.Id}";

            return new ChatMessage()
            { 
                Content = result,
                SessionId = thread.Id
            };
        }

        private async Task<ToolOutput> GetResolvedToolOutput(string functionName, string toolCallId, string functionArguments, DefaultAzureCredential cred)
        {
            using JsonDocument argumentsJson = JsonDocument.Parse(functionArguments);

            if (functionName == "calculateAgeInDogYears")
            {
                int yearsArgument = argumentsJson.RootElement.GetProperty("humanYears").GetInt32();
                return new ToolOutput(toolCallId, CalculateAgeInDogYears(yearsArgument).ToString());
            }
            else if (functionName == "requestMoreInformationFromSiteContext")
            {
                string questionArgument = argumentsJson.RootElement.GetProperty("question").GetString();
                return new ToolOutput(toolCallId, (await RequestMoreInformationFromSiteContext(questionArgument)).ToString());
            }

            return null;
        }

        private int CalculateAgeInDogYears(int humanYears)
        {
            return humanYears * 10;
        }

        private async Task<string> RequestMoreInformationFromSiteContext(string question)
        {
            if (string.IsNullOrEmpty(question))
            {
                return string.Empty;
            }

            DBService dbService = new DBService(_config.WEBAPP_EASYAGENT_DB_ENDPOINT, _credential, "testFailover-vectors", "base");

            var aClient = new AIProjectClient(new Uri(_config.WEBAPP_EASYAGENT_FOUNDRY_ENDPOINT), _credential);

            var eClient = aClient.GetAzureOpenAIEmbeddingClient(deploymentName: _config.WEBAPP_EASYAGENT_EMBEDDING_MODEL);

            var qEmbedding = eClient.GenerateEmbedding(question);

            string context = string.Join(",", await dbService.GetNNearestTextsAndEmbeddingsAsync(qEmbedding.Value.ToFloats().ToArray()));

            return context;
        }
    }
}
