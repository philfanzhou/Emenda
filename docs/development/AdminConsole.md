# Admin Console

Emenda standalone deployments ship a built-in admin console at `GET /admin`: a
server-rendered page whose only sign-in is the **SignaCore hosted login**
(authorization-code + PKCE through the official `SignaCore.Client.AspNetCore`
package, the same integration pattern as Lexarbor and Doctheca). After signing
in with an account that carries the configured role (default `admin`), the
console displays and manages the caller credentials
(`X-Emenda-Api-Key`) that calling systems use against `/api/*`.

The console exists only in standalone mode; legacy Local/Platform deployments
keep the platform's own administration surface unchanged.

## Configuration

All keys live under `Emenda:Admin` (environment form `Emenda__Admin__*`,
wired through `start.sh`):

| Key | Required | Purpose |
|---|---|---|
| `Emenda:Admin:Oidc:Authority` | yes | SignaCore base URI, e.g. `http://192.168.55.10:5002` |
| `Emenda:Admin:Oidc:ClientId` | yes | The Confidential application's AppId registered in SignaCore |
| `Emenda:Admin:Oidc:ClientSecret` | yes | The application's client secret |
| `Emenda:Admin:Oidc:RedirectUri` | yes | Exact callback, `<origin>/admin/auth/oidc/callback` |
| `Emenda:Admin:Oidc:PostLogoutRedirectUri` | yes | `<origin>/admin/auth/oidc/logout/return` (same origin as the redirect) |
| `Emenda:Admin:RequiredRole` | no | Role claim required in the access token (default `admin`) |
| `Emenda:Security:DynamicCallers:Path` | no | Dynamic caller file path (default `./data/emenda-callers.json`; `start.sh` pins `/app/data/emenda-callers.json`) |

Missing Oidc keys never block startup: the public API keeps running, `GET /admin`
shows the not-configured card and every `/admin/auth/oidc/*` endpoint answers a
fixed 503.

### Registering SignaCore

Register in this order (see the [SignaCore hosted-login guide]):

1. Create a **Confidential** application (`POST /api/admin/apps`); keep the
   one-time secret.
2. Set its access-token audience mode to **PerApplication**
   (`PUT /api/admin/apps/{appId}/audience-mode`), so its tokens carry
   `aud = appId` and tokens of other applications are rejected.
3. Register the redirect URIs: `Redirect` →
   `http://<emenda-origin>/admin/auth/oidc/callback`, `PostLogout` →
   `http://<emenda-origin>/admin/auth/oidc/logout/return` (exact match, no
   query string).
4. Set the OIDC policy (`PUT /api/admin/apps/{appId}/oidc-policy`):
   `allowAuthorizationCode=true`, `allowedScopes=["openid","profile"]`,
   `allowRefreshToken=false`.
5. Decide who administers Emenda: the bootstrap administrator
   (`Admin:Username`) always receives `role=admin`; other accounts need the
   role through the application's claims callback.

[SignaCore hosted-login guide]: https://github.com/philfanzhou/SignaCore/blob/main/docs/integrations/HostedLogin.md

## Caller credentials

Caller names are case-insensitive. When a dynamic store is configured, its
records override same-name static configuration, including disabled records
and deletion tombstones. Without a configured dynamic path, authentication
continues to use static configuration only.

- **Static configuration** — `Emenda:Security:Callers` (appsettings/env).
  The console lists untouched entries as configuration-sourced and does not
  reveal their keys. Disable/delete of an untouched configuration entry
  returns **409**: change deployment configuration, or rotate it into a
  dynamic credential first. Rotation immediately replaces the static key.
- **Dynamic file** — `Emenda:Security:DynamicCallers:Path`. Existing version 1
  documents are readable without migration. Every successful management
  write persists a version 2 document atomically before publishing the new
  in-memory state:

```json
{
  "version": 2,
  "callers": [
    {
      "name": "study-platform",
      "apiKey": "generated-caller-key",
      "enabled": true,
      "deleted": false,
      "note": "Study platform backend",
      "createdAtUtc": "2026-11-11T00:00:00.0000000+00:00",
      "createdBy": "admin",
      "updatedAtUtc": "2026-11-11T00:00:00.0000000+00:00"
    },
    {
      "name": "retired-platform",
      "apiKey": "",
      "enabled": false,
      "deleted": true
    }
  ]
}
```

Deletion removes the credential from the console's effective caller list and
clears its stored secret, but **keeps a tombstone by name**. This prevents a
same-name deployment key from becoming valid again after deletion or restart.
Tombstones cannot be revealed, rotated or enabled (404); an explicit Create
with that name generates a new credential. Never remove tombstones manually
while a same-name configuration caller can still exist.

