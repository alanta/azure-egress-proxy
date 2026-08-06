## ADDED Requirements

Requirements below marked *(pre-existing)* describe topology this change does not modify. They
are recorded because this capability describes the deployment's shape as a whole, and because
several are load-bearing security properties that no spec held before. The work they imply is
confirming the deployment already satisfies them — and correcting the requirement, not the
infrastructure, where it does not.

### Requirement: Enforcement is the network floor, not the workload's cooperation *(pre-existing)*

A workload subnet SHALL deny outbound Internet traffic, so that the proxy is the only route out
regardless of whether a workload is configured to use it. Proxy environment variables SHALL NOT
be the mechanism of enforcement.

#### Scenario: A workload that ignores the proxy still cannot reach the Internet

- **WHEN** a workload opens a direct outbound connection to an Internet destination, bypassing
  its proxy configuration
- **THEN** the subnet's network security group denies it

#### Scenario: The floor admits only named Azure destinations

- **WHEN** a workload subnet's outbound rules are inspected
- **THEN** each allowance above the deny rule names a specific service tag or address required by
  a resident of that subnet, and none of them is the open Internet

### Requirement: The proxy subnet is deliberately unrestricted outbound *(pre-existing)*

The subnet hosting the proxy SHALL NOT carry the workload egress floor. It is the component whose
purpose is to reach arbitrary allowed destinations on the Internet, and its policy is the
allowlist it enforces, not a network rule.

#### Scenario: The proxy reaches allowed destinations

- **WHEN** the proxy connects outward on behalf of a workload to a destination its allowlist
  permits
- **THEN** no network security group rule prevents it, and the decision rests with the allowlist

#### Scenario: The exemption is confined to the proxy

- **WHEN** the deployment's subnets are inspected
- **THEN** only the proxy's subnet is without the deny-Internet floor

### Requirement: Traffic leaves through a known, enumerable set of addresses *(pre-existing)*

Proxied egress SHALL leave the deployment through a public IP prefix, so that a partner can
allowlist the deployment by address range. The fleet SHALL NOT be able to egress from an address
outside that prefix.

#### Scenario: Egress addresses come from the prefix

- **WHEN** the proxy connects to an external destination
- **THEN** the source address observed by that destination belongs to the deployment's public IP
  prefix

#### Scenario: The prefix bounds the fleet

- **WHEN** the prefix has no unassigned address left
- **THEN** the fleet cannot grow, because an additional node would have to egress from outside
  the block that partners allowlist

### Requirement: Workloads reach the proxy by name over a private path *(pre-existing)*

A workload SHALL reach the proxy through a private DNS name resolving to a private load-balancer
frontend, over network peering. The proxy SHALL NOT be reachable for this purpose over a public
address.

#### Scenario: The name resolves privately

- **WHEN** a workload resolves the proxy's name
- **THEN** it receives the private frontend address of the internal load balancer, and the
  private DNS zone is linked to the networks whose residents need that name and no others

#### Scenario: The path is peering, not the Internet

- **WHEN** a workload connects to the proxy on its listening port
- **THEN** the traffic crosses the peering between the workload and proxy networks


### Requirement: The management plane is isolated from the data plane and the workloads

The control plane and the management console SHALL be deployed in a virtual network that is
peered with neither the proxy's network nor any workload network, and in a Container Apps
environment shared with no workload. Their dependencies SHALL be reached as platform endpoints
rather than over a network path to another zone.

#### Scenario: No route exists between the management zone and the proxy

- **WHEN** the deployment is inspected for network paths
- **THEN** the management virtual network has no peering to the hub or spoke virtual networks,
  and no rule in either network security group grants the management subnet reachability to the
  proxy subnet

#### Scenario: The control plane cannot depend on the proxy

- **WHEN** the control plane resolves and reaches its own dependencies
- **THEN** it reaches blob storage and the identity provider directly, and has no route through
  the proxy it configures

#### Scenario: Management compute is not co-tenant with a workload

- **WHEN** the control plane or the console is deployed
- **THEN** it runs in a Container Apps environment that hosts no workload application

### Requirement: The workload egress floor grants no management-only destination

A subnet's outbound rules SHALL admit only the destinations its own residents require. Egress
opened for the management plane SHALL NOT be present on a workload subnet.

#### Scenario: Workloads cannot reach Azure Resource Manager

- **WHEN** a workload in the spoke attempts an outbound connection to Azure Resource Manager
- **THEN** the connection is denied by the subnet's network security group

#### Scenario: The management subnet carries its own ARM allowance

- **WHEN** the console reads scale-set state, public-IP-prefix consumption, or Azure Monitor
  metrics
- **THEN** its own subnet permits the outbound connection to Azure Resource Manager

### Requirement: Separation does not sever the paths the deployment depends on

Isolating the zones SHALL leave every path the deployment needs intact. A workload SHALL still
reach the proxy, the console SHALL still reach the control-plane API, and each component SHALL
still reach its own platform dependencies.

#### Scenario: Workloads still reach the proxy

- **WHEN** a workload in the spoke opens a `CONNECT` to the proxy
- **THEN** the subnet's rules permit it, and an allowed destination succeeds and a denied one is
  refused exactly as before

#### Scenario: The console still reaches the control plane

- **WHEN** the console renders policy
- **THEN** it reaches the control-plane API, both being applications of the same environment

