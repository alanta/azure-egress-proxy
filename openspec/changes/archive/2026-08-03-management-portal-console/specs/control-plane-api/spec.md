## ADDED Requirements

### Requirement: Platform configuration is readable

The control plane SHALL expose `GET /grants` (the platform-managed RBAC grants) and
`GET /fallback` (the platform-owned baseline for sources matching no ruleset) as separate
read-only endpoints. Both SHALL require authentication only and SHALL consult no RBAC verb,
consistent with reads being open. Neither section SHALL become writable through the API.

#### Scenario: Any authenticated caller reads the grants

- **WHEN** an authenticated caller sends `GET /grants`
- **THEN** the API returns the current grant entries regardless of the caller's own verbs

#### Scenario: Any authenticated caller reads the fallback

- **WHEN** an authenticated caller sends `GET /fallback`
- **THEN** the API returns the current fallback block, and an absent or empty fallback is
  reported as deny-all

#### Scenario: Grants remain unwritable

- **WHEN** any caller attempts to write to `/grants` by any method
- **THEN** the API rejects the request; the `grants` section is edited only out of band by the
  platform team

### Requirement: Reads report state recency

Control-plane read responses SHALL surface the state document's last-modified time and ETag, so
a caller can report when the configuration last changed without reading the blob directly.

#### Scenario: A read carries the state document's modification time

- **WHEN** an authenticated caller reads rulesets, grants, or the fallback
- **THEN** the response carries the last-modified time and ETag of the state document the
  values were read from

#### Scenario: Recency is document-scoped

- **WHEN** any ruleset is written
- **THEN** the reported last-modified time advances for all reads, because it describes the
  state document rather than an individual ruleset
