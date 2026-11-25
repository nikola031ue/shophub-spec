# shophub

The ShopHub panel (spec 004): owners register, sign in and manage their shops. An Angular app and
a .NET API in one image; the API is the only writer of the owners' Shop, Wallet and DiscordChannel
resources (constitution Principle V).

Status: accounts API, Kubernetes gateway, the panel's sign-in and registration pages, and shop
records. The reconciler, shop endpoints and shop pages come next.

## Run locally

To click through the panel in a browser, with Docker:

```sh
docker compose up -d --build   # PostgreSQL + this checkout's image
# open http://localhost:8080
docker compose down -v         # stop and delete the local database
```

The local database trusts every connection on the compose network and publishes no port; it is for
trying the pages only. There is no cluster, so shop pages answer 503 here.

## Settings

The app reads only environment variables. Missing or invalid settings stop the start with a
message that names the variable, never its value.

| Variable | Required | Meaning |
|----------|----------|---------|
| `DATABASE_URL` | ✓ | PostgreSQL URI, e.g. `postgresql://app:pw@host:5432/app` (CNPG Secret `shophub-db-app`, key `uri`) |
| `SHOPS_NAMESPACE` | – | Namespace of the owners' Shops, Wallets and DiscordChannels; default `shops` |
| `SHOP_DOMAIN` | – | Shop host suffix, for display only (URLs come from the Shop status); default `shops.localhost` |
| `MAX_SHOPS_PER_OWNER` | – | Default 5 |
| `TRUSTED_PROXY_HOST` | – | DNS name of the entry point's pods (`traefik-proxies.traefik.svc.cluster.local`). See [Client address](#client-address) |
| `REGISTRATION_RATE_LIMIT_PER_HOUR` | – | Registrations per hour per client address; default 10 |

At start the app applies its schema (`Storage/Postgres/Scripts/NNN_*.sql`, each once, under an
advisory lock, so replicas can start together). Until the database answers it retries with
backoff and does not open its port; `/healthz` answers only while the database does.

## Accounts API

| Method | Path | Answer |
|--------|------|--------|
| GET | `/api/session` | `{signedIn, email?}`; (re)issues the `XSRF-TOKEN` cookie |
| POST | `/api/register` | 202, no body, no cookie, **the same whether or not the email is taken**; then sign in |
| POST | `/api/signin` | 204 + session cookie · 401 "Wrong email or password." for a wrong password and an unknown email alike · 429 locked |
| POST | `/api/signout` | 204; ends this session only |
| POST | `/api/password` | 204; ends every other session of the owner · 401 wrong current password (counts towards the lockout) |

State-changing requests need the `X-XSRF-TOKEN` header. No account enumeration: every path that
checks or sets a password does exactly one PBKDF2 run, and an unknown email is verified against a
fixed dummy hash, so neither the answer nor its timing tells a taken email from a free one. Known
limit: without email verification, registering an email and then signing in with that password
shows whether it was taken (both steps are rate-limited or locked out).

## Panel pages (Angular)

`src/ShopHub.Web/src/app/auth/`:

- **Register** (`/register`): the API answers 202 whether or not the email is taken, so the page
  always goes on to sign in with the same email and password. If that sign-in gets 401, the page
  says only "If you already have an account, sign in with its password." It never says that the
  email is taken; the tests check the wording and that a new and a taken email give the same page.
- **Sign in** (`/signin`): one text for a wrong password and an unknown email; a lockout says how
  many minutes to wait. After sign-in it returns to the page that asked for it (same-site paths
  only).
- **Sign out**: in the header; ends this session only.
- **Guards**: owner pages need a session (otherwise sign-in, then back); a signed-in owner skips
  sign-in and registration.
- **XSRF** (`interceptors.ts`): every state-changing `/api` request sends `X-XSRF-TOKEN`. A missing
  token is fetched from `/api/session` first; a stale one (the session ended on the server) is
  replaced once on the API's XSRF 400. Any other 401 means the session ended: the panel goes to
  sign-in.

## Security building blocks

Ported from the shop app (shop 0.2.0) with their tests, and adapted to many accounts:

- **Client address** (`ForwardedHeaders.cs`, `ClientAddress.cs`): `X-Forwarded-For`/`-Proto` count
  only when the TCP peer is one of the addresses `TRUSTED_PROXY_HOST` resolves to (refreshed
  every 30 s), and only their last value. Any other peer keeps its own address. Unset, or a failed
  lookup, trusts nobody.
- **Lockout** (`Auth/Lockout.cs`, `Storage/Postgres/PostgresLockoutStore.cs`): 5 failed sign-ins
  for one account (normalised email) from one address within 15 minutes lock that pair for 15
  minutes. 50 failures for one account across all addresses lock that account everywhere for 15
  minutes. Other accounts are never affected. An attempt while either is locked counts nowhere,
  so one address alone can't reach the account-wide limit. State is in PostgreSQL
  (`signin_lockouts`), shared by every replica.
