# Creating Custom Skills

[Русская версия](docs/SKILLS.ru.md)

This guide walks you through creating skills from scratch — from a simple GET endpoint to complex POST requests with templates.

---

## What is a Skill?

A skill is a configured API call that the AI agent can invoke. It defines:
- Which URL to call
- What HTTP method to use
- What parameters the agent should provide
- How to format the request body and headers
- What to extract from the response

Skills are organized into **groups** (e.g. "Jira", "GitLab", "Internal API").

---

## Quick Example

Before diving into details, here's what a complete skill looks like:

**Goal:** let the agent fetch a Jira issue by key.

| Field | Value |
|-------|-------|
| Name | `jira_issue` |
| Description | Get issue details by key |
| Group | Jira |
| URL | `https://jira.company.com/rest/api/2/issue/{key}` |
| Method | GET |
| Parameters | `key` (String, required) |
| Response filter | `key, fields.summary, fields.status.name, fields.assignee.displayName` |

The agent calls it like this:
```
cgw_invoke(skill=jira_issue, params={key: "PROJ-123"})
```

The extension makes a GET request to `https://jira.company.com/rest/api/2/issue/PROJ-123` using the browser's Jira session, filters the response, and returns only the fields the agent needs.

---

## Creating a Skill via UI

1. Open extension settings (extension icon → ⚙ Settings)
2. Click **+ Skill** in the top toolbar
3. Fill in the fields (described below)
4. Click **Save skill**
5. Use the **▶ Test** button to verify

---

## Skill Fields Reference

### Basic

| Field | Required | Description |
|-------|----------|-------------|
| **Name** | Yes | Function name the agent uses. Use `snake_case`: `get_user`, `search_issues` |
| **Description** | No | What the skill does. Shown to the agent in `cgw_list` output |
| **Group** | Yes | Which group this skill belongs to |

### Request

