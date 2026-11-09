# Agent (AGENT-01..04)

How the food-assistant Agent behaves for its clients and operators. The design and contracts are in
`.agents/ANGI_Agent_Implementation_Ver1.1.md`, the `docs/Agent_*` files and the Agent sheets of the API Design and
Data Dictionary; this page summarizes what a client or an operator needs.

## Chat-only frontend

The Agent UI needs a chat input and a Send button. Open a conversation with `POST /api/v1/agent/conversations` and `{}` (or an owned `roadmapId` to edit a saved trip). Then send the authenticated Traveler's messages with only `content`:

```http
POST /api/v1/agent/conversations/7/messages
Content-Type: application/json
Authorization: Bearer <access-token>
```

```json
{
  "content": "Tôi muốn lên lịch ăn uống Hà Nội 1 ngày, ngân sách 500000 đồng"
}
```

Omit `clientMessageId` and `expectedConversationVersion` (or send null) to let the backend allocate an unused positive ID and use the current conversation revision under its lock. Every request without an ID is a new message, including repeated text. The response returns `data.clientMessageId` and `data.conversationVersion`. GET conversation returns `data.version`.

For retry-safe clients, supply a fresh positive `clientMessageId` per new message and reuse the same ID/content when retrying it. An explicit ID reused with different business content still returns `MESSAGE_ID_REUSED`; an explicit stale version still returns `CONVERSATION_VERSION_CONFLICT`. If a request without an ID times out, reload the conversation before sending again because automatic retries are new turns and can consume provider quota. The frontend does not classify intent or fill `command`, `selectedItemKey` or `confirmedState`; those optional fields remain compatible for existing clients.

The backend interprets ordinary planning, item edits and requests for another option. `bỏ bản nháp` discards the preview while keeping the official roadmap. `làm lại từ đầu` starts a fresh planning context and detaches the previous saved roadmap without deleting it. Ambiguous item references are clarified in chat.

To save a reviewed preview, send `lưu lịch trình này` or `chốt lịch này` as a new message. The backend chooses the current owned proposal, rechecks catalog prices, expiry and the source roadmap timestamp, then saves the roadmap and chat acknowledgement atomically. Success includes `data.commit` (`roadmapId`, `roadmapUpdatedAt`, `committedProposalId`, `itemIds`). Replaying a message with the same explicit ID returns its stored commit result. A bare `đồng ý`, a quoted instruction or a negated save sentence never commits a roadmap. A bare save intent (`lưu`, `lưu lại đi`, `chốt đi`) neither saves nor replans: it answers with the current preview's size and total and the exact save wording, without an LLM call. Asking `hãy lập lịch giúp tôi để chốt lịch` returns the existing ready preview for review without saving it. The explicit `/confirm` endpoint remains available for existing clients.

When a requested change relaxes existing constraints, the backend first asks a confirmation question and stores `state.pendingStateChange`. Reply `xác nhận thay đổi` (or `đồng ý`) to apply those exact pending constraints and generate an updated preview in that turn, or `giữ nguyên` to reject them. Confirmation of constraints does not save a roadmap. A validated updated preview supersedes the previous preview; provider failures preserve the previous persisted state and preview. Server-managed `pendingStateChange`, `sourceRoadmapUpdatedAt` and `historyStartSequence` are response/context metadata; clients do not send them back.

## Roadmap change detection

`roadmaps.updated_at` replaces the former roadmap `version` counter. PostgreSQL generates a UTC timestamp with microsecond precision on every parent write and every insert/update/delete of a roadmap day or item. Moving a child updates both parents. Each update uses `GREATEST(clock_timestamp(), previous_updated_at + interval '1 microsecond')`, so rapid writes and backwards clock adjustments cannot reuse a token. These changes roll back with their transaction. EF reads the final parent timestamp after saving children before mapping responses.

An update proposal stores `baseRoadmapUpdatedAt`; conversation state tracks `sourceRoadmapUpdatedAt`; a commit returns `roadmapUpdatedAt`. Confirmation locks the owned parent row and compares its exact `updated_at` with the snapshot inside the write transaction. A mismatch returns HTTP 409 `ROADMAP_UPDATED_AT_CONFLICT`; request a fresh preview. Background roadmap generation performs the same comparison before replacing items. Conversation and proposal versions retain their separate purposes.

Treat these timestamp strings as opaque concurrency tokens. Keep the original ISO 8601 UTC value, for example `2026-10-29T02:00:00.123456Z`; do not pass it through JavaScript `Date` or formatting that drops microseconds. Agent snapshots are server-managed, so the frontend does not need to construct or echo their timestamps for confirmation.

Migration `20261029020050_ReplaceRoadmapVersionWithUpdatedAt` removes the roadmap counter columns and converts nested JSON in existing proposals, conversation state and message replay responses. Existing uncommitted previews are superseded, in-flight attempts are released, and clients must reload their conversation and obtain a fresh preview. Roadmaps, messages and committed results are retained. Historical committed timestamps that cannot be recovered use the recorded `committed_at` for display; they never authorize a new write. Apply this migration with old API/worker processes stopped. Downgrading requires a pre-migration backup because the removed counters cannot be reconstructed.

## Planning rules

Trip planning requires province, duration and budget; `startDate` is optional. Every proposal response contains the complete snapshot ordered by day, meal and sort order, including restored older proposals. Navigation focus is cleared after a successful edit. The provider receives actual catalog tags; unsupported taste preferences remain trip notes rather than invented tag guarantees.

## Provider calls

Agent provider calls use `Agent:LlmTimeoutSeconds` (60 seconds by default), capped by the remaining `Agent:TimeoutSeconds` turn budget (90 seconds). `Agent:LeaseSeconds` remains 120 seconds. Other AI consumers keep their independent `Ai:TimeoutSeconds` setting.

