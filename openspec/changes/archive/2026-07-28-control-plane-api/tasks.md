## 1. Ruleset schema evolution

- [x] 1.1 Add `allowlist/rulesets.schema.json` for the control plane's own store (`name`, `subjects[]` of `appid`/`netid`, `content`, `acl`, `owner`); leave `allowlist/allowlist.schema.json` **unchanged** — it stays the proxy's frozen read contract
- [x] 1.2 Update `docs/allowlist.md` for the ruleset model (subjects, acl, writer≠subject, subjects write-once at onboard, one-to-one uniqueness, the onboard default)
- [x] 1.3 Implement the store→allowlist renderer (one module per subject; single-subject id = ruleset name) and prove with a test that rendering `rulesets.json` reproduces today's `allowlist.json` exactly, so the proxy needs no code change
- [x] 1.4 Add `allowlist/rulesets.json` sample (sample-app as a ruleset); `allowlist/allowlist.json` stays as-is

## 2. Platform-managed RBAC

- [x] 2.1 Define the RBAC config source as a platform-owned `grants` section of the single control-plane state blob, holding per-identity grants of the verbs `onboard`/`update`/`bind`/`offboard`
- [x] 2.2 Implement read-only RBAC loading in the API, and guarantee in code that the write path splices only the target ruleset and never `grants`
- [x] 2.3 Record trust-on-first-use ownership when an `onboard`-holder creates a ruleset (owner gains `update`/`bind`/`offboard`)
- [x] 2.4 Enforce the one-to-one uniqueness invariant on onboard (reject a subject already owned by another ruleset)
- [x] 2.5 Add sample grants to `allowlist/rulesets.json` for the demo

## 3. Control-plane API scaffold

- [x] 3.1 Create the control-plane API as a new standalone ASP.NET Core project under `src/` (its own process, not folded into any existing service)
- [x] 3.2 Wire JWT/JWKS authentication reusing the proxy's validation approach (`iss`/`aud`/`exp`, RS256)
- [x] 3.3 Add authorization: resolve caller identity, check the required RBAC verb (`onboard`/`update`/`bind`/`offboard`) or TOFU ownership; enforce writer≠subject; reads open to any valid token
- [x] 3.4 Add `Microsoft.Extensions.Resilience` (Polly v8) and register the blob RMW retry pipeline (retry on 412, bounded, expo backoff + jitter)

## 4. Private blob store write path

- [x] 4.1 Implement blob read (fetch `rulesets.json` + ETag) and blob write (`If-Match`) via the control plane's own managed identity (sole writer); publish the rendered `allowlist.json` after a successful store write
- [x] 4.2 Implement the read-modify-write delegate: read fresh ETag → splice one ruleset → write `If-Match`, wrapped in the resilience pipeline
- [x] 4.3 On exhausted retry budget, return `409 Conflict`
- [x] 4.4 Implement validation, the `{ added, removed }` host diff against current content, and `report` as the onboarding default (a ruleset created without an explicit action starts in `report`; an explicit action is always honoured and the system never downgrades a ruleset's action)
- [x] 4.5 A plain `update` writes content only and rejects an `acl` change; a `subjects` change is gated by the `bind` verb (owner holds it via TOFU), validated and uniqueness-checked like an onboard; a restated (unchanged) `subjects` list needs neither
- [x] 4.6 Full-replace semantics: emit an audit event for each host removed by a push, and for each subject bound/unbound by a membership change (with the pushing identity)

## 5. Endpoints

- [x] 5.1 `GET /rulesets` — list all rulesets for any authenticated caller (reads are open; transparency)
- [x] 5.2 `GET /rulesets/{name}` — read one ruleset (content + subjects)
- [x] 5.3 `PUT /rulesets/{name}` — onboard if absent (requires `onboard`, sets `subjects`, TOFU ownership) else full-replace content (requires `update`/ownership); a `subjects` change additionally requires `bind`; `401`/`403`/`409` as specified
- [x] 5.4 `POST /rulesets/{name}:check` — dry-run validation + coercion, returns `{ added, removed }` diff, no write
- [x] 5.5 `DELETE /rulesets/{name}` — offboard: remove the ruleset, free its subjects, fail-closed to fallback

## 6. Local Aspire setup

- [x] 6.1 Add the control-plane API as a new project resource in `src/AppHost/AppHost.cs`, wired to Azurite (blob) and mock-idp (`JWKS_URL`) like the proxy
- [x] 6.2 Seed `rulesets.json` (rulesets + grants) into Azurite alongside the allowlist (extend `AllowlistSeeder` or add a companion seeder)
- [x] 6.3 Make the API the sole writer of the `egress-config/allowlist.json` blob the proxy polls read-only, and validate JWTs against the mock-idp JWKS
- [x] 6.4 Verify locally: `curl` onboard/update with a mock-idp token → blob updates → proxy enforcement changes within one poll interval

## 7. Infrastructure

- [x] 7.1 Bicep: Container App (or equivalent) for the API + its user-assigned managed identity
- [x] 7.2 Grant the API identity `Storage Blob Data Contributor` (sole writer) on the allowlist and rulesets blobs
- [x] 7.3 Switch the proxy's identity to `Storage Blob Data Reader`; ensure no pipeline identity holds any blob role
- [x] 7.4 Confirm blob versioning + soft delete remain enabled for rollback

## 8. Tests and end-to-end verification

- [x] 8.1 Unit tests: `report` defaulting at onboard, explicit actions honoured, no downgrade on update, content-only update enforcement, membership change gated by `bind` (owner allowed, update-only refused), bind uniqueness, writer≠subject rejection, onboard uniqueness, TOFU ownership
- [x] 8.2 Concurrency test: two concurrent writes to different rulesets both succeed; sustained same-ruleset contention returns `409`
- [x] 8.3 API tests: authN (`401`), authZ per verb (`403`), onboard→update→offboard happy path, `:check` makes no write
- [x] 8.4 End-to-end (deployed): onboard + update a ruleset via `curl` + service principal/MI, observe the blob update, and verify the proxy's enforcement changes after reload

## 9. Documentation

- [x] 9.1 Add `docs/control-plane.md`: the ruleset model, the four operating modes, platform-managed RBAC (`onboard`/`update`/`bind`/`offboard` + TOFU ownership), the private blob store + access model, and the API surface with `curl` examples (onboard → update → offboard, `:check`)
- [x] 9.2 Document the two topologies side by side: **without** the control plane (GitOps / Mode 1 — direct-write allowlist, PR-published, no API) and **with** it (Mode 2 — pipeline push through the API, blob private to the control plane), including how to choose and how to switch
- [x] 9.3 Setup instructions for each topology: GitOps setup (as today) and control-plane setup (deploy the API + its identity, seed the state blob incl. grants, blob role assignments — API sole writer, proxy reader, no pipeline role)
- [x] 9.4 Local dev: document running the control plane in Aspire and exercising the onboard/update/offboard loop against Azurite + mock-idp
- [x] 9.5 Update `README.md` (quickstarts) and `docs/allowlist.md` (write-path section) to reference the two topologies and link `docs/control-plane.md`; note the change on `ROADMAP.md`
