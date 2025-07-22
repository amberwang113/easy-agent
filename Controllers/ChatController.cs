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
        private const string azureAgent = "asst_nFU5kuwQWFZX9R3jKEmDLwm2";
        private DefaultAzureCredential credential = new DefaultAzureCredential();
        private readonly ChatbotConfiguration _config;

        public ChatController(IOptions<ChatbotConfiguration> options)
        {
            _config = options.Value;
        }


        [HttpPost]
        public async Task<IActionResult> Query([FromBody] ChatMessage chatMessage)
        {
            try
            {
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
            PersistentAgentsClient client = new(_config.WEBAPP_EASYAGENT_FOUNDRY_ENDPOINT, credential);

            // TODO: from configuration
            PersistentAgent agent = await client.Administration.GetAgentAsync(_config.WEBAPP_EASYAGENT_FOUNDRY_AGENTID);

            PersistentAgentThread thread = threadId == null ? await client.Threads.CreateThreadAsync() : await client.Threads.GetThreadAsync(threadId);

            PersistentThreadMessage message = await client.Messages.CreateMessageAsync(
                thread.Id,
                MessageRole.User,
                userMessage);

            List<ToolOutput> toolOutputs = [];
            ThreadRun streamRun = default;

            AsyncCollectionResult<StreamingUpdate> stream = client.Runs.CreateRunStreamingAsync(thread.Id, agent.Id);

            do
            {
                toolOutputs.Clear();
                await foreach (StreamingUpdate streamingUpdate in stream)
                {
                    if (streamingUpdate.UpdateKind == StreamingUpdateReason.RunCreated)
                    {
                        Console.WriteLine($" --- Run started for thread id {thread.Id} agent id {agent.Id} ---");
                    }
                    else if (streamingUpdate is RequiredActionUpdate submitToolOutputsUpdate)
                    {
                        RequiredActionUpdate newActionUpdate = submitToolOutputsUpdate;
                        toolOutputs.Add(await
                            GetResolvedToolOutput(
                                newActionUpdate.FunctionName,
                                newActionUpdate.ToolCallId,
                                newActionUpdate.FunctionArguments,
                                credential
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
                    stream = client.Runs.SubmitToolOutputsToStreamAsync(streamRun, toolOutputs);
                }
            } while (toolOutputs.Count > 0);

            var messages = client.Messages.GetMessages(thread.Id, streamRun?.Id);

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

            DBService dbService = new DBService(_config.WEBAPP_EASYAGENT_DB_ENDPOINT, credential, "testFailover-vectors", "base");

            var aClient = new AIProjectClient(new Uri(_config.WEBAPP_EASYAGENT_FOUNDRY_ENDPOINT), credential);

            var eClient = aClient.GetAzureOpenAIEmbeddingClient(deploymentName: _config.WEBAPP_EASYAGENT_EMBEDDING_MODEL);

            var qEmbedding = eClient.GenerateEmbedding(question);

            string context = string.Join(",", await dbService.GetNNearestTextsAndEmbeddingsAsync(qEmbedding.Value.ToFloats().ToArray()));

            return context;
        }
    }
}
