using Azure.AI.Agents.Persistent;
using EasyAgent.Plugins;
using EasyAgent.Models;
using EasyAgent.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text;

namespace EasyAgent.Controllers
{
    [ApiController]
    [Route("/")]
    public class ChatController : Controller
    {
        private readonly IAgentService _agentService;
        private readonly ToolCallExecutor _toolCallExecutor;
        private readonly ILogger<ChatController> _logger;

        public ChatController(
            IAgentService agentService,
            ToolCallExecutor toolCallExecutor,
            ILogger<ChatController> logger)
        {
            _agentService = agentService;
            _toolCallExecutor = toolCallExecutor;
            _logger = logger;
        }

        [HttpPost]
        public async Task<IActionResult> Query([FromBody] ChatMessage chatMessage)
        {
            try
            {
                return Ok(await RunAgentAsync(chatMessage.Content, chatMessage.SessionId, HttpContext.Request.Headers));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception during chat request");
                return Ok(new ChatMessage { Content = $"Exception during request: {ex}", SessionId = chatMessage.SessionId });
            }
        }

        [HttpGet("history")]
        public async Task<IActionResult> GetHistory([FromQuery] string threadId)
        {
            if (string.IsNullOrEmpty(threadId))
                return Ok(Array.Empty<object>());

            try
            {
                var agentsClient = await _agentService.GetAgentsClientAsync();

                // Verify the thread exists
                try
                {
                    await agentsClient.Threads.GetThreadAsync(threadId);
                }
                catch
                {
                    // Thread no longer exists — tell the client to reset
                    return Ok(new { expired = true });
                }

                var messages = agentsClient.Messages.GetMessagesAsync(threadId);
                var history = new List<object>();
                await foreach (var msg in messages)
                {
                    foreach (var content in msg.ContentItems)
                    {
                        if (content is MessageTextContent textContent)
                        {
                            history.Add(new
                            {
                                role = msg.Role == MessageRole.User ? "user" : "assistant",
                                content = textContent.Text
                            });
                        }
                    }
                }

                // Messages come newest-first; reverse for chronological display
                history.Reverse();
                return Ok(history);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception loading thread history for {ThreadId}", threadId);
                return Ok(new { expired = true });
            }
        }

        private async Task<ChatMessage> RunAgentAsync(string userMessage, string? threadId, IHeaderDictionary incomingHeaders)
        {
            var agentsClient = await _agentService.GetAgentsClientAsync();
            var agent = await _agentService.GetAgentAsync();
            var toolMap = _agentService.ToolMap;

            // Enrich the user message with RAG site context
            var siteContextPlugin = HttpContext.RequestServices.GetRequiredService<SiteContextPlugin>();
            string siteContext = await siteContextPlugin.RequestMoreInformation(userMessage);

            string enrichedMessage = string.IsNullOrEmpty(siteContext)
                ? userMessage
                : $"{userMessage}\n\n[Site Context: {siteContext}]";

            // Create or reuse thread
            PersistentAgentThread thread;
            if (string.IsNullOrEmpty(threadId))
            {
                thread = (await agentsClient.Threads.CreateThreadAsync()).Value;
            }
            else
            {
                try
                {
                    thread = (await agentsClient.Threads.GetThreadAsync(threadId)).Value;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to retrieve thread {ThreadId} — creating a new thread", threadId);
                    thread = (await agentsClient.Threads.CreateThreadAsync()).Value;
                }
            }

            // Post the user message
            await agentsClient.Messages.CreateMessageAsync(
                thread.Id,
                MessageRole.User,
                enrichedMessage);

            // Create a run and enter the polling loop
            ThreadRun run = (await agentsClient.Runs.CreateRunAsync(thread.Id, agent.Id)).Value;

            while (run.Status == RunStatus.Queued
                || run.Status == RunStatus.InProgress
                || run.Status == RunStatus.RequiresAction)
            {
                if (run.Status == RunStatus.RequiresAction)
                {
                    var toolOutputs = new List<ToolOutput>();
                    foreach (var toolCall in run.RequiredActions)
                    {
                        if (toolCall is RequiredFunctionToolCall functionCall)
                        {
                            _logger.LogInformation("Agent requested tool call: {Name}({Args})",
                                functionCall.Name, functionCall.Arguments);

                            string result = await _toolCallExecutor.ExecuteAsync(
                                functionCall.Name,
                                functionCall.Arguments,
                                toolMap,
                                incomingHeaders);

                            toolOutputs.Add(new ToolOutput(functionCall.Id, result));
                        }
                    }

                    // Submit tool outputs and continue the run
                    run = (await agentsClient.Runs.SubmitToolOutputsToRunAsync(run, toolOutputs)).Value;
                }
                else
                {
                    await Task.Delay(500);
                    run = (await agentsClient.Runs.GetRunAsync(thread.Id, run.Id)).Value;
                }
            }

            if (run.Status == RunStatus.Failed)
            {
                _logger.LogError("Agent run failed: {Error}", run.LastError?.Message);
                return new ChatMessage
                {
                    Content = $"Agent run failed: {run.LastError?.Message}",
                    SessionId = thread.Id
                };
            }

            // Read the assistant's response messages
            var messages = agentsClient.Messages.GetMessagesAsync(thread.Id);
            var result2 = new StringBuilder();
            await foreach (var msg in messages)
            {
                if (msg.Role == MessageRole.Agent)
                {
                    foreach (var content in msg.ContentItems)
                    {
                        if (content is MessageTextContent textContent)
                            result2.AppendLine(textContent.Text);
                    }

                    // Only take the first (latest) assistant message
                    break;
                }
            }

            return new ChatMessage
            {
                Content = result2.ToString().TrimEnd(),
                SessionId = thread.Id
            };
        }
    }
}