#### Scenario: Each component reaches its own dependencies

- **WHEN** the proxy reads its configuration, the control plane writes it, or the console queries
  Azure Monitor and Azure Resource Manager
- **THEN** each subnet's rules permit that component's own destinations

### Requirement: The management zone exists only when it has residents

The management resource group, network and environment SHALL be deployed only when the control
plane is deployed. A deployment without a control plane SHALL create no management-zone resource,
identity or role assignment.

#### Scenario: Mode 1 deploys no management zone

- **WHEN** the deployment is made without the control plane, which is the default
- **THEN** no management resource group, virtual network, network security group, environment or
  identity is created, and the proxy and its workloads are fully functional

#### Scenario: The console does not bring a zone into existence alone

- **WHEN** the console is requested without the control plane
- **THEN** the deployment does not proceed on that combination, the console having no source of
  policy to read

#### Scenario: Grants follow the zone

- **WHEN** no management zone is deployed
- **THEN** no `AcrPull` for a management pull identity exists on the registry, and no `Reader` or
  `Monitoring Reader` for the console exists on the platform resource group

### Requirement: Artifacts precede the compute that fetches them

Compute SHALL NOT be deployed before the artifacts it fetches at start-up exist. The proxy binary
and every container image SHALL be placed by a phase that completes before the deployment
creating the scale set and the container applications begins.

#### Scenario: A first deployment succeeds from nothing

- **WHEN** the deployment is run against a subscription holding none of its resources
- **THEN** the scale set finds its binary at first boot and every container application finds its
  image at first pull, with no manual step between

#### Scenario: A missing artifact fails visibly

- **WHEN** an artifact cannot be placed
- **THEN** the deployment fails at that point and reports it, rather than continuing to create
  compute that will start and never serve

#### Scenario: The registry is optional

- **WHEN** every image is given as a reference that is already pullable
- **THEN** no registry is deployed, no pull identity is granted on one, and no application is
  configured with registry credentials

### Requirement: Platform identities are deployed with the compute they belong to

Each user-assigned identity SHALL be created in the resource group of the workload that uses it.
Its role assignments SHALL be scoped to the resource being granted, not to the identity's own
resource group.

#### Scenario: Management identities live with management compute

- **WHEN** the deployment is inspected
- **THEN** the control-plane and console identities exist in the management resource group

#### Scenario: The console can read the egress platform and nothing wider

- **WHEN** the console's role assignments are inspected
- **THEN** `Reader` and `Monitoring Reader` are scoped to the hub resource group, and the
  console holds no role on its own resource group, on any workload resource group, or on the
  configuration storage account

### Requirement: Shared platform resources are deployed with the platform

A resource serving more than one zone SHALL be deployed in the platform resource group, and no
zone's platform dependency SHALL be deployed in a workload resource group. This covers the
configuration storage account, the bootstrap storage account, the container registry, and the
log workspace.

#### Scenario: The registry is platform infrastructure

- **WHEN** the deployment is inspected
- **THEN** the container registry is in the platform resource group, and every identity that
  pulls from it holds its `AcrPull` assignment scoped to that registry

#### Scenario: The management plane does not boot from a workload resource

- **WHEN** the control plane or the console starts
- **THEN** every artifact and configuration source it reads is outside any workload resource
  group

### Requirement: Image pull uses a per-environment identity

Each Container Apps environment SHALL have one user-assigned identity dedicated to image pull,
and it SHALL be the only identity holding `AcrPull`. No application's functional identity SHALL
hold a registry grant, and no pull identity SHALL be shared between environments.

#### Scenario: Workload identity carries no infrastructure grant

- **WHEN** the sample application's user-assigned identity is inspected
- **THEN** it holds no role assignment on the container registry, so the identity the proxy
  authenticates by its `appid` claim grants nothing beyond being that workload

#### Scenario: Pull identities do not cross a zone boundary

- **WHEN** the registry's role assignments are inspected
- **THEN** each environment's pull identity is distinct, and no identity is referenced by
  applications in more than one environment

### Requirement: Configuration and bootstrap storage are data-plane resources

The account holding the rendered allowlist and the account holding the proxy binary SHALL be
deployed in the same resource group as the proxy, and SHALL be reachable by the proxy without
depending on the control plane.

#### Scenario: Mode 1 needs no control plane

- **WHEN** the deployment is made without the control plane
- **THEN** the configuration account exists, the proxy reads its allowlist from it, and no
  management-zone resource is required for the proxy to serve traffic

#### Scenario: The scale set fetches its binary at boot

- **WHEN** a scale-set instance boots
- **THEN** it obtains a managed-identity token and reads the proxy binary from the bootstrap
  account over its own subnet's outbound path

### Requirement: Storage is written with Entra credentials only

No storage account in the deployment SHALL permit shared-key access. Every upload path — the
proxy binary, the allowlist seed, and demonstration swaps — SHALL authenticate as an Entra
principal.

#### Scenario: Shared keys are refused

- **WHEN** a client attempts to authenticate to either storage account with an account key
- **THEN** the request is refused because shared-key access is disabled on the account

#### Scenario: The binary upload authenticates as the deployer

- **WHEN** the deployment uploads the proxy binary
- **THEN** it authenticates with the deploying principal's Entra identity, which holds an
  explicitly assigned data-plane role on the bootstrap account