### First bootstrap and recovery

A configured dynamic path must already contain a valid document. Missing,
unreadable, malformed or unsupported-version files, invalid entries, duplicate
names and invalid tombstones block **all caller authentication**, including
static callers; they never silently fall back to deployment keys. Presented
credentials receive **503** on the product API, and authenticated management
requests also receive **503**. Invalid files are logged and are not replaced
by management operations. Health and the admin login surface remain available.

For a confirmed **new installation only**, provision an empty file on the
persistent volume before the first request:

```sh
mkdir -p ./data
(umask 077; set -C; printf '%s\n' '{"version":1,"callers":[]}' > ./data/emenda-callers.json)
```

The no-clobber command fails rather than overwriting existing state. Use the
actual configured/mounted path and grant the service account read/write
access. This file contains plaintext caller keys: restrict volume access and
protect backups. Use a persistent volume (for example `-v emenda-data:/app/data`)
so container replacement preserves both credentials and revocations.

**Do not bootstrap to recover a missing file or lost volume.** Restore the
latest trusted credential state, including all tombstones, then restart.
Losing the entire volume still fails closed because the missing file is never
automatically initialized. However, explicitly replacing it with an empty
document, restoring an older snapshot, removing the dynamic-path setting or
rolling back the binary can remove revocation history. There is no external
revocation authority to detect that rollback. If the state cannot be recovered,
remove/rotate the deployment's old static keys before deliberately initializing
new state; then issue new caller credentials. Do not claim old keys remain
revoked after an operator discards their only revocation record.

Existing version 1 overrides remain authoritative when read by this version:
rotation, disable and deletion preserve suppression of their static
predecessors across restart. Previously deleted overrides whose history was
already lost cannot be reconstructed; rotate/remove those deployment keys
as part of the upgrade.

### Runtime operations

The file is read **once per process**, not watched. Console changes affect the
next request immediately and persist across restart. Direct edits, repair or
restoration of the file require a process restart; a process that failed its
initial load remains unavailable until restart. Stop the service before manual
maintenance to avoid overwriting in-memory state or losing console changes.
Only one service process may write a given file; this is not a distributed
multi-instance credential store.

Console operations (unsafe methods require CSRF; the fixed session cookie
never leaves the browser's cookie jar):

| Action | Endpoint | Effect |
|---|---|---|
| List (masked keys) | `GET /admin/api/callers` | effective, non-deleted callers: name, `abcd****wxyz`, source, state, note |
| Create | `POST /admin/api/callers` | generates a 43-char key, shown **once**; explicitly recreates a deleted name with a new key |
| Reveal | `GET /admin/api/callers/{name}/secret` | full key of a non-deleted dynamic entry; configuration entries return 409 |
| Rotate | `POST /admin/api/callers/{name}/rotate` | existing caller only; new key revokes the previous one; static entries become dynamic overrides; disabled entries stay disabled |
| Enable/disable | `PUT /admin/api/callers/{name}` | dynamic entries only; disabling never reactivates their static predecessor |
| Delete | `DELETE /admin/api/callers/{name}` | dynamic entries only; persists a tombstone so neither the removed key nor its static predecessor matches |

A failed persistence operation returns 503 and does **not** publish the
requested credential change. The last successfully committed state stays
active; do not treat a failed rotate/disable/delete response as revocation.

## Security properties

- The browser only ever holds the opaque session cookie
  (`emendaAdminSession`); access/ID tokens and the client secret stay
  server-side, and administrator passwords are only entered on SignaCore's
  own page.
- A session may only be established when the validated access token carries
  the required role; every other outcome (denial, exception, timeout) fails
  closed.
- Session-authenticated unsafe methods require the antiforgery pair
  (`emendaAdminCsrf` cookie + `X-CSRF-TOKEN` header); a missing pair answers
  a fixed 403, an expired session a fixed 401.
- The console page sends `Cache-Control: no-store`; created keys are shown
  once and never persisted in the browser.

## Testing

`AdminConsoleEndpointTests`, `DynamicCallerStoreTests` and
`ProductCallerAuthenticationTests` cover the unconfigured console, real
session/CSRF boundary with seeded tickets, product API activation/revocation,
version 1 override compatibility, rotation/disable/deletion across fresh store
and host instances, deletion tombstones, failed-write consistency, and explicit
503 responses without static fallback for missing/corrupt state. Missing-file
and complete-volume loss tests also assert that management cannot bootstrap
over lost revocations. Tests pin direct-edit/repair restart requirements.
No real OIDC server is used: seeded session tickets replace hosted login and
the test backchannel rejects network access. The hosted-login protocol remains
owned by the official package and must be verified against SignaCore at
deployment time.