| Field | Required | Description |
|-------|----------|-------------|
| **URL** | Yes | API endpoint. Supports path parameters: `https://api.example.com/users/{userId}` |
| **HTTP method** | Yes | GET, POST, PUT, PATCH, DELETE |
| **Origin URL** | No | Where to get auth from, if different from URL domain (see [fetchOrigin](#fetchorigin)) |
| **Body template** | No | JSON template for POST/PUT/PATCH (see [Body templates](#body-templates)) |
| **Headers** | No | Custom HTTP headers. Values support `{{param}}` substitution |

### Response

| Field | Required | Description |
|-------|----------|-------------|
| **Response filter** | No | Comma-separated dot-notation paths to extract from the response (see [Response filtering](#response-filtering)) |

### Parameters

Each parameter has:

| Field | Description |
|-------|-------------|
| **Name** | Parameter name. Used in URL `{name}`, body `{{name}}`, headers `{{name}}` |
| **Type** | `String`, `Integer`, `Float`, `Boolean`, `Date` |
| **Required** | Whether the agent must provide this parameter |
| **Description** | Help text shown to the agent in `cgw_schema` |

---

## Parameter Substitution

Parameters are substituted in three places, each with different syntax:

### 1. URL path: `{param}`

```
URL:    https://api.example.com/users/{userId}/posts/{postId}
Params: { userId: "123", postId: "abc" }
Result: https://api.example.com/users/123/posts/abc
```

- Values are URL-encoded automatically (`space` → `%20`)
- Matched parameters are consumed — they won't appear in query string

### 2. Query string (automatic for GET/DELETE)

Parameters **not** used in the URL path are added as query string:

```
URL:    https://api.example.com/search
Method: GET
Params: { query: "test user", limit: "10" }
Result: https://api.example.com/search?query=test+user&limit=10
```

### 3. Body template: `{{param}}`

```
Template: {"email": "{{email}}", "count": {{count}}, "active": {{active}}}
Params:   { email: "john@test.com", count: "5", active: "true" }
Result:   {"email": "john@test.com", "count": 5, "active": true}
```

Type-specific substitution:

| Type | Template | Input | Result |
|------|----------|-------|--------|
| String | `"name": "{{name}}"` | `hello "world"` | `"name": "hello \"world\""` |
| Integer | `"count": {{count}}` | `42` | `"count": 42` |
| Float | `"price": {{price}}` | `9.99` | `"price": 9.99` |
| Boolean | `"active": {{active}}` | `true` / `1` / `yes` | `"active": true` |

> **Important:** String values must be inside quotes in the template. Numbers and booleans — without quotes.

### 4. Headers: `{{param}}`

```
Header:  X-Custom: Bearer {{token}}
Params:  { token: "abc123" }
Result:  X-Custom: Bearer abc123
```

---

## Body Templates

Used for POST, PUT, PATCH requests. Write a JSON template with `{{param}}` placeholders:

**Simple:**
```json
{
  "title": "{{title}}",
  "body": "{{content}}"
}
```

**With mixed types:**
```json
{
  "query": "{{searchText}}",
  "maxResults": {{limit}},
  "includeArchived": {{archived}}
}
```

**No template (flat JSON):**
If the body template is empty, all remaining parameters (not used in URL) are sent as a flat JSON object:

```
URL:    https://api.example.com/users
Method: POST
Params: { name: "John", email: "john@test.com" }
Body:   {"name": "John", "email": "john@test.com"}
```

---

## Response Filtering

The `responseFilter` field lets you extract only specific fields from the API response, reducing noise for the agent.

**Syntax:** comma-separated dot-notation paths.

**Example:**

API returns:
```json
{
  "id": 10001,
  "key": "PROJ-123",
  "fields": {
    "summary": "Fix login bug",
    "status": { "name": "Open", "id": "1" },
    "priority": { "name": "High", "id": "2" },
    "assignee": { "displayName": "John", "email": "john@corp.com" }
  }
}
```

Filter: `key, fields.summary, fields.status.name, fields.assignee.displayName`

Result returned to agent:
```json
{
  "key": "PROJ-123",
  "fields": {
    "summary": "Fix login bug",
    "status": { "name": "Open" },
    "assignee": { "displayName": "John" }
  }
}
```

**Arrays** are handled automatically — the filter applies to each element:

Filter: `issues.key, issues.fields.summary` on a search response extracts those fields from every issue in the array.

**Empty filter** returns the full response.

---

## fetchOrigin

Some corporate APIs have a different domain for the API endpoint and the web UI where you log in:

- Web UI (where you're logged in): `https://app.corp.com`
- API endpoint: `https://api.corp.com/v1/data`

The extension captures auth tokens from the origin where you're logged in. If the API is on a different domain, set **Origin URL** to the web UI domain:

```
URL:          https://api.corp.com/v1/issues
Origin URL:   https://app.corp.com
```

The extension will:
1. Open a background tab at `https://app.corp.com`
2. Capture the Authorization header from that origin
3. Use it when making requests to `https://api.corp.com`

**When to use:** only when API domain differs from login domain. Leave empty for most cases.

---

## Common Patterns

### GET with path parameter

```
Name:           get_user
URL:            https://api.example.com/users/{userId}
Method:         GET
Parameters:     userId (String, required)
Response filter: id, name, email, role
```

### GET with query parameters (search)

```
Name:           search_users
URL:            https://api.example.com/users
Method:         GET
Parameters:     query (String, required)
                limit (Integer, optional)
                offset (Integer, optional)
```

Agent calls `search_users(query="john", limit="10")` → GET `https://api.example.com/users?query=john&limit=10`

### POST with JSON body

```
Name:           create_comment
URL:            https://api.example.com/issues/{issueKey}/comments
Method:         POST
Parameters:     issueKey (String, required)
                body (String, required)
Body template:  {"body": "{{body}}"}
```

### PUT with mixed types

```
Name:           update_settings
URL:            https://api.example.com/users/{userId}/settings
Method:         PUT
Parameters:     userId (String, required)
                theme (String, required)
                notifications (Boolean, required)
                fontSize (Integer, optional)
Body template:  {"theme": "{{theme}}", "notifications": {{notifications}}, "fontSize": {{fontSize}}}
```

### DELETE

```
Name:           delete_comment
URL:            https://api.example.com/comments/{commentId}
Method:         DELETE
Parameters:     commentId (String, required)
```

---

## Creating a Preset File

To share skills across teams, create a preset JSON file:

```json
{
  "Groups": [
    {
      "Id": "myapi",
      "Name": "My API",
      "Description": "Internal company API",
      "Color": "#4f46e5"
    }
  ],
  "Skills": [
    {
      "Name": "get_user",
      "Description": "Get user profile by ID",
      "GroupId": "myapi",
      "Url": "https://MY_API_URL/users/{userId}",
      "HttpMethod": "GET",
      "Parameters": [
        {
          "Name": "userId",
          "Description": "User ID",
          "Type": "String",
          "Required": true
        }
      ],
      "Headers": {},
      "BodyTemplate": "",
      "ResponseFilter": "id, name, email"
    },
    {
      "Name": "search_users",
      "Description": "Search users by name or email",
      "GroupId": "myapi",
      "Url": "https://MY_API_URL/users/search",
      "HttpMethod": "GET",
      "Parameters": [
        {
          "Name": "q",
          "Description": "Search query",
          "Type": "String",
          "Required": true
        },
        {
          "Name": "limit",
          "Description": "Max results (default 20)",
          "Type": "Integer",
          "Required": false
        }
      ],
      "Headers": {},
      "BodyTemplate": "",
      "ResponseFilter": "results.id, results.name, total"
    }
  ]
}
```

After import, replace `MY_API_URL` with the actual domain.

---

## Testing

1. Open extension settings
2. Click the skill card
3. Click the blue **▶ Test** button
4. Fill in parameter values
5. Click **▶ Run**
6. Check the result panel

**Debugging tips:**

- If you get a 401 error → log in to the system in your browser
- If you get a network error → check that the URL is correct and the system is accessible
- For detailed logs → `chrome://extensions` → CorpGateway → **Inspect views: service worker**
- All invocations are logged in the audit log (`cgw_audit` tool)

---

## Operation Confirmation

Skills can be configured to require confirmation before execution. This protects against unintended actions by the AI agent.

### How it works

When a skill has `confirm: true`, the agent should use `cgw_invoke_confirmed` instead of `cgw_invoke`. The `cgw_schema` response includes the `confirm` flag and the recommended `invoke` tool name.

**Primary flow (with OpenCode or agents that support permissions):**

1. Agent calls `cgw_schema(skill)` → sees `confirm: true, invoke: "cgw_invoke_confirmed"`
2. Agent calls `cgw_invoke_confirmed(skill, params)`
3. OpenCode shows native confirmation prompt in the terminal
4. User approves → skill executes

To enable native prompts in OpenCode, add to `opencode.json`:
```json
{ "permissions": { "mcp:corp:cgw_invoke_confirmed": "ask" } }
```

**OTP fallback (if agent mistakenly uses cgw_invoke for a confirmed skill):**

1. Agent calls `cgw_invoke(skill, params)` for a skill with `confirm: true`
2. Extension generates a 4-digit code, shows it via OS notification
3. Extension returns "confirmation required — ask user for the code"
4. Agent asks user for the code, calls again with `confirmCode`
5. Extension validates the code and executes

### Configuring per skill

In the skill editor, use the **"Require confirmation"** checkbox:
- **Checked** — the skill requires confirmation before execution
- **Unchecked** (default) — the skill executes immediately

### OTP code properties

- 4 random digits (0000–9999)
- Valid for **60 seconds**
- One-time use — consumed after successful validation
- If the code expires or is invalid, a **new code** is automatically generated and sent
