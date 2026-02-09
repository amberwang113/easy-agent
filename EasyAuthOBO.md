# EasyAuth & OBO: What the Demo Site Needs to Know

> **Audience:** An agent (or developer) working on the **host demo site** (e.g., `triptastic.azurewebsites.net`). This doc explains how Easy Agent authenticates to your API, what tokens you'll receive, and how to differentiate between callers.

---

## Overview

Easy Agent is a site extension that mounts at `/ai/chat` on your App Service. When a user asks a question, the Foundry agent may **call back into your API** (via OpenAPI tool definitions) to perform actions like creating alerts, looking up data, etc.

Your API will receive these callbacks with different authentication identities depending on how the system is configured. There are **three possible scenarios**, and your site needs to handle all of them.

---

## The Three Auth Scenarios

### Scenario 1: No EasyAuth (anonymous)

- **When:** `WEBSITE_AUTH_ENABLED` is not `True` on the App Service.
- **What happens:** Foundry calls your API with **no auth headers at all**.
- **What your API sees:** An unauthenticated request. No user identity.
- **Your API should:** Treat it as an anonymous/public call.

### Scenario 2: EasyAuth enabled, no OBO (managed identity)

- **When:** EasyAuth is on, but the three `WEBSITE_EASYAGENT_OBO_*` settings are not configured.
- **What happens:** Easy Agent authenticates to Foundry as the **app's managed identity**. When the Foundry agent calls back to your API, it uses `OpenApiManagedAuthDetails` — the token audience is `https://{WEBSITE_SITE_NAME}.azurewebsites.net`, and the identity is the **managed identity service principal**, not any user.
- **What your API sees:** A valid bearer token, but the `oid` / `sub` claim is the managed identity's object ID. There is **no user context**. Every request looks like the same service account.
- **Your API should:** Accept the token, but understand it represents the application, not any individual user. User-specific actions (e.g., "show *my* alerts") are not possible.

### Scenario 3: EasyAuth + OBO (user impersonation)

- **When:** EasyAuth is on **and** all three OBO settings are configured:
  - `WEBSITE_EASYAGENT_OBO_TENANT_ID`
  - `WEBSITE_EASYAGENT_OBO_CLIENT_ID`
  - `WEBSITE_EASYAGENT_OBO_CLIENT_SECRET`
- **What happens:**
  1. A user signs in via EasyAuth on the browser.
  2. EasyAuth injects the user's AAD access token into the header `X-MS-TOKEN-AAD-ACCESS-TOKEN`.
  3. Easy Agent's `ChatController` reads that token.
  4. `AgentService` performs an **On-Behalf-Of token exchange** with Entra ID — trading the user's token + the app registration's client secret for a new token scoped to Foundry.
  5. The `PersistentAgentsClient` is created with that OBO credential, so Foundry API calls carry the **user's identity**.
  6. When Foundry calls back to your API, the token now represents **the actual signed-in user**.
- **What your API sees:** A bearer token where `oid`/`sub`/`preferred_username`/`name` claims belong to the **real user**. You can identify who is making the request.
- **Your API should:** Use the token claims to implement per-user authorization (e.g., "show alerts created by *this* user").

---

## How It's Implemented in Easy Agent (already done)

The OBO flow is fully implemented across three files:

### `ChatbotConfiguration.cs`

Three properties bound from environment variables:

```csharp
public string WEBSITE_EASYAGENT_OBO_TENANT_ID { get; set; } = string.Empty;
public string WEBSITE_EASYAGENT_OBO_CLIENT_ID { get; set; } = string.Empty;
public string WEBSITE_EASYAGENT_OBO_CLIENT_SECRET { get; set; } = string.Empty;
```

### `Controllers/ChatController.cs`

Extracts the user token from the EasyAuth header and passes it through:

```csharp
// Extract user token from EasyAuth header if present (for OBO flow)
string? userToken = HttpContext.Request.Headers["X-MS-TOKEN-AAD-ACCESS-TOKEN"].FirstOrDefault();
var agentsClient = await _agentService.GetAgentsClientAsync(userToken);
```

### `Services/AgentService.cs`

Creates a per-request `PersistentAgentsClient` with OBO when configured:

