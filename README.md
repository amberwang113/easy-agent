# Easy Agent

> **TL;DR for agents/developers:** This is a .NET 9 Azure App Service **site extension** that bolts an AI chat assistant onto any existing website. It scrapes the host site into Cosmos DB embeddings, then uses RAG + Azure AI Foundry agents to answer user questions from that site content. It mounts at `/ai/chat` via an IIS `applicationHost.xdt` transform. All auth (EasyAuth, managed identity, OBO) is handled automatically.

## What This Project Is

Easy Agent is a **proof-of-concept** that adds an AI-powered chat widget to any Azure Web App without modifying the host application. Install it as a site extension and it:

1. **Scrapes** the host website (via a continuous WebJob) and stores page content + vector embeddings in Cosmos DB.
2. **Serves** a chat UI at `/ai/chat` alongside the host site.
3. **Answers** user questions using RAG — retrieving relevant site content from Cosmos DB and forwarding enriched queries to an Azure AI Foundry persistent agent.
4. **Calls back** into the host website's API via OpenAPI tool definitions, with automatic EasyAuth-aware authentication.

## Project Structure

```
EasyAgent/
??? Controllers/ChatController.cs   ? POST endpoint (route: /)
??? Services/AgentService.cs         ? Azure AI Foundry agent lifecycle (singleton)
??? Services/DBService.cs            ? Cosmos DB vector search client
??? Plugins/SiteContextPlugin.cs     ? RAG: embeds query ? vector search ? site context
??? Models/ChatMessage.cs            ? Request/response DTO
??? Pages/Index.cshtml               ? Chat UI (Razor Page)
??? wwwroot/js/chat.js               ? Browser-side chat logic (fetch POST to /)
??? Program.cs                       ? App startup + WebJob deployment
??? ChatbotConfiguration.cs          ? Strongly-typed config (bound from env vars)
??? applicationHost.xdt              ? IIS transform: mounts extension at /ai/chat
??? web.config                       ? ASP.NET Core out-of-process hosting
??? WebJobs/EasyAgentScraper.zip     ? Bundled scraper WebJob (extracted at startup)
??? EasyAgent.nuspec                 ? NuGet packaging for site extension
??? EasyAuthOBO.md                   ? OBO auth guide for demo site developers
??? build.cmd                        ? Build + publish + pack
```

## How It Works

### Request Flow

1. User sends a message via the chat UI at `/ai/chat`.
2. **`ChatController`** receives the POST (`Content` + `SessionId`).
3. **`SiteContextPlugin`** generates an embedding for the user's question, runs a `VectorDistance` query against Cosmos DB via **`DBService`**, and returns the top-N matching page chunks with their source URLs.
4. The user message is enriched with `[Site Context: ...]` and sent to the Azure AI Foundry agent.
5. **`AgentService`** (singleton, lazily initialized) provides the persistent agent. On first use it creates or updates the agent with:
   - A system prompt constraining answers to site content only.
   - An OpenAPI tool definition built from the host site's API spec, enabling the agent to call back into the host website.
6. The agent's response is streamed back. A thread ID is returned for conversation continuity across messages.

### Key Design Decisions

- **Site extension architecture**: `applicationHost.xdt` inserts an IIS application at `/ai/chat` pointing to the extension's `content/` directory. The extension runs as a separate ASP.NET Core process (out-of-process, see `web.config`) so it doesn't interfere with the host app.
- **RAG over tool-only**: Site context is injected *into the user message* before calling the agent, rather than relying solely on the agent's OpenAPI tool. This gives the agent immediate context for every query.
- **Agent-managed tools**: The OpenAPI tool is registered directly on the Azure AI Foundry agent (not as a Semantic Kernel plugin). This preserves the agent-side auth configuration for callbacks.
- **WebJob bundling**: `Program.cs` extracts `WebJobs/EasyAgentScraper.zip` into the App Service's continuous WebJob directory at startup, so the scraper deploys automatically with the extension.

## Configuration

All settings are **App Service application settings** (environment variables), bound to `ChatbotConfiguration`:

