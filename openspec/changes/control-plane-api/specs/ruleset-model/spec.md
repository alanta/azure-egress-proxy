## ADDED Requirements

### Requirement: Ruleset structure

The system SHALL model each unit of egress policy as a **ruleset** consisting of a unique
`name`, a set of `subjects` (each a workload identity `appid` or a `netid`), `content`
(`allowed_hosts` and an `action` of `enforce`, `report`, or `open`), and an `acl`
(`edit` for humans, `push` for machines, `admin` governing the acl). Rulesets SHALL live in
their own store, and the system SHALL render that store to the **existing, unchanged**
allowlist document the proxy already consumes — one module entry per subject — so the proxy's
read/reload contract and its code are not affected.

#### Scenario: Ruleset governs its subjects

- **WHEN** a request is resolved by the proxy to a subject listed in a ruleset
- **THEN** that ruleset's `content` (`allowed_hosts`, `action`) applies to the request

#### Scenario: Rendered ACL is proxy-compatible

- **WHEN** a set of rulesets is rendered to the allowlist blob
- **THEN** the rendered ACL is in the format the current proxy already consumes, requiring
  no proxy changes

### Requirement: One subject belongs to at most one ruleset

The system SHALL enforce that any given subject (`appid` or `netid`) appears in at most one
ruleset. There SHALL be no composition, union, or precedence across rulesets.

#### Scenario: Duplicate subject is rejected

- **WHEN** the platform registry defines a subject that already belongs to another ruleset
- **THEN** the configuration SHALL be rejected as invalid rather than merged

#### Scenario: Effective policy comes from a single ruleset

- **WHEN** the effective allowlist for a subject is computed
- **THEN** it is taken from exactly one ruleset, never combined from several

### Requirement: Writer is never the subject

The system SHALL ensure that the identity authorized to write a ruleset (`acl.push` or
`acl.edit`) is never the workload identity the ruleset governs (a `subject`). A workload
MUST NOT be able to modify the ruleset that governs its own egress.

#### Scenario: Workload cannot write its own ruleset

- **WHEN** a caller whose identity is a `subject` of a ruleset attempts to write that
  ruleset
- **THEN** the write SHALL be denied

### Requirement: Platform-managed RBAC and subject write-once

The system SHALL manage authority to change rulesets through platform-owned RBAC that grants
identities the verbs `onboard` (create a ruleset), `update` (replace content), and
`offboard` (remove a ruleset). Base grants SHALL be platform-owned configuration loaded
read-only by the control plane; the control plane SHALL enforce them without verifying
subject ownership against Azure. `subjects` SHALL be set only at `onboard` and SHALL be
immutable under `update` (content-only). When an `onboard`-holder creates a ruleset, the
control plane SHALL record it as owner (trust-on-first-use), granting it `update`/`offboard`
on that ruleset.

#### Scenario: Update writes content only

- **WHEN** an authorized caller updates a ruleset
- **THEN** only `allowed_hosts` and `action` are updated, and any attempt to change
  `subjects` or `acl` in the same request is rejected

#### Scenario: Onboard requires the verb

- **WHEN** a caller without the `onboard` verb attempts to create a ruleset
- **THEN** the request SHALL be denied

#### Scenario: Trust-on-first-use assigns ownership

- **WHEN** a caller holding `onboard` creates a ruleset
- **THEN** that caller is recorded as the ruleset's owner and may subsequently `update` and
  `offboard` it

### Requirement: Report is the onboarding default

Evaluation within a ruleset SHALL be uniform — a ruleset has exactly one effective `action`,
and its hosts are never evaluated under differing actions. A ruleset created by `onboard`
**without an explicit action** SHALL be stored with `action: report`, so a workload whose
egress is not yet understood is observed rather than broken.

`report` SHALL be a default, never an override. An explicitly requested `action` SHALL always
be honoured, at onboard and afterwards: `report` permits all traffic and only logs it, so
coercing it over an explicit `enforce` would grant a new workload *more* egress than it asked
for. The system SHALL never lower a ruleset's action on its own — in particular, adding a host
to an enforcing ruleset SHALL NOT weaken it. Host additions and removals SHALL be audited and
surfaced in the `:check` diff, which is the control on widening an enforcing ruleset.

#### Scenario: Onboard without an action starts in report

- **WHEN** a caller onboards a ruleset without specifying an `action`
- **THEN** the ruleset is stored and rendered with `action: report`

#### Scenario: An explicit action at onboard is honoured

- **WHEN** a caller onboards a ruleset requesting `action: enforce`
- **THEN** the ruleset is stored and rendered with `action: enforce`

#### Scenario: Promotion is an explicit push

- **WHEN** the owning caller later pushes the tuned host set with `action: enforce`
- **THEN** the ruleset's effective action becomes `enforce`

#### Scenario: Adding a host never weakens an enforcing ruleset

- **WHEN** a push adds a host to a ruleset currently in `enforce` and requests `enforce`
- **THEN** the ruleset stays in `enforce` with the host added, and the addition is audited

### Requirement: Fail-closed decommission

The system SHALL make removing a subject's egress access fail-closed: when a ruleset is
offboarded (removed) or a subject is otherwise no longer governed by any ruleset, the subject
SHALL fall to the `fallback`/deny block on the next proxy reload.

#### Scenario: Offboarded ruleset denies its subjects

- **WHEN** a ruleset is offboarded
- **THEN** its former subjects are governed by the `fallback`/deny block after the next
  reload
