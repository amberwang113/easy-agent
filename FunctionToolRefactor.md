# Function Tool Refactor: OBO Without Foundry Callbacks

## Problem

Today, EasyAgent registers the site's OpenAPI spec as an `OpenApiToolDefinition` on the Foundry agent. When the agent decides to call the site's API, **Foundry makes the HTTP call directly** — and authenticating those callbacks with the user's identity requires a Foundry Connection (`OpenApiConnectionAuthDetails`) that doesn't have a clear portal setup path yet.

## Proposed Solution

Convert the OpenAPI spec into **`FunctionToolDefinition`s** instead. The agent still "decides" which API to call and with what parameters, but instead of Foundry making the HTTP call, it returns a `RequiresAction` status with the tool call details. **EasyAgent executes the HTTP call itself**, using the user's OBO token (or EasyAuth token) for auth, and feeds the result back to the agent.

### Before (current)

```
User → EasyAgent → Foundry Agent (OpenAPI tool registered)
                         ↓
                    Agent decides to call API
                         ↓
                    Foundry calls site API directly ← AUTH PROBLEM
                         ↓
                    Agent gets response, replies to user
```

### After (proposed)

```
User → EasyAgent → Foundry Agent (Function tools registered)
                         ↓
                    Agent decides to call API
                         ↓
                    Agent returns RequiresAction + tool call details
                         ↓
                    EasyAgent executes HTTP call to site API ← WE CONTROL AUTH
                         ↓
                    EasyAgent submits tool output back to agent
                         ↓
                    Agent gets response, replies to user
```

## What Changes

### 1. New Service: `OpenApiToolConverter` (new file)

**`Services/OpenApiToolConverter.cs`**

Parses the OpenAPI spec JSON and converts each operation into a `FunctionToolDefinition`.

For each path + method in the spec:
- **Tool name** = `operationId` (e.g., `getAllAlerts`, `createAlert`, `deleteAlert`)
- **Description** = `summary` field from the spec
- **Parameters** = JSON schema built from path params, query params, and request body

Example conversion from the current AmberAlerting spec:

| OpenAPI Operation | Function Tool Name | Parameters |
|---|---|---|
| `GET /alerts` | `getAllAlerts` | (none) |
| `POST /alerts` | `createAlert` | `{ message: string, countdown: integer }` |
| `GET /alerts/{id}` | `getAlertById` | `{ id: string }` |
| `DELETE /alerts/{id}` | `deleteAlert` | `{ id: string }` |

The converter also stores a mapping from tool name → HTTP method + URL template so EasyAgent knows how to execute the call later.

### 2. New Service: `ToolCallExecutor` (new file)

**`Services/ToolCallExecutor.cs`**

Executes the actual HTTP calls when the agent requests a tool invocation.

Responsibilities:
- Receives tool name + arguments from the agent's `RequiresAction` response
- Looks up the HTTP method + URL template from the mapping built by `OpenApiToolConverter`
- Substitutes path parameters into the URL
- Attaches the user's auth token (from EasyAuth header or OBO exchange)
- Makes the HTTP call to the site's API
- Returns the response body as the tool output string

Auth logic:
```
if EasyAuth token available:
    Add "Authorization: Bearer {X-MS-TOKEN-AAD-ACCESS-TOKEN}" header
else:
    Call with no auth (anonymous scenario)
```

Note: We use the **original EasyAuth access token** directly (the `X-MS-TOKEN-AAD-ACCESS-TOKEN` header), NOT an OBO-exchanged token. OBO is for calling Foundry. For calling the site's own API, the user's original token is already scoped correctly — EasyAuth issued it for this audience.

### 3. Modify: `AgentService.cs`

Changes:
- Remove all `OpenApiToolDefinition` / `OpenApiAuthDetails` / `OpenApiConnectionAuthDetails` / `OpenApiManagedAuthDetails` code
- Remove the `WEBSITE_EASYAGENT_FOUNDRY_CONNECTION_ID` usage
- Use `OpenApiToolConverter` to parse the spec into `FunctionToolDefinition[]`
- Register those on the agent instead of the OpenAPI tool
- Store the tool-name-to-HTTP mapping for use by `ToolCallExecutor`

The initialization goes from:

```csharp
// OLD: One OpenAPI tool with auth config
var openApiToolDef = new OpenApiToolDefinition(
    name: summary,
    description: summary,
    spec: spec,
    openApiAuthentication: openApiAuth,
    defaultParams: ["format"]
);
```

To:

```csharp
// NEW: Multiple function tools, no auth config needed on the agent
var (functionTools, toolMap) = OpenApiToolConverter.Convert(specJson);
// functionTools = FunctionToolDefinition[] 
// toolMap = Dictionary<string, ToolHttpInfo> (name → method + url template)
```

### 4. Modify: `ChatController.cs`

The current flow uses Semantic Kernel's `AzureAIAgent.InvokeAsync()` which handles the run loop internally. For function tools that EasyAgent needs to execute, we need to handle the `RequiresAction` status ourselves.

Two options:

