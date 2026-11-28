# Conversation-backed mistakes

This optional capability imports the complete conversation collection workflow into Emenda. It is disabled by default. It does not require a Student service, a user JWT, Identity, or StorageReferences in standalone mode. It complements automatic extraction; the former upload and VL structured-analysis workflows were removed.

## Configuration

Configure `Emenda:ConversationCollection` using deployment secrets/environment variables:

```json
{
  "Emenda": {
    "ConversationCollection": {
      "Enabled": false,
      "CallerName": "platform",
      "TutorUrl": "https://tutor.example",
      "AuthorizationBaseUrl": "https://portal.example",
      "CollectionKey": ""
    }
  }
}
```

When enabling the feature, supply an independent 32–256-character `CollectionKey` without control characters. Share it with Portal and Tutor's `QUESTION_COLLECTION_KEY`. The existing product API key is a separate secret. Both URLs must be operator-controlled HTTP(S) origins: no path other than `/`, credentials, query, or fragment. Outbound clients disable redirects. No model name or credential is hardcoded.

## Backend boundary

Every collection route requires the existing middleware-verified `X-Emenda-Api-Key` caller matching `CallerName`, plus `X-Question-Collection-Key`. Browser `Origin` and `Sec-Fetch-Site` headers are rejected. Anonymous development authentication does not grant collection access.

The current wire contract is published at `/contracts/mistake-http-v10.json`. All successful collection responses retain the original `success/data` envelope:

- `POST /api/mistakes/conversation-collections`: `{operationId, studentId, grade, actorId, ownerId}`. Grade is 0–12. Student IDs are opaque nonempty strings, maximum 36 characters; actor IDs maximum 128; owner IDs maximum 256. Identity fields cannot contain control characters. Portal derives these fields from the current authenticated account/student, never from browser assertions.
- `GET /api/mistakes/conversation-collections/{operationId}?studentId=...&actorId=...`: current authorized status/result, scoped by caller, actor, operation and student. Foreign operations return no job fields.
- `POST /api/mistakes/conversation-collections/stop`: `{studentId, actorId}`. Locks accepted work and stops only Tutor operations already marked stopped after a history clear. Newly started, uncleared operations continue.
- `POST /api/mistakes/{itemId}/conversation-analysis`: `{studentId, actorId, evidenceHash, version, model, content}`. Version is 1; model is 1–32 ASCII letters/digits/hyphens; content is nonblank and at most 8000 Unicode runes. Conditional save verifies caller/student ownership and the exact current evidence hash. Teacher root-cause analysis remains separate.

An existing operation UUID cannot be rebound to another caller, actor, student, owner or grade. Receipts, deterministic item IDs and image directories are caller-scoped. HTTP queries exclude conversation records belonging to other callers, including list counts, process logs and structured analysis. Reanalysis status reads also require a currently visible parent; permanent receipts suppress orphaned conversation jobs after deletion, while deleted legacy-job history and trusted null-scope background workers retain their original behavior.

## Current authorization and failures

Emenda POSTs `{actorId, studentId, ownerId}` to the fixed Portal `/api/ai-tutor/collection/authorize` callback, with only the collection key. Portal must re-fetch current association using the actor ID and verify its own canonical student/owner convention. Emenda never reconstructs a user token or hardcodes Portal's owner convention.

Only a valid `200 application/json {authorized:false}` is permanent revocation. HTTP errors, redirects, outages, malformed/oversized responses, and timeouts remain retryable failures. Admission/status/analysis failures return safe unavailable responses rather than pretending to deny access permanently. Workers recheck access before reading and before committing materialization.

## Durable behavior

Schema 18 (`20261118000000_AddConversationMistakes`) adds independent item snapshots, durable jobs and permanent replay receipts after schema 17. It preserves structured-analysis data and every historical migration; schema 19 later retires the managed source tables without touching conversation data. The strict classifier verifies the full catalog; downgrade refuses to discard collection work or snapshots. StorageReferences is not restored.

Workers lease queued work with expiration/retry bounds. Source export validation checks owner-bound event identities, quoted attempts, outcomes, source/asset linkage and bounded images. Images are copied into independent content-addressed mistake storage, read back and hash-verified before references are committed; staged references survive failed writes. A current export fingerprint and clear/lease barriers are checked before commit.

Corrections are per attempt: a later correct answer does not erase another incorrect attempt; explicit retraction targets the matching attempt. Permanent receipts prevent deleted mistakes from being recreated by replay. Teacher review, rejection and edits protect snapshots from automatic replacement. Rejection remains local and does not invoke upload-return workflows. Model analysis is hash/version/model conditional and never overwrites the teacher's analysis or removes teacher-edit protection.

List `reviewStatus=4` is a query-only learner-visible filter: confirmed mistakes plus complete, active automatic conversation mistakes. It is never stored as a review status. Pending-review upload queues exclude conversation-only records. Clearing the chat does not delete independently retained mistake snapshots/images.

The generic product presigning endpoint accepts a conversation original or crop only when the authenticated product caller matches its canonical caller-hashed path and a live caller-owned conversation item references that exact object. Noncanonical aliases, foreign objects, staged-only references, deleted references and unreferenced objects cannot be signed. Display sizes (`medium`, `small`, `thumbnail`) are signed only after the canonical parent passes this same check; a variant path is never accepted as a parent, so thumbnails do not create an alternate signing path. `GET /api/mistakes/{id}/images/{index}?size=` returns the same bytes through the caller-scoped item. Conversation records do not enter the legacy source/return JWT-managed-data predicate.

## Focused verification

Set `MISTAKE_READONLY_TEST_CONNECTION` (collection suite) and `MISTAKE_MIGRATION_TEST_CONNECTION` (schema/upgrade suite) to an isolated PostgreSQL admin connection with CREATE/DROP DATABASE rights. Tests create disposable databases and temporary local image storage; never point this at business data. The original collection suite uses opaque student IDs and covers replay/deletion, correction order, revocation, quote validation, teacher protection, image copying, clear/fingerprint barriers and scoped admission/list visibility.

```bash
dotnet test src/Tests/Emenda.Service.Tests/Emenda.Service.Tests.csproj --configuration Release --filter 'FullyQualifiedName~Conversation|FullyQualifiedName~MistakeHttpContract|FullyQualifiedName~ExtractionSchemaPostgresTests'
```

This command is a verification recipe, not a statement that tests or deployment have run. Retain default-off configuration until Portal/Tutor callback/export integration has passed real HTTP acceptance.

### Local migration evidence

The paired migration was checked with PostgreSQL 15.19 and separate Emenda/User Portal processes. The loopback-only HTTP acceptance completed 31 assertions covering admission recovery, real worker materialization, replay/conflict, caller/student isolation, learner visibility, conditional model analysis, independent teacher analysis, history clearing and deleted-item replay protection. Student and Tutor responses were synthetic; no real model or student records were used.

Focused provider/database, Portal, shared-client, legacy-upload, teacher/assistant permission and image-cropping regressions passed after the reported failures were corrected and their affected tests rerun. The worker regression was observed failing on the raw JSON SQL literal before the parameterized fix. Frontend verification passed 81 behavioral tests and all three Portal production builds; contract verification passed 22 mutation tests. Browser checks exercised authenticated blob images and retained question attachments after a failed follow-up.

Historical product v1–v4 bytes and the pre-existing local administrator changes were unchanged; the consumer's v4/v5 artifacts, snapshot and executable validator matched the product source exactly. These results are local implementation evidence, not a production deployment, a container-image build, real S3 acceptance, real-model quality acceptance or authorization to upgrade a deployed Emenda version.
