# Security Policy

## Supported versions

Emenda is in its extraction/early-product phase. Only the `main` branch is supported.

## Reporting a vulnerability

Please do **not** open a public issue for a security problem. Report it privately through
GitHub's private vulnerability reporting ("Security" tab -> "Report a vulnerability"), or
contact the maintainer through the repository owner's GitHub profile.

Include the affected version/commit, a description, reproduction steps and the impact. You
will receive an acknowledgement as soon as possible and a coordinated disclosure timeline
after the report is triaged.

## Deployment expectations

Emenda stores learner mistake data, including images. Treat every deployment as sensitive:

- Never expose the API without caller authentication. Standalone mode requires a
  per-caller credential under `Emenda:Security:Callers`; anonymous access is a local
  development-only escape hatch.
- Keep the object-storage bucket private. Pre-signed URLs are bearer capabilities and
  expire.
- Do not commit deployment secrets (database connection strings, caller credentials,
  object-storage keys, model provider keys) to the repository; inject them through the
  environment or a secret manager.