```csharp
public async Task<PersistentAgentsClient> GetAgentsClientAsync(string? userToken = null)
{
    await EnsureInitializedAsync();

    if (!string.IsNullOrEmpty(userToken) && IsOboConfigured())
    {
        var oboCredential = new OnBehalfOfCredential(
            _config.WEBSITE_EASYAGENT_OBO_TENANT_ID,
            _config.WEBSITE_EASYAGENT_OBO_CLIENT_ID,
            _config.WEBSITE_EASYAGENT_OBO_CLIENT_SECRET,
            userToken);
        return new PersistentAgentsClient(_config.WEBSITE_EASYAGENT_FOUNDRY_ENDPOINT, oboCredential);
    }

    // Fall back to managed identity / DefaultAzureCredential
    return new PersistentAgentsClient(_config.WEBSITE_EASYAGENT_FOUNDRY_ENDPOINT, _defaultCredential);
}
```

The **agent definition** (`PersistentAgent`) is still cached as a singleton — only the **client credential** changes per request.

---

## How to Tell Callers Apart in Your API

When your API receives a callback from the Foundry agent, inspect the bearer token claims:

| Claim | Managed Identity (Scenario 2) | OBO / User (Scenario 3) |
|---|---|---|
| `oid` | Managed identity object ID (a service principal GUID) | The signed-in user's object ID |
| `sub` | Service principal subject | User's subject identifier |
| `preferred_username` | **Missing** | User's email (e.g., `amber@contoso.com`) |
| `name` | **Missing** | User's display name |
| `idtyp` | `app` | `user` |
| `appid` / `azp` | The managed identity's client ID | The OBO app registration's client ID |

**The simplest check:** Look at the `idtyp` claim. If it's `app`, the caller is a service principal (managed identity). If it's `user`, it's an OBO-impersonated real user.

```csharp
// Example: In your API controller
var identity = HttpContext.User.Identity as ClaimsIdentity;
var idType = identity?.FindFirst("idtyp")?.Value;

if (idType == "user")
{
    // OBO flow — we know who the user is
    var userId = identity?.FindFirst("oid")?.Value;
    var userName = identity?.FindFirst("preferred_username")?.Value;
    // Filter data by user, enforce per-user permissions, etc.
}
else
{
    // Managed identity — no user context
    // Return all data, or reject if user-specific action is required
}
```

---

## The Full Request Flow

```
Browser (user signed in via EasyAuth)
  │
  │  POST /ai/chat  (session cookie → EasyAuth translates to token)
  ▼
┌──────────────────────────────────┐
│  Easy Agent (site extension)     │
│  ChatController.cs               │
│  ├─ Reads X-MS-TOKEN-AAD-ACCESS- │
│  │  TOKEN from EasyAuth header   │
│  ├─ Calls AgentService           │
│  │  .GetAgentsClientAsync(token) │
│  └─ AgentService decides:        │
│     ├─ OBO configured + token?   │
│     │  → OnBehalfOfCredential    │
│     │    (exchanges user token   │
│     │     via Entra ID)          │
│     └─ Otherwise?                │
│        → ManagedIdentityCredential│
└──────────┬───────────────────────┘
           │  Foundry API call
           │  (Bearer: user-scoped or MI-scoped token)
           ▼
┌──────────────────────────────────┐
│  Azure AI Foundry                │
│  ├─ Runs the persistent agent    │
│  ├─ Agent decides to call your   │
│  │  API via OpenAPI tool         │
│  └─ Makes HTTP call to:         │
│     https://amberalerting        │
│     .azurewebsites.net/alerts    │
│     with bearer token            │
└──────────┬───────────────────────┘
           │
           ▼
┌──────────────────────────────────┐
│  YOUR DEMO SITE API              │
│  ├─ Receives bearer token        │
│  ├─ Validates via EasyAuth       │
│  ├─ Inspects claims:             │
│  │  ├─ idtyp=user → OBO user    │
│  │  └─ idtyp=app  → managed ID  │
│  └─ Returns data accordingly    │
└──────────────────────────────────┘
```

---

## What You Need to Configure on the Demo Site

### 1. EasyAuth (already done if auth is enabled)

Your App Service should have **Authentication** enabled with Microsoft Entra ID. This is what:
- Forces browser users to sign in.
- Provides the `X-MS-TOKEN-AAD-ACCESS-TOKEN` header to Easy Agent.
- Validates incoming bearer tokens on your API endpoints.

