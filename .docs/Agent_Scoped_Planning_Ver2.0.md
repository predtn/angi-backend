# Agent scoped planning v2 (2026-10-30)

User-authorized backend-only extension. This contract supersedes overlapping Agent Preferences v1
descriptions. Frontend and angi-reco implementations, endpoints, scoring, cold-start, feedback,
transaction fencing, confirmation and commit-only impressions remain unchanged.

## State, persistence and compatibility

AGENT-01..04 keep their URLs, envelopes, status/error contracts and prompt-only request support.
AgentState schemaVersion remains 1 with additive default-empty arrays: tasteOverrides and mealRequests.
Legacy JSON without these properties retains whole-trip tasteTargets and existing provenance.
All state stays in core.agent_conversations.state JSONB and proposal snapshots: no new table/column/migration.
preferences.baselineVector is the frozen reco profile snapshot; effectiveVector remains the trip default.
preferenceApplication is replan (default) or future (explicitly requested future suggestions only).

tasteOverrides entries: {dayNumber,mealSlot,targets:[{dimension,target}]}; dayNumber is required,
mealSlot is null for a day or a supported meal. Scope keys must be unique, within numDays.
Targets retain the existing twelve dimensions and 0/.25/.5/.75/1 scale.
Apply baseline -> tasteTargets -> day targets -> meal targets, replacing only specified axes.
An explicit change to every meal must also update/remove overlapping exceptions on those axes;
ask whether exceptions remain only when that intention is ambiguous. Preserve unrelated axes.
Compute request vectors on demand and send the existing conversation user_vector_override to reco.
Do not change the profile, m, scoring weights or impression behavior. Empty/zero vectors remain null.

mealRequests entries: {dayNumber,mealSlot,dishName,restaurantName,restaurantId,tagIds,differentFromPlan}.
Null day/meal = whole trip; a meal needs a day; keys are unique. Names are bounded catalog selectors,
not invented IDs. Tag filters combine by AND across applicable scopes; the most specific supplied
name wins. restaurantId is nullable, selected from verified name lookup after the user resolves
a duplicate branch; it requires restaurantName. differentFromPlan excludes source itinerary dishes
and forbids duplicates across final meals; unused candidates from other scopes remain eligible.
It means different from this plan, never a verified history of everything the user has eaten.
Specialties require real catalog tags; unsupported specialty/novelty semantics require clarification.

## Internal tools and candidate authorization

Add read-only search_catalog {kind:dish|restaurant,name,province,selectedId}; no new public endpoint.
Only public verified/visible nondeleted restaurant data is searchable. Return bounded matches,
availability and exact/ambiguous/not-found outcomes; a closed or sold-out match is not absent.
Lookup is case-insensitive with escaped literal search. Near matches are suggestions, never authority
to silently substitute a specifically requested name. Duplicate exact names need disambiguation.
get_recommendation resolves applicable named selectors before ranking. A missing/ambiguous/unavailable
selector produces a trusted clarification and no candidates. Validate final names/tags against catalog.
No catalog import, web lookup, synthetic dish or inferred menu membership.

Broad ranking calls spanning different effective tastes or meal requests return a scoped-call hint;
the Agent must call homogeneous day/meal scopes within existing limits. Reuse identical vectors.
Backend applies all Agent tags and compatible meal filtering before the candidate limit, allowing
untagged meals. Discovery's existing grouped-tag behavior remains the default for other modules.
Backend rechecks every ranked ID, current catalog, scoped constraints and meal compatibility.
Return explicit candidate/ignored/returned counts and coverage warnings; at most k is not exactly k.
Vector pending/failed alone does not mean ignored. Never relax filters to fill a plan.
AgentState.mealSlots defaults empty for legacy compatibility (coverage not declared); ordinary new
plans declare breakfast/lunch/dinner unless the user requests fewer. Validator requires declared
coverage for every day and rejects missing meals. Empty existing-roadmap updates remain valid.

## Scoped changes and provenance

Ask only for unresolved scope, taste intensity or novelty meaning; explicit day/meal wording is enough.
Keep edits outside mutation scope unchanged. Changed effective taste/meal selectors within a source
item's scope require fresh matching grants even when its dish ID is retained (except future taste-only
updates). Future-only changes produce a reviewed revision with the same dishes so it remains savable.
Fingerprints use effective targets/vector and applicable meal selectors, not unrelated days.
Backend derives rankingFingerprint on validated items, never accepts it from model output.
Reevaluated retained dishes keep item identity/feedback but receive their new recommendation request ID.
Confirm verifies stored provenance and catalog again without calling LLM/reco. Legacy retained items
remain committable. All saved dishes still go through the existing transactional impression outbox.

## Grounded replies

LLM reply text is an internal draft and is not forwarded as factual user-visible text.
AgentOutput.clarification is nullable and allowlisted: trip_details, taste_scope, taste_intensity,
edit_scope, novelty, catalog_choice, coverage, catalog_support, general.
Backend constructs proposal summaries, catalog failure messages, clarification questions and answers
from validated snapshots/tool outcomes and fixed templates. Names/prices/totals/change counts are
backend-derived. Unknown opening hours, fame, dietary certainty and ranking explanations are omitted.
Snapshots remain authoritative for consumers; factual correctness is limited by catalog freshness.

## Verification

Test scope inheritance/isolation, invalid/null/duplicate scope data, heterogeneous ranking calls,
named catalog absence/ambiguity/unavailability, restaurant menu membership, scoped tag/meal checks,
novelty exclusion, fresh grants for retained dishes, preserved outside scopes, future-only changes,
bounded top-k coverage, malicious free-text replies, legacy JSON/confirm and commit replay/outbox.
Use real PostgreSQL tests for repository SQL and HTTP persistence. No live LLM calls in routine tests.

Verified locally: 2591 backend unit tests; 50 existing Agent/PostgreSQL integration tests plus
5 new scoped planning/catalog integration tests. The real reco HTTP group requires a separately
configured reco service and was skipped. Provider interpretation is covered with deterministic stubs,
not a live-model accuracy guarantee. Dedicated database: angi_agent_scoped_test_20261030.