| Setting | Description |
|---|---|
| `WEBSITE_EASYAGENT_FOUNDRY_ENDPOINT` | Azure AI Foundry project endpoint |
| `WEBSITE_EASYAGENT_FOUNDRY_CHAT_MODEL` | Chat model deployment name |
| `WEBSITE_EASYAGENT_FOUNDRY_EMBEDDING_MODEL` | Embedding model deployment name |
| `WEBSITE_EASYAGENT_FOUNDRY_AGENTID` | Existing agent ID to update (optional; creates new if empty) |
| `WEBSITE_EASYAGENT_FOUNDRY_OPENAPISPEC` | OpenAPI spec JSON of the host website's API |
| `WEBSITE_EASYAGENT_SITECONTEXT_DB_ENDPOINT` | Cosmos DB endpoint for embeddings |
| `WEBSITE_EASYAGENT_SITECONTEXT_DB_NAME` | Cosmos DB database name (defaults to `{WEBSITE_SITE_NAME}-EasyAgent`) |
| `WEBSITE_MANAGED_CLIENT_ID` | User-assigned managed identity client ID (falls back to `DefaultAzureCredential`) |
| `WEBSITE_SITE_NAME` | App Service site name (set automatically by Azure) |
| `WEBSITE_AUTH_ENABLED` | Set automatically by Azure when EasyAuth is on |
| `WEBSITE_EASYAGENT_OBO_TENANT_ID` | Microsoft Entra ID tenant ID for OBO flow (optional; see [OBO section](#on-behalf-of-obo-authentication)) |
| `WEBSITE_EASYAGENT_OBO_CLIENT_ID` | App registration (client) ID for OBO flow (optional) |
| `WEBSITE_EASYAGENT_OBO_CLIENT_SECRET` | Client secret for OBO token exchange (optional) |

## EasyAuth Compatibility

`AgentService` checks `WEBSITE_AUTH_ENABLED` at initialization and configures the agent's OpenAPI tool auth accordingly:

| EasyAuth State | Agent Tool Auth | Details |
|---|---|---|
| **Enabled** (`True`) | `OpenApiManagedAuthDetails` | Token audience: `https://{WEBSITE_SITE_NAME}.azurewebsites.net`. The agent uses managed identity to authenticate callbacks through EasyAuth. |
| **Disabled** | `OpenApiAnonymousAuthDetails` | No auth headers on tool callbacks. |

No manual configuration is needed — the extension adapts automatically.

## On-Behalf-Of (OBO) Authentication

By default, Easy Agent calls Azure AI Foundry using the App Service's **managed identity**. This means every user's request hits Foundry as the *application*, not as the logged-in user.

The **OBO flow** changes this: when a signed-in user sends a chat message, Easy Agent exchanges their EasyAuth access token for a new token that represents *that specific user*. Foundry then acts **on behalf of** the user, and any OpenAPI tool callbacks to your API carry the user's identity.

**How it works:**

1. EasyAuth authenticates the user and injects their AAD token into `X-MS-TOKEN-AAD-ACCESS-TOKEN`.
2. `ChatController` reads this header and passes the token to `AgentService.GetAgentsClientAsync(userToken)`.
3. `AgentService` creates an `OnBehalfOfCredential` using the token + the three OBO app settings.
4. A per-request `PersistentAgentsClient` is built with that credential.
5. The cached agent definition (`PersistentAgent`) is shared — only the client credential changes per request.

**Activation rules:**

| OBO Settings | User Token (EasyAuth) | Credential Used |
|---|---|---|
| ? Not configured | — | Managed identity / `DefaultAzureCredential` |
| ? All three set | ? Missing | Managed identity / `DefaultAzureCredential` |
| ? All three set | ? Present | `OnBehalfOfCredential` (user impersonation) |

OBO is entirely opt-in. When the three settings are absent, the app behaves exactly as before.

For full details on configuring your demo site to accept OBO tokens, differentiate between user and managed identity callers, and test the flow, see **[EasyAuthOBO.md](EasyAuthOBO.md)**.

## Build & Deploy

```bash
# Build, publish, and create the .nupkg site extension package
build.cmd
```

This runs `dotnet publish -c Release` then `nuget pack EasyAgent.nuspec`, producing a `.nupkg` in `bin/Release/`. Install the package as an Azure Site Extension via a NuGet feed or the App Service extensions gallery.

## Tech Stack

- **.NET 9** / ASP.NET Core Razor Pages
- **Microsoft Semantic Kernel** (`Microsoft.SemanticKernel.Agents.AzureAI`)
- **Azure AI Foundry** (persistent agents, OpenAPI tool use)
- **Azure Cosmos DB** (vector search via `VectorDistance`)
- **Azure Managed Identity** / `DefaultAzureCredential` / `OnBehalfOfCredential`