- **Sessions** (`Auth/Sessions.cs`): the cookie names a server-side session, checked on every API
  request. Sign-out ends that session; a password change ends every other session of the owner.
  The session cookie is `Secure`, `HttpOnly`, `SameSite=Strict`, 8 hours, not sliding.
  State-changing requests need `X-XSRF-TOKEN`.
- **Cookies**: every cookie the app writes is `Secure` (cookie policy), whatever the request scheme.
- **Rate limits** (`Auth/RateLimits.cs`): registration per client address, held per replica.
- **Headers**: every response carries `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`,
  `Content-Security-Policy: frame-ancestors 'none'` and `Referrer-Policy: same-origin`.

## Shop records

`Shops/ShopRecords.cs`, table `shops` (one row per shop, the source of truth the reconciler
applies to the cluster):

- **Validation**:
  - name: trimmed, 1 to 100 characters (Unicode code points, as PostgreSQL and the Shop CRD count
    them), no control characters; unique per owner ignoring letter case and surrounding spaces;
  - availability `standard` or `high`; database `standard` or `light`;
  - wallet address `0x` + 40 hex digits. All-lowercase or all-uppercase is accepted; mixed case
    must pass the EIP-55 checksum (`Shops/Eip55.cs`, same rule as shop-operator). It is stored in
    checksummed form.
- **Ids**: `s` + 9 random base32 characters (`a-z`, `2-7`), drawn again on a collision.
- **Create** runs in one transaction under a row lock on the owner, so any number of concurrent
  requests and replicas behave as if one at a time:
  - the same create again (same name, settings and wallet, shop not being deleted) returns the
    existing shop and inserts nothing, so a double submit or retry makes one shop;
  - the same name with other letter case or other settings is a duplicate name;
  - an owner with 5 shops in any state, including ones being deleted, gets the limit.
- **Owner filter**: every owner-facing query takes the owner id. Another owner's shop and a missing
  one look the same.

## Kubernetes access

`Shops/KubeGateway.cs` is the only code that talks to the API server, as the pod's
ServiceAccount (a kubeconfig outside the cluster):

- It server-side applies a shop's DiscordChannel, current Wallet and Shop with field manager
  `shophub`, and reads the Shop's status.
- Every object is named exactly from the shop record (`<id>`, `<id>-w<generation>`). Deletes go
  one object at a time; nothing lists or selects by label, so a mistake can't reach another shop.
- The only Secret it reads is `<id>-admin`. RBAC grants `get` on Secrets in `shops` (no list or
  watch) and can't narrow that to a name pattern, so the gateway is what enforces it; the tests
  record every request to prove it.
- RBAC (chart `shophub`, see `tests/.../K3s/Manifests/shophub-rbac.yaml`): a Role in `shops` only.
  Shops, Wallets, DiscordChannels: `get list watch create patch delete`; Secrets: `get`.

The k3s tests (`Integration/K3s/`) run the gateway against a real API server with the
shop-operator CRDs from the pinned chart and assert the RBAC with access reviews.

## Run locally

Requirements: .NET SDK (version in `global.json`), Node 24 (or 22), Docker.

```bash
# Build the Angular app into src/ShopHub.Api/wwwroot
(cd src/ShopHub.Web && npm ci && npx ng build)

# A database
docker run -d --name shophub-pg -p 5432:5432 -e POSTGRES_USER=app -e POSTGRES_PASSWORD=dev -e POSTGRES_DB=app postgres:18.7

# Run the API (serves the app on http://localhost:5080)
DATABASE_URL=postgresql://app:dev@localhost:5432/app dotnet run --project src/ShopHub.Api
```

The image works the same way, on port 8080:

```bash
docker build -t shophub:dev .
docker run --rm -p 8080:8080 -e DATABASE_URL=… shophub:dev
```

## Tests

```bash
dotnet test --solution ShopHub.slnx          # unit + Testcontainers PostgreSQL and k3s (needs Docker)
(cd src/ShopHub.Web && npx ng test --watch=false)
```

## Layout

```text
src/ShopHub.Api/     .NET API: Program, settings, forwarded headers, Auth/, Storage/Postgres/
src/ShopHub.Web/     Angular app (built into the API's wwwroot)
tests/ShopHub.Api.Tests/  xUnit v3 on Microsoft.Testing.Platform: Unit/, Integration/
```

## Releases

CI (`.github/workflows/ci.yaml`) runs `lint` and `test` on every PR and push to main. A tag
`vX.Y.Z` on a commit that is on main builds and pushes `ghcr.io/nikola031ue/shophub:X.Y.Z`; an
existing version is never overwritten.