**Option A: Drop to the raw `PersistentAgentsClient` API**
- Create thread, post message, create run
- Poll run status
- When `RequiresAction`: extract tool calls, execute via `ToolCallExecutor`, submit tool outputs, resume polling
- When `Completed`: read messages, return response

**Option B: Stay with Semantic Kernel `AzureAIAgent` but register a Kernel function**
- Register the `ToolCallExecutor` as a Kernel plugin/function
- Semantic Kernel's `InvokeAsync` loop automatically calls registered functions when the agent requests them
- Requires making `ToolCallExecutor` implement `KernelFunction` conventions

**Recommendation: Option A** — it's more explicit, avoids Semantic Kernel experimental API quirks, and gives full control over the auth header injection. The raw API is straightforward:

```csharp
// Pseudocode for the new run loop
var run = await client.Runs.CreateRunAsync(threadId, agent.Id);

while (run.Status == RunStatus.Queued || run.Status == RunStatus.InProgress || run.Status == RunStatus.RequiresAction)
{
    if (run.Status == RunStatus.RequiresAction)
    {
        var toolOutputs = new List<ToolOutput>();
        foreach (var toolCall in run.RequiredAction.SubmitToolOutputs.ToolCalls)
        {
            string result = await toolCallExecutor.ExecuteAsync(
                toolCall.Name, toolCall.Arguments, userToken);
            toolOutputs.Add(new ToolOutput(toolCall.Id, result));
        }
        run = await client.Runs.SubmitToolOutputsToRunAsync(threadId, run.Id, toolOutputs);
    }
    else
    {
        await Task.Delay(500);
        run = await client.Runs.GetRunAsync(threadId, run.Id);
    }
}
```

### 5. Modify: `ChatbotConfiguration.cs`

- `WEBSITE_EASYAGENT_FOUNDRY_CONNECTION_ID` can be **removed** (no longer needed)
- All other config stays the same

### 6. Cleanup

Remove:
- `WEBSITE_EASYAGENT_FOUNDRY_CONNECTION_ID` from `ChatbotConfiguration.cs`
- All `OpenApiAuthDetails` / `OpenApiManagedAuthDetails` / `OpenApiConnectionAuthDetails` / `OpenApiAnonymousAuthDetails` references
- The `WEBSITE_AUTH_ENABLED` check for auth type selection (no longer relevant to tool registration)
- Semantic Kernel `AzureAIAgent` / `AzureAIAgentThread` usage in `ChatController.cs` (replaced by raw client calls)

Keep:
- OBO configuration (`WEBSITE_EASYAGENT_OBO_*`) — still needed for authenticating to Foundry itself
- `GetAgentsClientAsync()` with OBO logic — still creates the per-request client
- `SiteContextPlugin` — still enriches user messages with site context
- The LLM call to summarize the spec — can still be used for the agent's instructions

## Files Changed

| File | Action |
|---|---|
| `Services/OpenApiToolConverter.cs` | **New** — parse OpenAPI spec → FunctionToolDefinition[] + HTTP mapping |
| `Services/ToolCallExecutor.cs` | **New** — execute HTTP calls with user auth |
| `Services/AgentService.cs` | **Modify** — use function tools instead of OpenAPI tool |
| `Controllers/ChatController.cs` | **Modify** — implement RequiresAction run loop with tool execution |
| `ChatbotConfiguration.cs` | **Modify** — remove `WEBSITE_EASYAGENT_FOUNDRY_CONNECTION_ID` |
| `EasyAuthOBO.md` | **Update** — document new flow, simplify scenarios |

## What This Unlocks

1. **Full user identity on API calls** — EasyAgent passes the user's token directly, no Foundry Connection needed
2. **Works with any EasyAuth-protected site** — no Foundry portal configuration beyond the basics
3. **Simpler auth model** — two concerns collapse into one: OBO for Foundry, direct token for site API
4. **No dependency on undocumented Foundry features** — `OpenApiConnectionAuthDetails` is no longer needed

## Risks / Considerations

- **Multiple tool calls per turn**: The agent might request several tool calls at once. The run loop needs to handle batches (the pseudocode above already does this).
- **Timeouts**: If the site API is slow, the run loop will be waiting. Consider adding `HttpClient` timeouts.
- **Error handling**: If an API call fails (4xx/5xx), return the error as the tool output so the agent can reason about it.
- **Spec complexity**: The `OpenApiToolConverter` needs to handle path parameters, query parameters, and request bodies. The current spec is simple (4 operations), but a real-world spec could have dozens. Keep the converter generic.
- **Semantic Kernel removal**: Moving from `AzureAIAgent.InvokeAsync()` to the raw `PersistentAgentsClient` run loop means we lose SK's built-in streaming and auto-invocation. But we gain full control — and the current code doesn't use streaming anyway.
- **Token audience**: The `X-MS-TOKEN-AAD-ACCESS-TOKEN` from EasyAuth should already be scoped to the site's own audience. Verify this works for the callback scenario. If not, an OBO exchange targeting the site's audience may be needed (but this is unusual).
