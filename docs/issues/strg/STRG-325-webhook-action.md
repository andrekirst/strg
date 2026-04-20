---
id: STRG-325
title: Webhook action with HMAC signing
milestone: v0.2
priority: low
status: open
type: implementation
labels: [inbox, actions, webhook, security]
depends_on: [STRG-308]
blocks: []
assigned_agent_type: feature-dev
estimated_complexity: medium
---

# STRG-325: Webhook action with HMAC signing

## Summary

Implement the `WebhookAction` inbox action type. When triggered, it makes an authenticated HTTP POST to a user-configured URL with a JSON payload describing the file. The outgoing request is signed with HMAC-SHA256 (like GitHub webhooks). The receiver may optionally return a `{ "targetPath": "..." }` JSON body to override where a subsequent Move action sends the file. The webhook secret is stored encrypted in `ActionsJson` (DB only; never returned via GraphQL).

## Technical Specification

### New action type (`src/Strg.Core/Domain/Inbox/InboxAction.cs`)

```csharp
[JsonDerivedType(typeof(WebhookAction), "webhook")]
```

```csharp
public record WebhookAction(
    string Url,
    string HmacSecretEncrypted,   // AES-encrypted; never exposed via GraphQL
    int TimeoutSeconds = 30,
    int MaxRetries = 3
) : InboxAction(ConflictResolution.AutoRename, AutoCreateFolders: false);
```

### Webhook payload format (`src/Strg.Infrastructure/Inbox/WebhookPayload.cs`)

```csharp
public record WebhookPayload(
    Guid FileId,
    string FileName,
    string MimeType,
    long SizeBytes,
    string Path,
    Guid DriveId,
    DateTimeOffset UploadedAt
);
```

### Webhook response format

```json
{ "targetPath": "/photos/overridden-path" }
```

All other fields in the response body are ignored. If the response body is empty or not valid JSON, it is treated as "no override".

### Action execution (`src/Strg.Infrastructure/Inbox/InboxProcessingConsumer.cs`)

```csharp
if (action is WebhookAction webhook)
{
    var payload = new WebhookPayload(file.Id, file.Name, file.MimeType, file.Size, file.Path, file.DriveId, file.CreatedAt);
    var body = JsonSerializer.Serialize(payload);
    var secret = _encryptionService.Decrypt(webhook.HmacSecretEncrypted);

    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
    var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body)));

    using var request = new HttpRequestMessage(HttpMethod.Post, webhook.Url)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };
    request.Headers.Add("X-Strg-Signature", $"sha256={signature}");

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    cts.CancelAfter(TimeSpan.FromSeconds(webhook.TimeoutSeconds));

    var response = await _httpClient.SendAsync(request, cts.Token);
    response.EnsureSuccessStatusCode();

    var responseBody = await response.Content.ReadAsStringAsync(ct);
    if (!string.IsNullOrWhiteSpace(responseBody))
    {
        try
        {
            var override = JsonSerializer.Deserialize<WebhookOverrideResponse>(responseBody);
            if (override?.TargetPath != null)
                context.Items["WebhookTargetPathOverride"] = override.TargetPath;
        }
        catch (JsonException) { /* ignore malformed response */ }
    }
}
```

A subsequent `MoveAction` in the same rule checks `context.Items["WebhookTargetPathOverride"]` and uses that path instead of its configured `TargetPath` if set.

### HTTP client registration (in DI)

```csharp
services.AddHttpClient<InboxProcessingConsumer>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(60); // per-consumer upper bound
});
```

Never use `new HttpClient()` — always use `IHttpClientFactory` via named or typed client.

### GraphQL input type

The `HmacSecretEncrypted` field is **never exposed** in GraphQL output. Instead:
- On create: user provides the raw secret in `webhookSecret: String!`; the mutation encrypts it before storing.
- On read: only `url` and `timeoutSeconds` are exposed; secret is omitted.

```graphql
input WebhookActionInput {
  url: String!
  webhookSecret: String!       # plain text — mutation encrypts before storing
  timeoutSeconds: Int = 30
  maxRetries: Int = 3
}
```

### Security

- Secret never logged (Serilog destructuring policy on `WebhookAction` should mask `HmacSecretEncrypted`).
- URL is validated as HTTPS only (block `http://` URLs).
- Response body max size: 4 KB (to prevent memory pressure from malicious responses).

## Acceptance Criteria

- [ ] `WebhookAction` record in `Strg.Core` with `[JsonDerivedType]`
- [ ] HMAC-SHA256 signature in `X-Strg-Signature: sha256={hex}` header
- [ ] `webhookSecret` encrypted before storage; never returned via GraphQL
- [ ] HTTP timeout configurable per action (`TimeoutSeconds`)
- [ ] Retry up to `MaxRetries` times with exponential backoff on non-2xx response
- [ ] Response `targetPath` override wires into subsequent `MoveAction`
- [ ] Only HTTPS URLs accepted (validation at rule creation time)
- [ ] `IHttpClientFactory` used — no `new HttpClient()`

## Test Cases

- TC-001: Webhook fires → POST with `X-Strg-Signature` header; signature verifiable by receiver
- TC-002: Response `{ "targetPath": "/override" }` → subsequent Move uses `/override` as target
- TC-003: Response empty → no override; Move uses configured target path
- TC-004: Webhook returns 500 → retried 3 times then fails; action status = Failed
- TC-005: Webhook times out → action status = Failed; file stays in inbox
- TC-006: HTTP URL (not HTTPS) → validation error at rule creation; rule not saved

## Security Review Checklist

- [ ] `HmacSecretEncrypted` masked in all log output (Serilog destructuring)
- [ ] URL validated as HTTPS before storage
- [ ] Response body size capped at 4 KB
- [ ] `targetPath` from response goes through `StoragePath.Parse()` before use

## Implementation Tasks

- [ ] Add `WebhookAction` to `InboxAction.cs`
- [ ] Create `WebhookPayload` and `WebhookOverrideResponse` records
- [ ] Add `ExecuteActionAsync` case for `WebhookAction` in `InboxProcessingConsumer`
- [ ] Register typed HTTP client for `InboxProcessingConsumer`
- [ ] Add HTTPS URL validation to `InboxRuleMutations`
- [ ] Add `WebhookActionInput` GraphQL input (secret encrypt on create)
- [ ] Mask `HmacSecretEncrypted` in Serilog policies
- [ ] Write integration tests with a mock HTTP server (e.g., WireMock.Net)

## Definition of Done

- [ ] `dotnet build` passes with zero warnings
- [ ] All TC-001 through TC-006 tests pass
- [ ] Security checklist fully satisfied
