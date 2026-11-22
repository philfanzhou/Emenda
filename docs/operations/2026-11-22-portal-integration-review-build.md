# Portal integration review and local build — 2026-11-22

Scope: `/tmp/Ruoyu.Study-pr328-integrated-20261122`, compared with Ruoyu.Study master `806589f6291f4dd41a366e9e1dafae736feebea8`. The original dirty repository was read only. No push, merge, deployment, browser operation or business-data write was performed.

## Release builds

Commands below ran from the archive root. All six builds passed. Backend builds reported zero warnings and zero errors. Frontends initially lacked installed dependencies; running `npm ci` in each archived frontend resolved that prerequisite without changing source or lockfiles.

| Project | Command | Result |
| --- | --- | --- |
| User backend | `dotnet build src/user_portal/backend/UserPortal.sln --configuration Release` | Passed |
| Teacher backend | `dotnet build src/teacher_portal/backend/TeacherPortal.sln --configuration Release` | Passed |
| Assistant backend | `dotnet build src/assistant_portal/backend/AssistantPortal.sln --configuration Release` | Passed |
| User frontend | `cd src/user_portal/frontend && npm ci && npm run build` | Passed |
| Teacher frontend | `cd src/teacher_portal/frontend && npm ci && npm run build` | Passed |
| Assistant frontend | `cd src/assistant_portal/frontend && npm ci && npm run build` | Passed |

Raw build logs: `/tmp/pr328-{user,teacher,assistant}-{backend,frontend}.log`.

## Tests actually executed

For shared projects, command was `dotnet test src/ruoyu.common/test/<project> --configuration Release`.

| Project / filter | Passed | Failed | Notes |
| --- | ---: | ---: | --- |
| Ruoyu.Study.MistakeBff.HttpClients.Tests | 248 | 0 | Re-run in this review |
| Ruoyu.Study.PortalAuthBff.Shared.Tests | 452 | 0 | Full suite |
| Ruoyu.Study.MistakeUploadBff.Shared.Tests | 69 | 0 | Full suite |
| Ruoyu.Study.MistakeReviewBff.Shared.Tests | 19 | 42 | Full suite is not green |
| Same review project, `--filter FullyQualifiedName~Emenda` | 13 | 0 | Includes versioned edits, sibling preservation, student/subject access, stale-version rejection and item binding |
| User backend Tests, same Emenda filter | 17 | 0 | Student ownership, authenticated confirmation, image integrity and practice access |
| Teacher backend Tests, same Emenda filter | 1 | 0 | Actual matching test count |
| Assistant backend Tests, same Emenda filter | 0 | 0 | No matching tests; not a test pass |
| `node --test tests/frontend/emenda-workspace.test.cjs` | 28 | 0 | Real composable/SFC fixture tests; no browser |

Raw logs: `/tmp/pr328-PortalAuthBff-tests.log`, `/tmp/pr328-MistakeUploadBff-tests.log`, `/tmp/pr328-MistakeReviewBff-tests.log`, `/tmp/pr328-http-tests.log`, `/tmp/pr328-review-emenda-tests.log`, `/tmp/pr328-{user,teacher,assistant}-emenda-tests.log`, `/tmp/pr328-workspace-tests.log`.

## Review findings and limits

`EmendaWireValidation` validates unique question IDs, source identity, crop geometry/hash/size, snapshot versions and question outcomes. It does not impose a question-count ceiling. Correct/unknown results permit zero crops; incorrect results require region-aligned crops. Existing analysis tests explicitly cover 40 questions.

The revision endpoints assemble the complete draft from the authoritative job, retain sibling questions and existing question IDs, check student and subject access, enforce expected version and succeeded state, and return the saved question ID. Manual additions receive a generated ID. Confirmation checks the bound item and pending state, passes the inspected version upstream, and rejects a second confirmation once the current item is no longer pending. Upstream persistence and review-state retention across actual revisions were not exercised against a live Emenda instance; fixture success cannot establish those properties in production.

The full shared review suite has legacy route failures returning HTTP 500. Its controller requires `IHttpClientFactory`, while the shared test host registers controllers and memory cache but no `AddHttpClient`; the custom factory also does not register that dependency. A separate wire-disconnect test requires the literal header `Content-Length: 0` on DELETE, which the observed runtime request omitted. The controller, test-host Program, custom factory and shared wire test are byte-for-byte unchanged from the specified master baseline. These findings therefore do not justify weakening validation or changing integration source. Baseline execution itself was not performed, so pre-existing failure status is not claimed as experimentally proven.

No confirmed integration-introduced defect was repaired in this review. No source changes were made. The result is six successful local builds with passing Emenda-specific tests, but an unresolved failing full shared review suite. No deployment or real-business acceptance is implied.

## Executed master baseline comparison

The earlier limitation above is superseded by an actual baseline run. Exact master `806589f6291f4dd41a366e9e1dafae736feebea8` was extracted with `git archive` from the original repository into `/tmp/pr328-baseline-UoTl7s` (an independent directory, not a worktree). Both archives ran `dotnet test src/ruoyu.common/test/Ruoyu.Study.MistakeReviewBff.Shared.Tests --configuration Release` with TRX logging. Neither run changed the original dirty tree or any running environment.

| Archive | Passed | Failed | Total |
| --- | ---: | ---: | ---: |
| Exact master baseline | 16 | 42 | 58 |
| Integrated archive | 19 | 42 | 61 |

TRX results were parsed and matched by fully qualified test identity, including parameterized test names. All 42 failure identities and their complete `ErrorInfo/Message` strings are identical: 42 shared failures, zero baseline-only failures, zero integrated-only failures, zero changed failure messages. The three additional integrated cases of `EmendaReviewBoundaryTests.VersionedEditPreservesSiblingsAndReturnsTheSavedQuestion` (`add=True, initialCount=32`; `add=True, initialCount=2`; `add=False, initialCount=2`) all pass.

The matching error categories are 41 HTTP status assertion failures (25 expected 200, 10 expected 403, 3 expected 400, 2 expected 202, 1 expected 404; all observed 500) and one `Assert.Contains()` missing-substring assertion. The full names, messages and stack traces are retained in the TRX evidence rather than abbreviated in this report.

Evidence: `/tmp/pr328-baseline-results/baseline.trx`, `/tmp/pr328-integrated-results/integrated.trx`; command output: `/tmp/pr328-baseline-test.log`, `/tmp/pr328-integrated-test.log`. Both test commands exit 1 because of the matching failures.

No new code blocker is demonstrated by this suite, so no implementation was changed. The full-suite green gate remains unmet on both master and the integration: baseline parity establishes no regression, not a passing suite or permission to waive that gate. Existing failures require separate ownership if a fully green suite is mandatory for release. Live Emenda persistence and production business acceptance remain unverified as described above.
