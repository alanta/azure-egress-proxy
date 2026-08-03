## ADDED Requirements

### Requirement: The portal is read-only

The management portal SHALL NOT write policy. It SHALL NOT call `PUT /rulesets/{name}` or
`DELETE /rulesets/{name}`, and it SHALL hold no write role on the allowlist storage account.
Its only non-`GET` call to the control plane SHALL be `POST /rulesets/{name}:check`, which
writes nothing.

#### Scenario: Portal composes a change without applying it

- **WHEN** an operator composes a candidate ruleset change in the portal
- **THEN** the portal validates it via `POST /rulesets/{name}:check`, renders the returned
  `added`/`removed`/`bound`/`unbound` diff, and emits a copyable command that would apply the
  change through the control-plane API
- **AND** neither the control-plane state nor the rendered allowlist is modified

#### Scenario: Portal identity holds no write role

- **WHEN** the portal's managed identity is provisioned
- **THEN** it receives only read roles (`Reader` and `Monitoring Reader` on the hub resource
  group), and no `Storage Blob Data Contributor` assignment on the allowlist storage

### Requirement: The portal is the only component that understands human identity

The management portal SHALL authenticate its users and SHALL call the control-plane API using
its own managed identity. The control-plane API SHALL NOT be extended to authenticate or
authorize human users as part of this change.

#### Scenario: Portal calls the control plane as itself

- **WHEN** the portal reads or dry-runs against the control-plane API on behalf of a signed-in
  operator
- **THEN** the request carries the portal's own managed-identity token, and the control-plane
  API applies its existing machine identity model unchanged

### Requirement: Access is limited to the platform team

The portal SHALL serve a single audience tier. It SHALL NOT implement per-user or per-ruleset
scoping of what a signed-in user may see, and SHALL NOT consult the ruleset `acl` block.

#### Scenario: All authorized users see the full posture

- **WHEN** any user authorized to sign in to the portal views the console
- **THEN** they see every ruleset, all traffic data, and all runtime status, without filtering
  by ruleset association

### Requirement: Denials resolve to their governing ruleset

The portal SHALL join proxy decision records to rulesets, using the audit record's `Role`
(the workload `appid` from the validated JWT) against `subjects[].appid`, and SHALL present the
governing ruleset alongside each denial.

#### Scenario: A denial is traced to the rule that caused it

- **WHEN** an operator views a denied decision for a workload identity
- **THEN** the portal shows the ruleset governing that subject, its current `allowed_hosts` and
  `action`, and offers a dry-run of the change that would allow the denied host

#### Scenario: A denial for an unmatched subject

- **WHEN** a denied decision carries a `Role` that belongs to no ruleset
- **THEN** the portal reports that the subject is governed by the `fallback` block rather than
  attributing it to a ruleset

#### Scenario: netid-mode rulesets are joined on a weaker key

- **WHEN** the portal presents traffic for a ruleset whose subjects are `netid` entries
- **THEN** the join is made on `SrcIp` and the view indicates that the correlation is by source
  address rather than by validated identity

### Requirement: Report-mode findings are surfaced for promotion

The portal SHALL surface, per ruleset in `report` mode, the hosts that would have been denied
under `enforce`, derived from the `EnforceWouldDeny` signal in the audit records.

#### Scenario: Operator reviews what a report-mode ruleset needs

- **WHEN** an operator opens a ruleset whose `action` is `report`
- **THEN** the portal lists the off-list hosts the workload actually attempted, so the ruleset
  can be completed before promotion to `enforce`

### Requirement: Runtime status is sourced from Azure

The portal SHALL obtain deployment configuration from ARM and operational metrics from Azure
Monitor, using its own managed identity. It SHALL NOT require the proxy to expose any HTTP
endpoint.

#### Scenario: Runtime panel reflects the deployment

- **WHEN** an operator views the runtime surface
- **THEN** the portal shows VMSS instances online, the egress public IP prefix capacity and how
  much of it is in use, network throughput, and load-balancer health

#### Scenario: Data freshness is stated, not implied

- **WHEN** the portal renders any metric sourced from Azure Monitor or Log Analytics
- **THEN** it displays the recency of that data, and does not present it as live

### Requirement: Configuration recency is shown

The portal SHALL display when the control-plane state was last modified, using the state
document's modification date as returned by the control-plane API.

#### Scenario: Overview shows when policy last changed

- **WHEN** an operator views the overview
- **THEN** the portal shows the state document's last-modified time, scoped to the document as
  a whole

### Requirement: The portal is optional and independently deployed

The portal SHALL be deployed as a separate service, gated on a deployment parameter, and the
platform SHALL remain fully functional when it is absent.

#### Scenario: Deployment without the portal

- **WHEN** the infrastructure is deployed with the portal parameter disabled
- **THEN** no portal resources are created, and the proxy and control plane are unaffected

#### Scenario: Portal outage does not affect enforcement

- **WHEN** the portal is unavailable
- **THEN** the proxy continues enforcing from its polled allowlist and the control-plane API
  continues to accept pipeline writes
