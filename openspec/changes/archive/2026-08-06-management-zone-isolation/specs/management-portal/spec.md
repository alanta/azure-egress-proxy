## MODIFIED Requirements

### Requirement: The portal is read-only

The management portal SHALL NOT write policy. It SHALL NOT call `PUT /rulesets/{name}` or
`DELETE /rulesets/{name}`, and it SHALL hold no write role on the allowlist storage account.
Its only non-`GET` call to the control plane SHALL be `POST /rulesets/{name}:check`, which
writes nothing.

Read-only SHALL hold in every direction, not only towards policy. The portal SHALL NOT be able to
reach a credential that permits writing, which covers the audit trail: the workspace shared key
authenticates an append to a custom table, so being able to *read* that key is a write capability
on `EgressProxy_CL`.

#### Scenario: Portal composes a change without applying it

- **WHEN** an operator composes a candidate ruleset change in the portal
- **THEN** the portal validates it via `POST /rulesets/{name}:check`, renders the returned
  `added`/`removed`/`bound`/`unbound` diff, and emits a copyable command that would apply the
  change through the control-plane API
- **AND** neither the control-plane state nor the rendered allowlist is modified

#### Scenario: Portal identity holds no write role

- **WHEN** the portal's managed identity is provisioned
- **THEN** it receives only read roles (`Reader` and `Log Analytics Reader` on the hub resource
  group), and no `Storage Blob Data Contributor` assignment on the allowlist storage

#### Scenario: The portal cannot obtain a credential that writes to the audit trail

- **WHEN** the portal's roles are inspected for access to the Log Analytics workspace shared key
- **THEN** no assigned role grants `Microsoft.OperationalInsights/workspaces/sharedKeys/read`

> **Why the role is `Log Analytics Reader` and not `Monitoring Reader`.** Both are read-only by
> reputation and both are built on `*/read`. But `*/read` matches
> `Microsoft.OperationalInsights/workspaces/sharedKeys/read`, and that key authenticates the legacy
> Data Collector API — which appends rows to custom tables, `EgressProxy_CL` among them. So
> `Monitoring Reader` grants a forge capability on the audit trail. `Log Analytics Reader` excludes
> the key read in its `notActions` (the exclusion is itself the evidence that `*/read` would
> otherwise cover it), and its action set is otherwise a superset, so the console loses nothing.

#### Scenario: The workspace refuses shared-key ingestion regardless

- **WHEN** any principal attempts to write to the workspace using its shared key
- **THEN** the workspace refuses it, because local authentication is disabled — a second,
  independent control, so that the invariant does not rest on one role definition
