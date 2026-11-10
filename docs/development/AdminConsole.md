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

Two sources merge by caller name, the dynamic entry winning:

- **Static configuration** — `Emenda:Security:Callers` (appsettings/env, what
  deployments bootstrap with). The console lists these as 配置-sourced and
  never displays their key: they belong to the deployment configuration.
- **Dynamic file** — the JSON file at `Emenda:Security:DynamicCallers:Path`,
  created and updated by the console (atomic writes, version 1 document):

```json
{
  "version": 1,
  "callers": [
    {
      "name": "study-platform",
      "apiKey": "43-char-random-credential",
      "enabled": true,
      "note": "Ruoyu.Study platform backend",
      "createdAtUtc": "2026-11-11T00:00:00.0000000+00:00",
      "createdBy": "admin",
      "updatedAtUtc": "2026-11-11T00:00:00.0000000+00:00"
    }
  ]
}
```

The file can also be edited directly (it is the same document the console
writes) and is designed to live on a volume mount (`-v emenda-data:/app/data`,
the default of `start.sh`), so credentials survive container replacement.
An unreadable file fails closed: its entries stop matching until it is fixed,
and static callers keep serving.

Console operations (all CSRF-protected; the fixed session cookie never leaves
the browser's cookie jar):

| Action | Endpoint | Effect |
|---|---|---|
| List (masked keys) | `GET /admin/api/callers` | name, `abcd****wxyz`, source, state, note |
| Create | `POST /admin/api/callers` | generates a 43-char key, shown **once** |
| Reveal | `GET /admin/api/callers/{name}/secret` | full key of a dynamic entry |
| Rotate | `POST /admin/api/callers/{name}/rotate` | new key; the old key stops working immediately (rotating a static entry creates the dynamic override) |
| Enable/disable | `PUT /admin/api/callers/{name}` | disabled entries never match |
| Delete | `DELETE /admin/api/callers/{name}` | the key stops working immediately |

Every change is effective immediately on the next request — the middleware
resolves the merged caller set per request, no restart needed.

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

`AdminConsoleEndpointTests` and `DynamicCallerStoreTests` cover: the
unconfigured state (page anonymous, protocol 503, no management API, product
API contract intact); a seeded session ticket driving the full management
round-trip through the real CSRF boundary; immediate activation/revocation of
keys on `/api/*`; and the store's file persistence, validation and fail-closed
behaviour. The hosted-login flow itself is owned by the official package and
verified against a live SignaCore at deployment time.