### 2. Accept tokens from the OBO app registration

In your site's **Entra ID app registration** (the one EasyAuth is configured with):

- Go to **Expose an API** → make sure an Application ID URI is set (e.g., `api://{client-id}` or `https://amberalerting.azurewebsites.net`).
- Add a scope like `user_impersonation` if you want explicit scope control.
- Under **Authorized client applications**, add the Easy Agent OBO app registration's client ID (`WEBSITE_EASYAGENT_OBO_CLIENT_ID`). This allows Easy Agent to exchange tokens on behalf of users without a consent prompt.

### 3. Accept tokens from managed identity (for non-OBO fallback)

Your EasyAuth configuration should also accept tokens from the managed identity. This typically works automatically — EasyAuth validates any token whose audience matches your site's Application ID URI.

---

## How to Test

### Test 1: Verify managed identity flow (Scenario 2)

1. Remove or leave blank the three `WEBSITE_EASYAGENT_OBO_*` settings.
2. Sign in to the site and open `/ai/chat`.
3. Ask the agent to "show all alerts" (triggers an OpenAPI tool callback).
4. In your API logs, check the incoming token — `idtyp` should be `app`, and there should be no `preferred_username` claim.

### Test 2: Verify OBO flow (Scenario 3)

1. Set all three `WEBSITE_EASYAGENT_OBO_*` settings.
2. Sign in as a specific user (e.g., `amber@contoso.com`).
3. Ask the agent to "show all alerts".
4. In your API logs, check the incoming token — `idtyp` should be `user`, and `preferred_username` should be `amber@contoso.com`.
5. Sign in as a **different user** and repeat. The `oid` and `preferred_username` should change.

### Test 3: Verify per-user data isolation

1. With OBO enabled, have **User A** create an alert via chat: "Create an alert with message 'test from A' and countdown 60".
2. Have **User B** ask "Show all my alerts".
3. If your API filters by `oid`, User B should **not** see User A's alert.

### Test 4: Verify fallback when token is missing

1. OBO settings are configured, but the `X-MS-TOKEN-AAD-ACCESS-TOKEN` header is absent (e.g., EasyAuth is disabled or the request bypasses it).
2. Easy Agent falls back to managed identity. The agent callback should arrive as `idtyp=app`.

---

## Easy Agent Config Settings for OBO

| Setting | Where to find it |
|---|---|
| `WEBSITE_EASYAGENT_OBO_TENANT_ID` | Azure Portal → Entra ID → Overview → **Tenant ID** |
| `WEBSITE_EASYAGENT_OBO_CLIENT_ID` | Azure Portal → App registrations → your OBO app → **Application (client) ID** |
| `WEBSITE_EASYAGENT_OBO_CLIENT_SECRET` | Azure Portal → App registrations → your OBO app → **Certificates & secrets** → secret **Value** |

All three must be set as App Service application settings (or in `appsettings.Development.json` for local dev). If any are missing, OBO is disabled and the system uses managed identity.

---

## Key Reference Docs

| Doc | URL |
|---|---|
| Foundry OpenAPI tool + managed identity | https://learn.microsoft.com/en-us/azure/app-service/configure-authentication-ai-foundry-openapi-tool |
| Entra OBO flow protocol | https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-on-behalf-of-flow |
| Agent OBO flow (Entra Agent ID) | https://learn.microsoft.com/en-us/entra/agent-id/identity-platform/agent-on-behalf-of-oauth-flow |
| Reference implementation (.NET) | https://github.com/kostapetan/foundry-data-obo |

---

## Summary

| Question | Answer |
|---|---|
| Does my site need to accept OBO tokens? | **Yes**, if you want per-user identity on Foundry agent callbacks. |
| Does my site need to accept managed identity tokens? | **Yes**, as the fallback when OBO isn't configured or the user token is missing. |
| How do I differentiate? | Check the `idtyp` claim: `user` = OBO, `app` = managed identity. |
| Do I need to change my EasyAuth config? | Add the OBO app registration as an authorized client under "Expose an API". |
| Does the chat UI need changes? | **No.** EasyAuth handles token injection via session cookies automatically. |
