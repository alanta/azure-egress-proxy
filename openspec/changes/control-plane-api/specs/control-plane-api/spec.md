## ADDED Requirements

### Requirement: Ruleset API endpoints

The control plane SHALL expose an HTTP API over rulesets: `GET /rulesets` (list),
`GET /rulesets/{name}` (read one), `PUT /rulesets/{name}` (onboard if absent, else replace
content), `POST /rulesets/{name}:check` (dry-run validate), and `DELETE /rulesets/{name}`
(offboard). A `PUT` to an existing ruleset SHALL be a full replace of its `content`.

#### Scenario: Update replaces ruleset content

- **WHEN** an authorized caller sends `PUT /rulesets/{name}` for an existing ruleset with
  `allowed_hosts` and `action`
- **THEN** the ruleset's content is replaced with the pushed values (subject to policy) and
  the blob store is updated

#### Scenario: Full replace removes absent hosts

- **WHEN** a `PUT` omits a host that is currently present in the ruleset
- **THEN** that host is removed from the ruleset (desired-state semantics), and the API
  emits an audit event recording the removed host and the pushing identity

#### Scenario: Read returns current ruleset

- **WHEN** any authenticated caller sends `GET /rulesets/{name}`
- **THEN** the API returns the ruleset's current content and subjects, regardless of the
  caller's RBAC grants

#### Scenario: Offboard removes the ruleset fail-closed

- **WHEN** an authorized caller sends `DELETE /rulesets/{name}`
- **THEN** the ruleset entry is removed and its subjects are freed, falling to the
  `fallback`/deny block on the next reload

### Requirement: Dry-run validation

The `POST /rulesets/{name}:check` endpoint SHALL run the same schema validation and
forced-report coercion as `PUT`, return the effective result and an `{ added, removed }`
diff against current content, and make NO change to the allowlist blob.

#### Scenario: Check reports coerced result without writing

- **WHEN** a caller sends `POST /rulesets/{name}:check` with a proposed content that adds a
  new host
- **THEN** the API returns the validated, coerced result (new host in `report`) and makes
  no change to the allowlist blob

#### Scenario: Check reports the removal diff

- **WHEN** a caller sends `POST /rulesets/{name}:check` with content that omits a currently
  present host
- **THEN** the response lists that host under `removed`, so a pipeline can gate on
  unexpected removals before pushing

### Requirement: Authentication via JWT/JWKS

The control plane SHALL authenticate callers by validating the caller's managed-identity
token using the same RS256/JWKS validation (`iss`/`aud`/`exp`) the proxy uses for workload
tokens. Requests without a valid token SHALL be rejected.

#### Scenario: Invalid token is rejected

- **WHEN** a request presents a missing, expired, or otherwise invalid token
- **THEN** the API SHALL respond with `401 Unauthorized`

#### Scenario: Valid token is accepted

- **WHEN** a request presents a valid managed-identity token
- **THEN** the API SHALL resolve the caller identity and proceed to authorization

### Requirement: Reads are open, writes are authorized by platform-managed RBAC

The control plane SHALL allow any authenticated caller to read rulesets (`GET /rulesets`,
`GET /rulesets/{name}`) regardless of RBAC — the full egress posture is transparent. Writes
SHALL be authorized against platform-managed RBAC grants for the caller's identity:
`onboard` to create a ruleset, `update` to replace an existing ruleset's content, and
`offboard` to remove one. The control plane SHALL enforce these grants without consulting
Azure (ARM/Graph) to verify subject ownership. A caller lacking the required verb SHALL be
denied even with a valid token.

#### Scenario: Any authenticated caller may list all rulesets

- **WHEN** an authenticated caller sends `GET /rulesets`
- **THEN** the API returns every ruleset, not only those the caller may write

#### Scenario: Caller with the verb may write

- **WHEN** an authenticated caller holding `update` (or ownership) sends `PUT` to an existing
  ruleset
- **THEN** the write is authorized

#### Scenario: Caller lacking the verb is denied

- **WHEN** an authenticated caller without the required verb attempts the operation
- **THEN** the API SHALL respond with `403 Forbidden`

### Requirement: Self-provisioning via onboard with trust-on-first-use

A caller holding the `onboard` verb SHALL be able to create a new ruleset by `PUT` to an
absent name, supplying its `subjects`. On success the control plane SHALL record the creating
identity as the ruleset's owner, granting it `update` and `offboard` on that ruleset
(trust-on-first-use). `subjects` SHALL be settable only at onboard; a subsequent `update`
SHALL NOT change `subjects` or `acl`.

#### Scenario: Onboard creates a ruleset and assigns ownership

- **WHEN** a caller with `onboard` sends `PUT /rulesets/{new-name}` with `subjects` and
  content
- **THEN** the ruleset is created, the caller is recorded as owner (gaining `update`/
  `offboard`), and all hosts are applied in `report` (forced-report on new hosts)

#### Scenario: Onboard without the verb is denied

- **WHEN** a caller lacking `onboard` sends `PUT` to an absent ruleset name
- **THEN** the API SHALL respond with `403 Forbidden` and create nothing

#### Scenario: Update cannot change subjects

- **WHEN** a caller sends `PUT` to an existing ruleset with a `subjects` field
- **THEN** the API SHALL reject the change to `subjects` (content-only update)

### Requirement: Blob is the control plane's private store

The blob SHALL be written only by the control plane's own identity; no pipeline or human
caller SHALL hold a blob role. The proxy SHALL read the blob read-only. The control plane
SHALL persist changes by reading the blob, splicing in the single ruleset being written, and
writing it back, without a separate database.

#### Scenario: Only the control plane writes the blob

- **WHEN** a pipeline needs to change egress rules
- **THEN** it does so only through the control-plane API, having no direct blob access

#### Scenario: Write splices one ruleset into the store

- **WHEN** a write updates ruleset A
- **THEN** the API reads the current blob, replaces only ruleset A, and writes the blob back,
  leaving other rulesets untouched

#### Scenario: Proxy reads the store read-only

- **WHEN** the proxy polls for configuration
- **THEN** it reads the same blob directly with a read-only role and reloads on ETag change,
  independent of control-plane availability

### Requirement: Optimistic concurrency with bounded retry

The control plane SHALL use ETag `If-Match` optimistic concurrency on the blob write, and
SHALL wrap the entire read-modify-write (read fresh ETag, re-splice, write `If-Match`) in a
bounded resilience retry that retries on precondition failure (412) with exponential backoff
and jitter. When the retry budget is exhausted, the API SHALL return `409 Conflict`.

#### Scenario: Concurrent writes to different rulesets both succeed

- **WHEN** two callers push different rulesets concurrently and one write encounters a 412
- **THEN** the losing write is retried against the fresh blob and ultimately succeeds
  without caller intervention

#### Scenario: Sustained same-ruleset contention surfaces 409

- **WHEN** the read-modify-write cannot complete within the retry budget due to repeated
  precondition failures
- **THEN** the API SHALL respond with `409 Conflict` so the caller can retry

#### Scenario: Push is idempotent under retry

- **WHEN** the read-modify-write delegate is re-executed during retry
- **THEN** the outcome is the same as a single successful execution, because `PUT` is a full
  replace of the ruleset content
