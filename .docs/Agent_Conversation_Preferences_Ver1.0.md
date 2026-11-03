# Conversation taste preferences and Agent lifecycle

This contract extends Agent v1.1. It supersedes previous rules that stored supported
tastes only as notes, detached roadmaps on reset, or counted Agent previews as impressions.

## Ownership and ranking

The Agent receives the prompt, trip state, bounded conversation history, current
roadmap/proposal, catalog tags and backend-owned preference snapshot. LLM output may
only propose `tasteTargets`; it cannot supply the baseline or effective vector.
The backend ignores such vector metadata from `confirmedState` and provider states.

`AgentState.preferences` is stored in conversation JSONB and immutable proposal
snapshots. It contains `ontologyVersion`, `baselineVector`, `effectiveVector`,
`profileUpdatedAt`, `profileReadAt` and `initialized`. No separate preference table
is introduced. The first successful profile read freezes the baseline for the context.
An unavailable read leaves initialization pending and retains collected targets;
a successful read with `vec=null` is a valid empty profile.

The fixed axis order is sweet, sour, salty, bitter, umami, spicy, cooling,
astringent, fatty, fresh, rich, aromatic. Targets use 0/.25/.5/.75/1; omitted axes
inherit their baseline. Targets replace those axes, without modifying other values
or clamping feedback-derived components. An all-zero effective vector is null.
Ambiguous intensity is clarified when needed. Dish names and nationality do not
imply taste targets. One vector applies to the whole conversation.

Backend catalog filters still run before ranking. Reco receives `user_id`, filtered
candidate IDs, `vector_source=conversation` and nullable `user_vector_override`.
The response must echo that source. Missing support, uninitialized preferences or
service failure uses rating fallback, without a recommendation request ID.
Scoring, implicit scores, survey/feedback interaction count `m` and cold-start remain
unchanged: at `m=0`, the conversation vector has no personalization weight.

## Conversation identity and retention

`POST /api/v1/agent/conversations` returns 201 when created and 200 when a supplied
roadmap already has a conversation. Concurrent opens serialize and reuse its ID;
a filtered unique index enforces the rule. A new trip uses a new conversation.
Reset retains the attached roadmap and rebuilds state from it. GET remains read-only.

After seven idle days, opening from the owned, available roadmap reuses the ID with
fresh roadmap state and lazily loads a new profile. Old drafts and transcript no
longer influence the new context. Existing valid legacy previews remain readable
and committable; preferences initialize when a new turn needs them.

After the configured 30-day retention, cleanup prunes messages and old proposals,
including active conversations, while retaining current proposals and recent replay
data. Recent retries retain their logical turn. Attached conversation rows survive;
old unbound conversations may be deleted. Cleanup does not renew business activity.
Persistent `last_message_sequence`, `last_proposal_version` and
`purged_client_message_id` prevent identity reuse. Purged retry IDs return HTTP 410
`MESSAGE_EXPIRED`. Callers may omit client IDs to use server-generated values.

## Commit-only impressions

UI confirmation and chat save share the same transaction. A real commit queues
`AGENT_IMPRESSION_RECORDED` outbox events for all committed items, grouped by their
original request ID, deduplicated, in batches of at most 200. Fallback items with
null request ID are skipped. Committed replay queues nothing new; rollback retains
neither roadmap changes nor new outbox events.

The existing worker sends RECO-06 after commit with internal `agent_proposal_id`.
Reco unavailability cannot roll back the saved roadmap and uses existing retries.
Conversation-source impressions without that reference fail with
`AGENT_COMMIT_REQUIRED`. Public IMP-01 cannot forward client commit authority.
The first impression stores its proposal reference; `(request_id,dish_id)` deduplication
prevents repeat counting. Saving creates no feedback, `m` increment, implicit update
or long-term taste-vector update.

## Rollout and verification

1. Apply reco migration `0004`, then deploy reco.
2. Apply backend migration `20261029153201_AddConversationPreferencesLifecycle`, then deploy backend.
   It stops with roadmap/conversation IDs if duplicates exist; resolve them explicitly.
   The earlier `ReplaceRoadmapVersionWithUpdatedAt` migration is preserved.

Use a dedicated PostgreSQL database whose name contains `test`:

```powershell
$env:RUN_POSTGRES_INTEGRATION_TESTS = '1'
$env:ANGI_TEST_POSTGRES_CONNECTION_STRING = '<dedicated test DB>'
$env:ANGI_TEST_RECO_URL = 'http://127.0.0.1:18000/'
$env:ANGI_TEST_RECO_API_KEY = '<internal test API key>'
dotnet test ANGI.UnitTests
dotnet test ANGI.IntegrationTests --filter 'FullyQualifiedName~Agent|FullyQualifiedName~Outbox|FullyQualifiedName~ConversationPreferencesMigration'
```

Real reco tests require both schemas migrated in that same database. They exercise
actual HTTP, PostgreSQL ranking logs, commit/rollback/outbox and impression counters;
only LLM and route-provider boundaries are deterministic stubs. Reco tests require
`RECO_DB_TESTS=required`, followed by `pytest`, `ruff check .`, `ruff format --check .`
and `alembic check`. Skipped external-provider tests do not verify live providers.
