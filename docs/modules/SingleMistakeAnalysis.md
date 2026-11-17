# Structured single-mistake analysis

The current API contract is [mistake-http-v4](../Integration/mistake-http-v4.json).
Mathematics (`subject=2`) is the first supported subject. Questions arrive as
preprocessed images, with an optional existing source region and no required
`QuestionId`. The report contains a summary, observable errors and their evidence,
possible causes and uncertainty, knowledge-point names, and material limitations.
Knowledge-point IDs are null until catalog integration is available.

## Teacher workflow

1. Read `GET /api/mistakes/{id}/analysis`. An item without analysis returns a report
   container with `revision=0` and null draft/confirmed reports.
2. Submit `POST /api/mistakes/{id}/reanalyze` with `structuredAnalysis=true`, a new
   UUID `requestId`, the displayed `expectedRevision`, and `reviewerId` for a
   trusted backend caller. `teacherDescription` may be empty for first analysis.
3. Poll the existing `POST /api/mistakes/reanalyze/jobs` endpoint with the actor's
   `reviewerId` and returned `jobIds`. The submission response remains HTTP 200
   with `data.success=true` and `data.jobId`. Completion is a separate job state.
4. Read the analysis again. Display the four report sections and a feedback text
   box. Subsequent submissions carry a fresh request ID, the latest revision and
   the teacher's natural-language feedback.
5. Confirm using `POST /api/mistakes/{id}/analysis/confirm`, with `expectedRevision`
   and `reviewerId`. This copies the draft to the confirmed report, updates the
   legacy `rootCause` summary and confirms a pending mistake atomically.

Example reanalysis body follows.

```json
{
  "structuredAnalysis": true,
  "requestId": "b34aa93f-f5c4-4a2a-a3ad-bfbfcfe7448e",
  "expectedRevision": 2,
  "reviewerId": "teacher-example",
  "teacherDescription": "第二行是加号，请依据这一识别修正重新分析。"
}
```

Authenticated staff can use their token subject as the reviewer. Product callers
authenticate with `X-Emenda-Api-Key`, supply the teacher identifier, and remain
responsible for student authorization. Anonymous requests and learner identities
cannot access these teacher reports. The student is taken from the stored mistake.

All three analysis operations authenticate the existing shared-audience and admin-AppId JWT schemes even when strict API mode is disabled and an item is not source-intake managed. Existing authenticated ProductCaller identity takes precedence; an anonymous identity returned by a scheme cannot authorize access or replace that caller. Extraction data additionally requires its owning caller or an authenticated administrator.

## Feedback and concurrent work

Feedback is kept in submission order, including comments associated with failed
jobs. A later explicit correction supersedes an earlier statement about the same
evidence. Every analysis receives the previous report and all accumulated feedback.
The model evaluates hypotheses against the images and must state uncertainty.

Repeat an identical request with its original request ID and expected revision to
recover its job ID. A changed payload under that ID returns `409 request_conflict`.
Only one pending/running analysis per mistake is accepted. Refresh after
`409 analysis_changed` or `409 analysis_in_progress` before starting a new request.
Model failures keep the last draft and confirmed report. Retrying a failed
analysis uses a new request ID and current revision; feedback already submitted
remains in the context.

`draftJobId` and `confirmedJobId` identify the feedback entries represented by each
report. Later feedback can exist while its job is pending or failed. A teacher may
confirm the retained successful draft after a failed attempt. The UI should show
the report's generating job and subsequent feedback clearly.

`inputChanged` indicates that the draft's material references, subject, grade,
student or question association no longer match the mistake. Reanalysis is needed
before confirmation. Material object keys must continue to reference the original
bytes; in-place replacement of an object under the same key is outside this check.
Identical repeated confirmation is idempotent. Old text-only reanalysis or summary
edits on a mistake with structured analysis return `409 structured_analysis_required`.
Rejection remains available and preserves the analysis summary.

For extraction-generated mistakes, the three analysis operations also enforce the extraction task's caller ownership. A caller cannot inspect another caller's or an administrator's reports by supplying a known mistake ID. Administrator access uses the existing authenticated administrator role, not a caller name supplied in the request.

Extraction content correction or group retraction locks the existing mistake rows and invalidates their analysis revision in the same transaction that publishes the corrected extraction. This includes text-only corrections that reuse the same crop bytes and UUIDs. Older worker completions and confirmations are rejected; retained reports and ordered teacher feedback remain readable as prior evidence, and a fresh analysis is required. Changing only the printed question number preserves the existing analysis and review, matching the extraction metadata-only rule.

## Persistence and ownership

One `mistake_analysis` row per mistake stores the latest draft, last confirmed
report and ordered feedback, independently of the 30-day job cleanup. Request
receipts survive that cleanup within the feedback list; the original job may no
longer be available through polling, so read the durable report instead. Earlier
AI reports are not retained. Deleting a mistake follows existing source guards
and deletes its analysis through the mistake foreign key.

Quaestura owns correct answers and reusable solutions. This feature performs no
catalog lookup and exposes `referenceAnswerAvailable=false`. Local mathematical
reasoning can appear in error explanations; there is no separately maintained
correct-answer field. The current model and prompt version describe the draft.

The existing `VlAnalyzer` provider configuration and reanalysis worker switches
apply. The product migration appends schema version 17 after extraction version 16,
adding one table and nullable `analysis_input` on `reanalyze_job`. Existing jobs use
the text-only path when that column is null. Downgrade is refused while analysis
rows exist, to preserve teacher work.

The repository provides backend endpoints. Downstream teacher applications must
explicitly integrate the report view, feedback box, progress display and confirmation
action; this backend contribution does not deploy or implement those pages.

## Integration verification, 2026-11-17

- Integrated the structured-analysis contribution with the current automatic-extraction mainline. Schema version 17 follows version 16; migration/model checks preserve extraction manifests, receipts and prior item rows, and repeated target startup remains a no-op.
- Release solution build passed with zero warnings/errors. The 97 domain tests and 513 independent service tests passed.
- On an owned PostgreSQL 15 instance, the integration selection passed all 129 non-authorization cases. The 14 new structured-analysis authorization cases passed in a focused rerun after correcting their fixture to use the report's actual revision, not a hard-coded pre-generation revision. No cases were skipped. Coverage includes extraction correction/report invalidation, metadata-only labels, owner isolation, both JWT audiences in strict and compatibility modes, startup/read-only behavior and version-15/16 upgrades.
- Dockerfile now copies the embedded `mistake-http-v4.json` resource that the prior container build lacked. A local image build was not run because the local Docker daemon was unavailable; the PR container build remains the release integration check.
- The tests use controlled model/storage adapters and isolated databases. No live provider accuracy, production database migration, downstream teacher page or deployment acceptance is claimed.
