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
Limiting wording ("đừng cay quá", "bớt ngọt") is also an underspecified taste. Taste words are matched
without diacritics only when the whole message is unaccented; an accented look-alike ("chưa", "mận",
"đang", "cây", "tuổi") is never a taste. While a taste question is open, a reply with no intensity, scope,
taste or duration/budget ends that question and goes to the model instead of repeating it. Model-chosen
taste_intensity/taste_scope questions name the tastes in the message and use the same scale (không, nhẹ,
vừa, nhiều, rất nhiều). An edit made only of an edit verb, fillers and pointing words ("đổi cái đó đi")
over an existing plan, without a selected item or focus, gets the fixed edit_scope question before any LLM call.
Novelty wording ("món chưa ăn bao giờ", "món lạ", "lạ miệng", "món mới") whose meaning is not already "different
from this plan" gets the fixed novelty question before any LLM call; no catalog tag means new, and refused novelty
("không cần món lạ") is not asked about. Choosing "never eaten" is answered that ANGI has no eating history and
offers dishes different from the current plan (or, without any plan, plans by taste); it never becomes a dish-name
lookup or an invented filter. Over an existing plan, a create/patch proposal answering a message with no change
wording, taste, day/meal or new user constraint grounded in that message ("ăn vui vui", "ok") is not accepted; a tag,
dish or restaurant the model adds counts only when the message names it (a "Món ăn vặt" tag invented from "ăn vui
vui" does not). The plan and turn-start state
stay unchanged and the general question is asked, without a pending question that would block saving.
A scoped create_proposal that also copies unchanged rows outside its scope ignores those copies; a changed row
outside the scope is still rejected, with repair feedback naming its day and meal. Malformed get_recommendation
arguments (scope outside the trip, unknown tag, topK, price, radius, IDs), a get_roadmap ID not bound to the
conversation, a route with unissued dishes and an invalid search_catalog name return invalid_arguments with the
reason to the model within the bounded tool rounds instead of failing the chat turn; no unbound roadmap data is ever
returned and schema violations still fail the turn. An unmatched catalog lookup (name not found, near matches) is kept
for the turn, so catalog_choice/catalog_support questions repeat it even after later rankings.
A clarify/answer turn with an unmatched lookup does not keep the dish/restaurant names it added to mealRequests:
an unsettled name is not a user constraint, so naming another dish/restaurant next turn needs no change confirmation.
A found lookup is never a clarification reply; only an answer to a direct catalog question repeats it.
When the user answered the taste question with one taste, an explicit level and a day/meal/trip scope, and the model
still asks taste_intensity or taste_scope, the model gets one repair stating the resolved target and placement.
The same question also asks the duration and per-person budget (total or daily) still missing from the
retained request; province is left to planning. A clause that is only an amount with a unit ("500k 1 ngày",
"khoảng 2 triệu") answers the budget question; an amount inside another clause ("món dưới 50k") does not.
A recommendation request while province, duration or budget is missing is not a contract failure: the
turn returns the fixed trip_details question, keeps the accepted state including tastes, and makes no
further LLM call. Explicit "bữa N ngày M" / "bữa trưa ngày M" wording binds a taste to that meal: "bữa N"
is the N-th declared meal (default breakfast, lunch, dinner). A whole-day override the model newly proposes
for a day whose meals the user named is narrowed to those meals, unless the same day is also named
without a meal. A meal number beyond the declared meals is asked about before any LLM call. Itinerary
replies list each applied taste with its level and scope.
A dish or restaurant named without a meal ("phải có món X", "phải ăn ở quán Y") means one meal of the trip: the backend
moves it from the trip/day request to the first free, fitting meal (lunch, dinner, then breakfast) before ranking, and keeps
that placement when the model resends the wide request. Wording naming it with "mọi bữa", "cả chuyến", "chỉ ăn" keeps the
wide scope. Duration and budget stated in the current message override the model's copy ("500k 1 ngày" can never become
1,500 đồng); constraint confirmation still applies. On a trip_details clarify the model returns every value stated so far.
An answer or an unrequested-change result closes any open non-taste question, so it cannot keep blocking save. "giữ nguyên",
"thôi", "không" declining an open question over a ready proposal close it without an LLM call; a blocked save names the open
question. A bare acknowledgement ("ok", "thôi", "hmm") over a plan with nothing open, and a dislike naming nothing ("cái này
không ngon"), get a fixed reply before any LLM call; pointing words are refused as a catalog name.
A whole-message removal of a meal with its day, a whole day, a meal on every day or an exact dish name ("xóa bữa tối ngày 2", "xóa
ngày 3", "bỏ bữa trưa toàn chuyến", "bỏ món Phở Gà", or a bare removal of the UI-selected item) is applied without an LLM call and
returns a new preview, keeping numDays; "bỏ bữa sáng" alone asks which days, without an LLM call; a meal holding a named request goes to the
model so constraint confirmation applies. Backend-owned `skippedMeals` lists meals removed on purpose. A declared meal may stay
empty when skipped or when the source already lacked it; a gap the edit creates, a new day or a new meal slot still blocks.
Removing every item of an unsaved preview points to discarding it. Backend-owned `replacedDishNames` (last 40) keeps replaced
and removed dishes out of later rankings of the trip unless they are the only candidates or requested by name, so repeated "món
khác" does not cycle. A proposal only over budget is repaired before the model repair: the most expensive new dishes are swapped
for cheaper dishes already ranked for that position and constraints (ranking keeps at least 30 names per meal as reserve grants,
never sent to the model); requested names and retained rows stay, and the budget question is asked only when nothing cheaper fits.
A conversation whose roadmap was deleted answers ROADMAP_NOT_FOUND with the way forward; "làm lại từ đầu" detaches it and starts empty.
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
Each ranking also keeps the rest of its ranked distinct names as a reserve grant for the same position and
request; reserves are never sent to the model and only serve this repeat repair, after the issued grants.
Edits and alternatives exclude dishes whose name equals a dish they keep or replace, at any restaurant.
Remaining repeats are disclosed as no other suitable ranked candidate; names the user requested are exempt.
The backend never widens grants to make a misplaced selection pass.
Confirm verifies stored provenance and catalog again without calling LLM/reco. Legacy retained items
remain committable. All saved dishes still go through the existing transactional impression outbox.

## Grounded replies

The model reads and words every chat message; the backend guards from behind (project owner, 2026-11-05). `action=chat` is shown after
`AgentReplyGuard` rejects anything with digits, prices, a claimed change, a link, markup, internal terms or more than 200 characters,
falling back to a fixed sentence. Greetings, thanks, help, vague edits, a bare "ok", taste level and scope, novelty and a meal the plan does
not have reach the model and get no backend reply first; the model words `edit_scope`, `general`, `taste_*`, `novelty` and `trip_details` questions.
Questions resting on what the backend observed (coverage, budget, catalog choice) stay fixed.
Still handled before the model until they are moved: moving a day of an existing plan to a named place, removing a named item, and saving.
`action=food_info` answers a general question about what a named dish is like (taste, main ingredients, origin, how it
is eaten) from the model's common knowledge. The backend drops the wording if it states a number, a health or allergy claim,
a change or an internal term, and offers to find the dish.

A request for a dish idea (`gợi ý một món`, `hôm nay ăn gì`) is not a trip: it never asks for place, duration or budget.
The model calls `suggest_dish` once, then writes
the reply in a second call around slots (`{name1}`, `{restaurant1}`, `{price1}`, `{address1}`, `{meals1}`, then `{name2}`...), which the backend fills
from the tool's facts. When the model leaves out a dish's address, or the meals of a dish that only suits some meals, the backend adds them, so the user always knows where to go.
A place the user names for the idea (`nhà tôi ở Cầu Giấy`) goes to the tool's `area`, which the backend locates and searches around; a place it cannot locate is reported, not ignored.
The reply is shown only if every dish has its name, restaurant and price slots kept together, the words around the
slots state no number, name or dish the user did not write, and it claims a taste ranking only when `personalized=true`; otherwise, or when
that call fails, the fixed reply is shown with no retry. A taste stated in the request goes to `tasteTargets` for that ranking only, and a
taste with no level means 0.75 there (user decision on 2026-11-05); it is not saved, and it does not change the rule for plans.
The reply says how it read such a taste and invites a change. `personalized` is true only when the user has a taste profile and reco confirms
it ranked with the vector, because reco ignores the vector of a user with no interactions. No grant is issued and the plan and state stay unchanged.
Stating a taste with no plan, place, duration, budget or open question anywhere (`tôi thích ngọt`) is read by the model as a request for an idea
rather than opening the planning questions (user decision on 2026-11-05). Dishes already in the plan are left out unless nothing else fits, and the reply
invites adding the dish by naming its day and meal. `DishOffered` marks that for one turn, so "ok" is read against the offer: it is not a bare
acknowledgement, and an open save question is dropped so that "ok" cannot save the plan. Adding uses the ordinary edit path to a new preview; nothing
is written until the user saves. A question about the user's own
favourites (`món sở trường của tôi là gì`) is `action=chat`: ANGI holds no such data, so the model says so and offers a suggestion;
a chat, answer or worded clarify reply is replaced when it uses a word that starts at least 5 catalog dish names and nothing it may
quote backs it. Rare dish words are not checked, because many are everyday words (`vui lòng`, `ba ngày`).

A point pinned on the roadmap gives the province and a search circle of at least 10 km, so a new plan does not ask where to
eat. A model state that leaves province, duration, budget or pin empty keeps the current value; only a pin the user names can
be removed, and that still asks for confirmation.

A trip longer than 14 days is assembled by the backend right after one `get_recommendation scope=full`, drawing on the reserve
list and repeating the least used dish only when a meal runs out. It costs one provider call, writes no dish notes or day
summaries, and fetches no route legs in chat. A source over 60 items reaches the model cut to the days the message names
(`sourceIsPartial`, `sourceItemCount`).

`action=out_of_scope` covers anything outside dishes, restaurants and the food itinerary, including general food
knowledge the system holds no data for (recipes, nutrition, a dish's history). Its reply is always backend wording, one of
`AgentReplyGuard.OutOfScopeReplies` chosen by turn (decided by the project owner on 2026-11-04); no model text is shown. For a food question, the
nullable `suggestedDish` adds one offer to find that dish, only when the user's own message names it.
Both actions run before state acceptance and return the state from the start of the turn: they cannot change a
constraint, close an open question or join the pending planning request, and the current proposal stays savable.
A greeting and a refusal both repeat the question still open.
A new preview is announced in a few sentences (place, days, total, empty meals, required disclosures); an edit names
what changed and says the rest stayed. Its dishes are shown by the proposal card and map, never repeated in chat. The
itinerary is listed in chat only for a request to see a saved plan (`AgentConfirmationPolicy.IsPlanViewRequest`) with no ready
proposal on screen.
Decided by the project owner on 2026-11-03: the model also words `answer` replies, `edit_scope` and `general`
clarifications (when no tool ran and no trip detail is missing) and a fact-free opener for a preview. `AgentReplyGuard`
shows that wording only if every number and mid-sentence capitalized name appears in the plan, trip state, this turn's
tool results or the user's message, and it makes no change claim, internal term, link or unbacked claim (opening hours,
fame). It catches an invented lowercase dish name only when that word starts at least 5 catalog dish names. Any failure shows the fixed reply. Budget,
coverage and catalog questions stay fixed because they rest on what the backend observed. Taste and novelty questions are worded by the model since
2026-11-05. A taste level the model adds or changes in state must carry `levelQuote`, the user's own words for it: the backend checks the words were
written and say more than the taste itself, drops the quote, and otherwise refuses the ranking and the proposal so the model asks instead. `suggest_dish` is exempt because it saves nothing.
A preview's opening sentence is worded by the model around `{total}`, `{numDays}`, `{place}` and, for an edit, `{changes}`, filled once the plan is validated, so no extra provider call is needed. A sentence that lacks `{total}`, lacks `{changes}` on an edit, or whose own words state a number, name or dish falls back to the fixed summary. Disclosures (missing meals, no budget, rating fallback, tastes applied, substitutions) stay backend-written after it.
Decided by the project owner on 2026-11-04: the model also words a `trip_details` question about a missing duration or budget when
the place is known; the place question stays fixed. A stated "sẽ đổi" is a promise only the backend may make, but it is allowed in a question.
`chat` and `food_info` replies that look like code are dropped, and a `chat` reply naming something nobody stated is too. A `reply_replaced`
event, without the text, records each time a check drops the model's wording.

Saving is handled by `AgentConfirmationPolicy` without the model, also decided on 2026-11-03. Saving with no known start date
asks for one and names the planned days; the answer ("3/11", "từ 3/11 đến 5/11", "ngày mai", "thứ 7 tuần sau", read by
`AgentConfirmationPolicy` in Vietnam time) saves at once. A date already past or a range whose length differs from the plan is
asked again and never changes the plan; "đổi thành N ngày" is a normal constraint change. "Để sau" saves with no date,
because `roadmaps.start_date` stays nullable. A bare "lưu" with a known date asks "Mình lưu lịch hiện tại … nhé?", and a
short yes ("ok", "ừ", "đúng rồi") then saves. Backend-owned `pendingSave` ties that yes to the exact proposal ID and
version asked about; it lives one turn, and clients or the model cannot set it. A plain yes without it saves nothing.
A start date stated after the preview does not need a new preview, since it does not change the dishes. AGENT-04 accepts
an optional `startDate` for the save dialog.
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

## Pre-plan questions read by the model (user decision on 2026-11-05)

Replaces the reading of the answers described in the 2026-11-03 section below; the steps and their order did not change.

- The model reads every message of a new trip and words the questions: place (`trip_details`), scope after a bare province (`place_scope`), duration and budget (`trip_details`), then preferences (`preferences`). `planningIntake` records the stage from the kind of question, so the frontend's quick replies still work.
- The backend keeps the order from state alone, never from the wording: `get_recommendation` and a proposal wait until the place, duration and budget are known and the preferences question was asked or the state already holds a taste or request; a trip submitted through the constraints form plans at once. A question ahead of its turn is replaced by the backend's question for what is missing, and wording that fails its check shows the fixed question of that kind.
- Places go through the `locate_places` tool: the model passes the names it read, and the backend finds the coordinates, radius and district, stores them and shows its own note above the next question. Over an existing plan the tool is refused, because moving a day keeps its own path.
- A province outside the catalog is dropped, and the reply names where ANGI has data. Stated numbers still override the model's reading through `AgentIntakePolicy.CaptureRequest`.
- With no plan, stating or correcting the place and duration, or leaving an unset budget open, needs no confirmation; a budget already set still does.
- Shared coordinates and a pinned point carry no language and stay outside the model; a point with no restaurant nearby gets its fixed answer with no provider call.
- Asking for one dish while the questions are open closes them. Cost: roughly one call per question, four or more per new plan, where these turns used to be free.

## Conversation history (2026-11-05)

Tool rows no longer count against history. `Agent:MaxHistoryMessages` (20) bounds the user and assistant messages sent to the model, and `Agent:MaxTranscriptMessages` (1000) bounds those returned to the chat screen, so a reload restores the conversation. Before this a reload showed 12 of the 50 messages of conversation 158, and the model lost what the user had said after a few tool-heavy turns.

## Pre-plan questions and empty replies (2026-11-03)

- A new plan (no ready proposal and no saved item) asks in a fixed order without a provider call: place, then
  duration and budget, then optional taste and day/meal requests. Values already known are skipped, and values in
  one message are all taken. `AgentState.planningIntake` holds the stage; it is backend-owned JSONB metadata like
  `pendingPlanningRequest`, so there is no migration.
- The place comes from the user's words or from coordinates, never from a roadmap title. Coordinates arrive through
  the existing `confirmedState` (`originMode=gps`); the nearest public restaurant within 50 km names the province,
  and candidates are limited to 10 km. No map provider is called. A location with no restaurant nearby keeps no
  coordinates and asks for a place.
- A long reply that answers nothing closes the questions and goes to the model. Changing a duration the roadmap
  holds, or a budget with no limit, still needs the existing separate confirmation.
- Decided by the project owner on 2026-11-04: at the duration/budget question, any reply stating no value goes to the model whatever its length
  (`intakeAside`), because spoken wordings cannot be listed. Budget is optional in chat, as in RM-06 and RM-10: a user who has not decided
  (`chưa chốt ngân sách`) continues under `budgetUnrestricted=true` with no second confirmation, and the reply and the proposal card say no budget
  applies. Once the trip is settled this way, the preferences question still comes before any plan: the backend asks it, and `get_recommendation`
  returns `missing_trip_details` until then. A reply at the scope question keeps its old behaviour.
- `reply` may be empty for any action: the backend has fixed wording for all of them. Treating it as malformed made
  `out_of_scope` (`huong dan toi`) spend 2-4 calls and end in a 503. A bare request for help is answered without a
  call. Output still unusable after its repair, and invalid model state, ask again instead of returning 503.
  AGENT_UNAVAILABLE remains for provider failures, oversized context and exhausted tool or call limits.

## Itinerary by day and places inside a province (2026-11-03)

- `Ngày 1 ở Hoàn Kiếm, ngày 2 đi Lăng Bác` is read by `AgentPlaceResolver` without a provider call and stored in the
  backend-owned `AgentState.dayAreas` (JSONB, no migration). Clients and the model cannot write it. A day without an area
  uses the whole province or the trip origin. One area without a day (`ở Cầu Giấy`) becomes the existing pinned origin
  and radius. The province is inferred from the places; a day past the known duration extends the trip, and days the
  itinerary leaves out are stated in the reply. Plans still hold one province.
- `AgentPlaceResolver` looks a name up in this order: province (whole province), landmark coordinates, catalog addresses, then
  Mapbox Search Box. Search Box returns confident wrong answers (`Lăng Bác` gave `Làng cổ Bắc Biên`, nonsense gave `Hà Nội`),
  so its result is used only when its name or address contains the user's whole phrase and it is not a city-level type.
  Without Mapbox the resolver stays catalog-only. A place that cannot be found is reported and that day uses the province.
- The radius is the smallest of 3, 5, 8, 12, 20, 30 and 50 km holding 20 restaurants. Above 12 km the reply says the search
  reaches the nearest restaurants. It counts restaurants, not dishes per meal, so a thin meal still appears in `mealCoverage`.
- `Fingerprint` includes a day's area only when one exists, so proposals saved before this change still confirm. One
  `scope=full` call splits into a search per area, each with its own grant, and validation checks each dish against its day.
- On an existing plan `Ngày 2 tôi ở Cầu Giấy` updates that day and passes `itineraryChangedDays` to the model; a day past the
  trip goes to the model because changing a saved duration needs confirmation.
- A place with no day on an existing plan (`đổi sang khu Cầu Giấy`, `ở Hoàn Kiếm thôi`) asks which days, without a provider call,
  and holds the place in the backend-owned `pendingDayArea`. `ngày 2`, `ngày 1 và 3` or `cả chuyến` moves those days; any other
  reply drops it and continues normally. A one-day trip moves without asking. `đổi sang <tên>` without `khu` or `hành trình`
  could be a dish, so only catalog addresses may confirm it. With no plan yet, the same short wording starts the pre-plan questions
  with that place as the area of the whole trip. A bare name with no such word still goes to the model.
- A model output the builder rejects twice no longer leaves a chat turn as 409 `PROPOSAL_NOT_COMMITTABLE`. A new plan is
  assembled from the issued candidates with no further call; an existing plan stays unchanged and savable.
- A new plan whose user declined extra requests closes tools once one search covered every meal, which removes the
  repeated `get_recommendation` rounds seen in conversation 94.
- `Agent:MessagesPerMinute` is now 60. `MessagesPerDay` stays 100.
- Near-first order applies only to a place the user named: a day in `dayAreas`, or a whole-trip area marked by the backend-owned
  `namedAreaLabel`. Reco's scores are split into bands of 0.05 from the best dish and sorted by distance inside each band, so a clearly better
  taste stays ahead. The nearest branch of a dish is sent to reco, and the list is requested up to `MaxRanked` so ties are seen; dishes past the
  meal quota stay out of the model's context. The whole province, GPS, a map origin and a rating fallback keep their previous order and depth.
- Names of other provinces and well-known destinations are refused before the map, because `Đà Nẵng, Hà Nội` returns a Hà Nội business with that
  name. A street keeps its word (`phố Huế`) and is not refused. This list is static, so an unlisted distant place can still match a local business.
- A meal with no dish inside a named place searches further along the radius steps; only that meal may use the wider radius (`widenedMeals`, backend-owned), the
  reply states it, and moving the day drops it. A meal with nothing at 50 km still gets the coverage question. The whole province, GPS, a map origin and a requested
  dish or restaurant keep their previous behaviour.
- A map result is trusted only with evidence, because a business can carry any place name (`Hồ Gươm, Hà Nội` returned `Hồ Gươm Plaza` in Hà Đông): an area or street containing the
  phrase, a business named exactly that, or most businesses with the phrase in their name within 2 km of each other. Common Hà Nội landmarks use fixed coordinates, and replies show the district of the nearest restaurant so a wrong location is visible early.
- `đi tự do` and similar wording mean the whole province for that day. A complaint such as `ngày 1 sai` on a plan with `dayAreas` is handled before the model: with the right place the day moves; without one the reply says where it searches and how to fix it. Complaints about a dish or meal stay with the model.
- A message naming nothing but the province asks the scope once (`planningIntake=scope`, no provider call): the whole province, an area, an itinerary or the current location. Any other reply keeps the whole province and says so. A message that already gives days, a budget or a taste keeps the previous flow without this question.
