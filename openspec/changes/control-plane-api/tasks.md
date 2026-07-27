## 1. Ruleset schema evolution

- [ ] 1.1 Evolve `allowlist/allowlist.schema.json`: rename `module` → `ruleset`, replace single `appid` with `subjects[]` (each `appid` or `netid`), add `acl` (`edit`, `push`, `admin`)
- [ ] 1.2 Update `docs/allowlist.md` for the ruleset model (subjects, acl, writer≠subject, subjects write-once at onboard, one-to-one uniqueness, forced-report)
- [ ] 1.3 Confirm the rendered ACL output is unchanged so the proxy needs no code change; add/adjust a render test proving proxy-compatibility
- [ ] 1.4 Update `allowlist/allowlist.json` sample to the ruleset shape (sample-app as a ruleset)

## 2. Platform-managed RBAC

- [ ] 2.1 Define the RBAC config source as a separate file (second platform-owned blob) holding per-identity grants of the verbs `onboard`/`update`/`offboard`
- [ ] 2.2 Implement read-only RBAC loader in the API (managed-identity access, cached)
- [ ] 2.3 Record trust-on-first-use ownership when an `onboard`-holder creates a ruleset (owner gains `update`/`offboard`)
- [ ] 2.4 Enforce the one-to-one uniqueness invariant on onboard (reject a subject already owned by another ruleset)
- [ ] 2.5 Add a sample RBAC document for the demo

## 3. Control-plane API scaffold

- [ ] 3.1 Create the control-plane API as a new standalone ASP.NET Core project under `src/` (its own process, not folded into any existing service)
- [ ] 3.2 Wire JWT/JWKS authentication reusing the proxy's validation approach (`iss`/`aud`/`exp`, RS256)
- [ ] 3.3 Add authorization: resolve caller identity, check the required RBAC verb (`onboard`/`update`/`offboard`) or TOFU ownership; enforce writer≠subject; reads open to any valid token
- [ ] 3.4 Add `Microsoft.Extensions.Resilience` (Polly v8) and register the blob RMW retry pipeline (retry on 412, bounded, expo backoff + jitter)

## 4. Private blob store write path

- [ ] 4.1 Implement blob read (fetch allowlist + ETag) and blob write (`If-Match`) via the control plane's own managed identity (sole writer)
- [ ] 4.2 Implement the read-modify-write delegate: read fresh ETag → splice one ruleset → write `If-Match`, wrapped in the resilience pipeline
- [ ] 4.3 On exhausted retry budget, return `409 Conflict`
- [ ] 4.4 Implement validation + forced-report coercion (diff against current hosts; new hosts forced to `report`)
- [ ] 4.5 Reject any `update` attempting to change `subjects` or `acl` (content-only); accept `subjects` only on onboard
- [ ] 4.6 Full-replace semantics: emit an audit event for each host removed by a push (removed host + pushing identity)

## 5. Endpoints

- [ ] 5.1 `GET /rulesets` — list all rulesets for any authenticated caller (reads are open; transparency)
- [ ] 5.2 `GET /rulesets/{name}` — read one ruleset (content + subjects)
- [ ] 5.3 `PUT /rulesets/{name}` — onboard if absent (requires `onboard`, sets `subjects`, TOFU ownership) else full-replace content (requires `update`/ownership); `401`/`403`/`409` as specified
- [ ] 5.4 `POST /rulesets/{name}:check` — dry-run validation + coercion, returns `{ added, removed }` diff, no write
- [ ] 5.5 `DELETE /rulesets/{name}` — offboard: remove the ruleset, free its subjects, fail-closed to fallback

## 6. Local Aspire setup

- [ ] 6.1 Add the control-plane API as a new project resource in `src/AppHost/AppHost.cs`, wired to Azurite (blob) and mock-idp (`JWKS_URL`) like the proxy
- [ ] 6.2 Seed the RBAC file into Azurite alongside the allowlist (extend `AllowlistSeeder` or add a companion seeder)
- [ ] 6.3 Make the API the sole writer of the `egress-config/allowlist.json` blob the proxy polls read-only, and validate JWTs against the mock-idp JWKS
- [ ] 6.4 Verify locally: `curl` onboard/update with a mock-idp token → blob updates → proxy enforcement changes within one poll interval

## 7. Infrastructure

- [ ] 7.1 Bicep: Container App (or equivalent) for the API + its user-assigned managed identity
- [ ] 7.2 Grant the API identity `Storage Blob Data Contributor` (sole writer) scoped to the allowlist blob; grant it read on the RBAC blob
- [ ] 7.3 Switch the proxy's identity to `Storage Blob Data Reader`; ensure no pipeline identity holds any blob role
- [ ] 7.4 Confirm blob versioning + soft delete remain enabled for rollback

## 8. Tests and end-to-end verification

- [ ] 8.1 Unit tests: forced-report coercion, content-only update enforcement, writer≠subject rejection, onboard uniqueness, TOFU ownership
- [ ] 8.2 Concurrency test: two concurrent writes to different rulesets both succeed; sustained same-ruleset contention returns `409`
- [ ] 8.3 API tests: authN (`401`), authZ per verb (`403`), onboard→update→offboard happy path, `:check` makes no write
- [ ] 8.4 End-to-end (deployed): onboard + update a ruleset via `curl` + service principal/MI, observe the blob update, and verify the proxy's enforcement changes after reload

## 9. Documentation

- [ ] 9.1 Add `docs/control-plane.md`: the ruleset model, the four operating modes, platform-managed RBAC (`onboard`/`update`/`offboard` + TOFU ownership), the private blob store + access model, and the API surface with `curl` examples (onboard → update → offboard, `:check`)
- [ ] 9.2 Document the two topologies side by side: **without** the control plane (GitOps / Mode 1 — direct-write allowlist, PR-published, no API) and **with** it (Mode 2 — pipeline push through the API, blob private to the control plane), including how to choose and how to switch
- [ ] 9.3 Setup instructions for each topology: GitOps setup (as today) and control-plane setup (deploy the API + its identity, seed the RBAC file, blob role assignments — API sole writer, proxy reader, no pipeline role)
- [ ] 9.4 Local dev: document running the control plane in Aspire and exercising the onboard/update/offboard loop against Azurite + mock-idp
- [ ] 9.5 Update `README.md` (quickstarts) and `docs/allowlist.md` (write-path section) to reference the two topologies and link `docs/control-plane.md`; note the change on `ROADMAP.md`