`Agent:MaxOutputTokens` defaults to 16000 per call (validated range 100..16000). This is sent as `max_completion_tokens`, which covers reasoning and the structured response. The checked-in backend model is `gpt-6-luna` with `Agent:ReasoningEffort=none`: this model requires reasoning to be disabled for Chat Completions function calling. Its Agent adapter enforces `none` even if an older deployment still sets `low`. For other models, allowed configured values remain null, `none`, `minimal`, `low`, `medium`, `high`; null omits the field, and the model must support the chosen value. On a host, use `Ai__Model=gpt-6-luna`, `Agent__MaxOutputTokens=16000` and `Agent__ReasoningEffort=none`; update older model/effort overrides when deploying. User-secrets are local development configuration and are not published to the host. Truncated responses remain non-retryable and cannot execute tools or save partial proposals.

Provider HTTP 500/502/503 permits at most two retries with backoff. A classified per-minute limit may retry using a short provider retry hint when the turn has enough remaining time; daily quota exhaustion is never retried. Timeout, network, authentication, rejected requests, refusal and truncated output do not trigger automatic retries. Schema or proposal-operation errors permit one repair within the same call/time budget. Tools are disabled after the allowed rounds so the model must finish or clarify. The default total provider-call budget is eight.

Provider rate/quota limits return HTTP 429 `AGENT_UNAVAILABLE`; other provider failures return HTTP 503 with a safe Vietnamese explanation. The application's own per-user quota returns HTTP 429 `AGENT_RATE_LIMITED`. `Retry-After` uses either HTTP headers or Gemini's structured retry metadata. Gemini object and array error envelopes are both supported. Logs retain only fixed diagnostic labels, status, timings and retry hints, never API keys, raw prompts or provider error messages. Canceling the frontend request propagates cancellation without repair, and failed turns preserve the previous preview.

The CORS policy exposes `Retry-After` to the configured frontend origins so browser clients can read the provider's retry guidance.

## Conversation preferences (2026-10-29)

Implementation contract and test setup: [Conversation preferences v1](Agent_Conversation_Preferences_Ver1.0.md).

This section supersedes earlier Agent lifecycle, supported-taste-as-note and preview-impression rules. The API Design and Data Dictionary contain the matching `Agent Preferences v1` contract sheet.

- A non-null roadmap_id uniquely identifies its conversation. Open returns 201 new or 200 reused/reopened.
  Reset keeps the attached roadmap. Reopen after 7 idle days retains ID but starts fresh context/profile.
- AgentState.tasteTargets holds only user-declared ontology targets on 0/.25/.5/.75/1. Backend-owned
  preferences contains the inherited profile snapshot, computed request vector, ontology version,
  profile update/read timestamps and initialization status. Missing dimensions inherit; never infer tastes from dishes.
- Initialize lazily outside locks; unavailable profile reads stay pending, distinct from a null profile.
  GET /users/{id}/preferences is internal/read-only. Conversation ranking passes vector_source and
  user_vector_override to reco. Algorithms/cold-start are unchanged; chat never increments m.
- Agent commit writes AGENT_IMPRESSION_RECORDED outbox entries in its transaction. Send all committed
  dishes grouped by original request_id after commit, with agent_proposal_id. Never count previews.
  Reco rejects conversation impressions without a commit reference. No synthetic feedback is created.
- Keep attached conversation rows when pruning 30-day payloads. Prune active histories too while
  retaining current proposal/recent replay. Sequence/proposal high-water marks survive pruning;
  expired client-message IDs are rejected with MESSAGE_EXPIRED. Maintenance never renews activity.
- Deploy reco migration/service before backend migration/service. Detect duplicate roadmap conversations
  before the unique index and stop with their IDs; never automatically delete or detach them.

## Scoped planning

Backend-only contract: [Agent scoped planning v2](Agent_Scoped_Planning_Ver2.0.md), also
included in the `Agent Scoped v2` worksheets of the API Design and Data Dictionary.
The existing message/confirm endpoints remain compatible. Agent state now supports day/meal
taste overrides and catalog meal requests; the backend computes per-request vectors while reco
keeps its current scoring/cold-start. Named dishes/restaurants must resolve against the public
catalog. Unknown/ambiguous/unavailable results ask for clarification instead of substitution.
User-visible replies are rendered from validated snapshots and fixed clarification templates.
No frontend or reco change, new database table, or migration is required for this extension.

## AI spending limit

Backend Agent and roadmap generation (RM-10) call OpenAI directly with `Ai:ApiKey`. Spending is capped on
OpenAI, not in the backend:

1. Create a dedicated OpenAI project for ANGI and issue `Ai:ApiKey` from it. Never use a key of another project.
2. On the project's **Limits** page, set a monthly budget and turn on **Enforce a hard limit**. Add a usage alert
   below the limit. OpenAI enforces the limit shortly after spend is recorded, so spending may slightly exceed it.
3. To stop spending at once, revoke the key or lower the limit on OpenAI.

Once the limit is reached OpenAI answers 429 `project_spend_limit_exceeded` (`organization_spend_limit_exceeded`
for an organization limit). The Agent never retries it and returns 503 `AGENT_UNAVAILABLE` asking the user to contact
an administrator; RM-10 falls back to scheduling the days itself. The OpenAI limit is shared by every user, so the
Agent's own per-user limits (`Agent:MessagesPerMinute`, `Agent:MessagesPerDay`, `Agent:MaxLlmCalls`) stay in place.
Reco keeps its own AI configuration and calls its provider independently.
