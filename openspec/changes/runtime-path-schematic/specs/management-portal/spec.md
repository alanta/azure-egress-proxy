## MODIFIED Requirements

### Requirement: Runtime status is sourced from Azure

The portal SHALL obtain deployment configuration from ARM and operational metrics from Azure
Monitor, using its own managed identity. It SHALL NOT require the proxy to expose any HTTP
endpoint. It SHALL present these sources as the stages of a single egress path — load
balancer, proxy fleet, egress prefix — rather than as independent resources.

#### Scenario: Runtime panel reflects the deployment

- **WHEN** an operator views the runtime surface
- **THEN** the portal shows VMSS instances online, the egress public IP prefix capacity and how
  much of it is in use, network throughput, and load-balancer health

#### Scenario: Runtime is presented as one path

- **WHEN** an operator views the runtime surface
- **THEN** the portal draws the load balancer, the scale set and the egress prefix as ordered
  stages of the route traffic takes out of the network, and presents each stage's readings
  alongside the stage they describe

#### Scenario: Data freshness is stated, not implied

- **WHEN** the portal renders any metric sourced from Azure Monitor or Log Analytics
- **THEN** it displays the recency of that data, and does not present it as live

#### Scenario: One slow source does not blank the surface

- **WHEN** one runtime source is slow or unavailable while the others answer
- **THEN** the stages fed by the answering sources continue to render and refresh

## ADDED Requirements

### Requirement: An unreadable runtime source is shown as unread, never as healthy

The portal SHALL distinguish three states for every runtime stage: a healthy reading, an
unhealthy or degraded reading, and no reading at all. A stage whose source could not be read
SHALL NOT be presented in the appearance used for a healthy stage, and SHALL NOT be presented
as reporting zero.

#### Scenario: Resource Manager does not answer

- **WHEN** ARM cannot be read while Azure Monitor can
- **THEN** the stages that depend on ARM are shown as unread, visually distinct from both
  healthy and unhealthy stages, and the stages fed by Azure Monitor continue to report

#### Scenario: A node reports no health verdict

- **WHEN** a scale-set instance carries no application-health verdict
- **THEN** the portal presents the absence of a verdict as its own state, and not as a healthy
  node

### Requirement: The runtime surface states what the egress prefix's capacity means for the fleet

The portal SHALL state, in prose alongside the path, the operational consequence of the egress
prefix's remaining capacity — how many further nodes the fleet can add before one would egress
from an address outside the prefix. It SHALL state this whether or not the prefix is under
pressure.

#### Scenario: The prefix is exhausted

- **WHEN** every address in the egress prefix is assigned
- **THEN** the portal states that a further node would egress from an address outside the block,
  which no partner has allowlisted, and whose traffic is refused at the partner's edge

#### Scenario: The prefix has spare capacity

- **WHEN** the egress prefix has addresses to spare
- **THEN** the portal states how many further nodes the fleet can add, rather than omitting the
  statement because nothing is wrong

#### Scenario: The prefix cannot be read

- **WHEN** the egress prefix is not readable
- **THEN** the portal states that the capacity is unknown, and does not present the fleet as
  either constrained or unconstrained
