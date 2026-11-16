# AI Tutor paired image-analysis implementation

`analyze-images.patch` contains the complete paired provider implementation for Emenda's `IQuestionAnalysisClient`, including endpoint wiring, environment configuration, contract validation, tests, and API documentation. It is source delivery, not a deployment or a business-data export.

## Exact upstream baseline

- Repository: <https://github.com/kingus188/ai-tutor>
- Baseline commit: `27c615a44049f668d0154d4f3b86eef283a061a2`
- Patch paths are relative to the AI Tutor repository root. The patch contains no local checkout paths, real credentials, model output, or student business data. Image strings and keys in tests are synthetic fixtures.

The patch modifies `.env.example`, `docs/api.md`, `apps/server/src/app.ts`, `apps/server/src/config.ts`, `apps/server/src/index.ts`, and `apps/server/src/http/llm-rate-limit.ts`, and adds `apps/server/src/modules/problem/image-analysis.routes.ts` and `apps/server/test/image-analysis.test.ts`.

## Apply and check locally

Use a separate AI Tutor checkout at the exact baseline. Do not apply over unrelated local edits. Node must satisfy upstream's `>=22.19.0` requirement.

```bash
# Run from the AI Tutor checkout. Set this to your Emenda checkout location.
EMENDA_ROOT=/path/to/Emenda

git rev-parse HEAD
# Expected: 27c615a44049f668d0154d4f3b86eef283a061a2

git apply --check "$EMENDA_ROOT/integrations/ai-tutor/analyze-images.patch"
git apply "$EMENDA_ROOT/integrations/ai-tutor/analyze-images.patch"
npm ci
npm run typecheck
npm run build
cd apps/server
npx tsx --test test/image-analysis.test.ts
```

These are local apply/check commands only: no service start, deployment, commit, or push is included. The coordinating implementation session reported that provider typecheck, build, and the seven original focused image-analysis tests passed before packaging. A subsequent security review found that the new route was absent from the existing rate limiter's model-path allowlist. The patch now includes that missing path and an eighth regression test: with RPM=1, the first valid request returns 200, the second returns 429/RATE_LIMITED, and the model call count stays at one. Only this new focused test was run after that fix; it passed (1 test, 0 failures). No full suite or build was repeated. These tests exercise controlled models, not real educational or image-recognition quality. Emenda client consolidation remains the parent task's responsibility.

## Runtime configuration

Inject provider secrets through runtime configuration, not source control or browser code:

- `EMENDA_ANALYSIS_KEY`: independent service key, 32–256 characters, sent by Emenda in `X-Emenda-Analysis-Key`. Do not reuse login or conversation-collection keys.
- `QUESTION_INTAKE_MODEL`: ID of an existing enabled vision-capable model profile. This reuses AI Tutor's current router, model settings, quotas, call metadata, and rate limits; it introduces no model SDK or alternate-model fallback.

The feature remains unavailable unless both settings are configured and the selected model is currently enabled. Existing private-listener and compatibility-listener source restrictions remain in effect. Browser `Origin` and `Sec-Fetch-Site` requests are rejected. Do not expose the private endpoint directly to browsers or public networks. If using the compatibility listener, retain its explicit source allowlist and existing bind restrictions.

Configure the paired Emenda section `Emenda:Extraction`:

```json
{
  "Emenda": {
    "Extraction": {
      "Enabled": false,
      "TutorBaseUrl": "http://127.0.0.1:8787",
      "TutorApiKey": "",
      "OperationTimeoutSeconds": 300
    }
  }
}
```

Enable only when the operator has configured the paired service and injected the independent key. `TutorBaseUrl` is an operator-controlled HTTP(S) origin, never a caller-supplied URL; it cannot contain userinfo, path, query, or fragment. Restricted LAN HTTP is supported; use TLS over untrusted networks. Register the typed client with its redirect-disabled `CreateHandler()` and an infinite `HttpClient.Timeout`, allowing its own operation timeout to govern cancellation. Neither side logs the service key or analysis content.

## Wire contract and behavior

`POST /api/v1/problems/analyze-images` accepts up to 8 MiB JSON: grade 1–12 and 1–9 unique upright JPEG/PNG data-URL images, each with source UUID, display width/height, and normalized proposed regions. Dimensions are at most 8192 on each axis and 16 million pixels. Boxes must be nonzero, finite, within `[0,1]`, and belong to their enclosing source.

The response is `{model,questions:[draft]}`, at most 2 MiB. Whole questions may span images; at most 32 groups and 32 source-bound regions per group are accepted. Subjects are 1–9 and outcomes are `correct|incorrect|unknown`. Correct/incorrect requires a visible actual student attempt, independently solved reference, and comparison evidence. Missing attempts, missing reliable references, and unclear content remain unknown; no synthetic fallback fills those fields. Malformed or foreign-source output fails rather than being repaired. Emenda assigns stable question IDs after accepting the complete response.

The endpoint forwards cancellation into the existing model call and disables SDK automatic retries. The Emenda client likewise does not retry requests and bounds both request and response bodies. Tests cover cross-image grouping, unknown, missing attempt for confident judgment, foreign sources, malformed provider responses, auth/browser boundaries, disabled configuration, limits, and cancellation. No real student images or production model calls are needed for the focused tests.
