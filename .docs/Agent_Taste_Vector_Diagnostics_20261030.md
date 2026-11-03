# Agent taste clarification and vector diagnostics — 2026-10-30

Scope: Agent/backend clarification and user-facing explanations. The user's instruction forbids
changes that alter angi-reco. No reco source/configuration/scoring, cold-start parameters,
profile counters, vector rows or retry counters are changed by this investigation.

## Conversation 10 evidence

The full read-only transcript contains one failed initial spicy request, then a completed
three-day sour request and a five-day spicy revision. The sour request created proposal 10
(version 1, 12 items) with sour=0.75 without asking about intensity or scope. The revision
asked only to confirm five days and a 2,500,000 VND total budget. The user's subsequent
"Xác nhận thay đổi" did not mention taste intensity or scope, yet the Agent created the
next proposal with spicy=0.75. Neither completed request contains human taste resolution.
The stored empty tasteOverrides and trip-level tasteTargets show the inferred whole-trip scope.

The latest checked proposal is version 2, ID 13, with 15 items. All selected items reference
reco request f2ff4532-0e91-47c5-a55b-4ae9d98dc668. Reco processed 2000 candidates with
vector_source=conversation and spicy=0.75, but m=0 and delta=0. Every candidate's explicit
and final recommendation scores were zero. Cold-start is intentional and remains unchanged.

The user said they liked spicy food without supplying an intensity or trip/day/meal scope.
No human clarification preceded the stored target 0.75. The prompt's immediate-planning
instruction competed with its conditional clarification instruction; shape/range validation
could accept a model-selected 0.75 without proving human resolution. The final reply was
constructed from catalog data and did not disclose the limitations of soft taste matching.

The proposal builder retained historical grants. Fallback warnings inspected any retained
grant with a null request ID even though every selected item had fresh reco provenance.

## Backend correction

- Check direct Vietnamese taste-preference phrases before LLM/tool execution. Ask unresolved
  intensity and scope together, keep the existing state and preview, and reuse explicit human
  resolutions from history. Collect partial answers without manufacturing a target.
- Do not treat confirmation of a trip/budget change as confirmation of an unspecified taste.
- Prioritize taste clarification over immediate planning in provider instructions. Other
  natural-language phrasing remains model-interpreted; the lexical guard is deliberately bounded.
- Render a soft-preference explanation when taste targets/overrides exist. A recommendation
  request ID confirms a processed request, not a positive personalization weight or a guarantee
  that each selected dish satisfies the desired intensity.
- Derive fallback warnings from selected item provenance rather than unused historical grants.

No public field, endpoint, database schema or reco request/response contract is added.

## Dish-vector failure evidence

Read-only checks found 2143 backend dishes and 2143 reco dish records, with zero active dishes
missing from reco. The records arrived between 09:31:42.688170Z and 09:32:22.861843Z on
2026-10-30, a 40.173673-second interval. Synchronization and vector generation are separate stages.

The last investigation snapshot found all 2143 vectors failed: 2142 RateLimitError failures
(HTTP 429) and one InternalServerError failure. No usable model output/vector was stored;
raw_ai_output was JSON null. Attempt counts were 1..2. The configured vector-generation model
was gemini-2.5-flash at Google's OpenAI-compatible endpoint, with a 30-second call timeout.

RECO-01 schedules generation in background after acknowledging a new/changed dish. Bulk upserts
can therefore initiate overlapping provider requests; successful synchronization does not mean
successful generation. The rapid bulk intake is a plausible contributor to rate limiting.
The saved errors contain only the HTTP code and exception class, not quota metrics or retry
details, so they cannot establish which of RPM, TPM, RPD or another provider limit was reached.

This is not evidence that the taste ontology cannot interpret the dishes. It is also not an
arithmetic error in reco: missing dish vectors intentionally contribute zero explicit similarity,
and m=0 intentionally gives personalization zero weight.

## Recovery within the user's boundary

1. Inspect the actual Gemini project's active limits and usage for gemini-2.5-flash in
   [Google AI Studio](https://aistudio.google.com/rate-limit). Limits are per project, not per key:
   [Google rate-limit documentation](https://ai.google.dev/gemini-api/docs/rate-limits).
2. Restore provider capacity or wait for the relevant limit to reset. Code cannot grant provider quota.
   No model/key/container configuration is changed as part of this fix.
3. Let the existing reco regeneration worker retry eligible rows after provider capacity is available.
   It selects at most 100 pending/failed rows per pass, waits five minutes between passes, and
   permits at most five attempts per content. At the checked 1..2 attempts, rows remain eligible.
4. Verify successful rows become ready with a non-null vector through the existing validation path.
   Avoid replaying a bulk seed merely to address provider failures; unchanged content is not a
   request to generate again, and re-uploading cannot fix provider quota.

Tests use fake LLM/reco boundaries; this investigation does not send new live generation requests.

HTTP regression coverage replays both completed planning prompts, verifies no LLM/ranking call
before clarification, persists partial answers, and checks that an existing preview remains
visible while both chat-save and the confirmation endpoint reject saving unresolved changes.
All write-side checks run against a dedicated test database; conversation 10 remains untouched.

Validation: the Release solution test run passed all 2629 unit tests and 358 PostgreSQL/HTTP
integration tests. Three opt-in live-service groups were skipped. All three new unscoped-taste
HTTP regression tests passed. The changes remain local until explicitly committed and deployed.
