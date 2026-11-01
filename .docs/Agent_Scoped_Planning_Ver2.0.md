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

Broad ranking calls spanning different effective tastes or meal requests are split by the backend
(user decision 2026-11-01): one call ranks each homogeneous day, or each declared meal of a heterogeneous
day, with its own vector, filters and day/meal grant, and returns `scopedResults` with per-part
selectionScope. The LLM states intent; it does not plan ranking scopes. Homogeneous calls keep their shape.
Backend applies all Agent tags and compatible meal filtering before the candidate limit, allowing
untagged meals. Discovery's existing grouped-tag behavior remains the default for other modules.
Backend rechecks every ranked ID, current catalog, scoped constraints and meal compatibility.
Return explicit candidate/ignored/returned counts and coverage warnings; at most k is not exactly k.
Vector pending/failed alone does not mean ignored. Never relax filters to fill a plan.
User decision 2026-11-01: the backend reads up to 2000 filtered rows in rating order and keeps one branch
per normalized dish name (the first, best-rated restaurant) before applying Agent:MaxCandidates, unless a
dish was requested by name. Breakfast, lunch and dinner rank only `local_sf_main` dishes by default and the
validator requires one main dish in each new or reselected main meal. Any category tag, dish or restaurant
requested for that position, or a category search filter for a side item, replaces the default.
Conversation ranking waits Recommendation:AgentTimeoutMilliseconds (default 3000 ms); other callers keep
TimeoutMilliseconds (800 ms) and their rating fallback.
AgentState.mealSlots defaults empty for legacy compatibility (coverage not declared); ordinary new
plans declare breakfast/lunch/dinner unless the user requests fewer. Validator requires declared
coverage for every day and rejects missing meals. Empty existing-roadmap updates remain valid.

## Scoped changes and provenance

Ask only for unresolved scope, taste intensity or novelty meaning; explicit day/meal wording is enough.
Direct underspecified taste preferences are checked by a conservative backend guard before LLM/tool
execution. A phrase such as "thích ăn cay" does not authorize any numeric target or whole-trip scope.
Ask missing intensity and scope together, preserve the existing state/proposal, and collect partial
answers across completed user turns. Reuse explicit human resolutions, not old model-chosen numbers.
Taste clarification retains a backend-owned `pendingPlanningRequest`: the original request, bounded
recent partial answers, and explicitly stated duration/total or daily budget. The active trip and draft
stay unchanged while taste is unresolved. When resolved, those explicit facts override stale model
copies of trip values before state acceptance or ranking. Daily budgets use the requested duration.
The existing constraint-confirmation policy still applies; a complete proposed change, including taste,
is transferred to `pendingStateChange` and applied only on confirmation. Successful previews and
discard/reset clear pending clarification memory. Clients and model output cannot forge this metadata.
Model-chosen taste scopes cannot set/clear navigation `focusDay/focusMeal`; only the current client-owned
focus is retained, including when staging and accepting a change. This keeps a day-2 taste exception
from blocking recommendation requests for a newly added day 4, while preserving explicit UI scope pins.
Constraint-change confirmation does not confirm an unresolved taste. The provider prompt gives this
clarification priority over immediate preview generation. Other wording remains model-interpreted.
Keep edits outside mutation scope unchanged. Changed effective taste/meal selectors within a source
item's scope require fresh matching grants even when its dish ID is retained (except future taste-only
updates). Future-only changes produce a reviewed revision with the same dishes so it remains savable.
Fingerprints use effective targets/vector and applicable meal selectors, not unrelated days.
Backend derives rankingFingerprint on validated items, never accepts it from model output.
Reevaluated retained dishes keep item identity/feedback but receive their new recommendation request ID.
`create_proposal` is a complete selection payload for its scope, accepted for both initial planning and
updates. The backend chooses creation/revision from the owned source, merges unaffected rows, and
derives changes. Supplied stale base versions are rejected. Snapshot row keys are reconciled with
source keys by exact key or day/meal/sort position; selected-item edits cannot replace other rows.
`patch_proposal` remains available for explicit deltas with an exact base version. Reselected rows,
including unchanged dish IDs, require fresh scoped grants and use their new request IDs; retained
rows outside an edit keep their provenance. Business-validation repair receives the rejected draft
and its concrete errors, including the rejected day/meal and issued candidate scopes. Schema repair,
proposal repair and unsupported-coverage repair each have one independent attempt, all within the
existing LLM-call, tool, context and total-time limits. A repaired schema does not consume proposal
or coverage recovery. Coverage recovery preserves the rejected selection and validation feedback;
if recovery still fails, the final response retains the blocking validation instead of hiding it.
Recommendation tool results include selectionScope and per-item allowedMealSlots. Catalog meal tags
do not widen a day/meal grant. Model context also lists the issued candidate scopes. Reuse adequate
full-trip results rather than repeating identical searches. A dish name should not repeat across the
itinerary while an unused authorized dish fits that position: the validator flags such new repeats without
blocking, and the backend swaps them for the best unused grant of the same position without another LLM call.
Remaining repeats are disclosed as a catalog limit; names the user requested are exempt.
The backend never widens grants to make a misplaced selection pass.
Confirm verifies stored provenance and catalog again without calling LLM/reco. Legacy retained items
remain committable. All saved dishes still go through the existing transactional impression outbox.

## Grounded replies

LLM reply text is an internal draft and is not forwarded as factual user-visible text.
AgentOutput.clarification is nullable and allowlisted: trip_details, taste_scope, taste_intensity,
edit_scope, novelty, catalog_choice, coverage, catalog_support, general.
Backend constructs proposal summaries, catalog failure messages, clarification questions and answers
from validated snapshots/tool outcomes and fixed templates. A general clarification never offers to
review a proposal that does not exist; after searches without a valid proposal it reports incomplete
planning. Constraint-consent questions name each changed day/meal request and mention the unchanged
plan only when a preview or roadmap exists. Names/prices/totals are
backend-derived. Unknown opening hours, fame, dietary certainty and ranking explanations are omitted.
Snapshots remain authoritative for consumers; factual correctness is limited by catalog freshness.
Itinerary replies start with “Đây sẽ là roadmap khám phá ẩm thực của bạn trong {numDays} ngày.”
and list every day with breakfast, lunch and dinner headings and one bullet per selected snapshot item,
ordered by day, meal and sort order. Item counts follow the validated recommendation selection; no
fixed three-item count or six-item truncation applies. Empty meals are explicitly marked without
inventing dishes. Snack/late-night headings also appear when selected or requested. Restaurant names,
prices, totals and preview/saved status remain backend-derived. Replies omit the change summary
(added/replaced/removed/moved counts); structured snapshot changes remain available to consumers.
Fallback disclosures follow selected items' request IDs, not stale grants retained from earlier drafts.
Profile/ranking transport, configuration or contract failures may use rating fallback. A proposal
assembly/action failure never substitutes an old fallback plan as the current result: the turn returns
`proposal=null` while the previous draft remains available through GET conversation. Successful
reselection replaces old fallback provenance with current reco request IDs.
When a trip/day/meal taste target is present, explain that it is a soft priority rather than a guarantee
for every dish, and that matching depends on dish data and interaction history. A reco request ID proves
service processing, not nonzero personalization weight. Reco scoring and cold-start remain unchanged.

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
