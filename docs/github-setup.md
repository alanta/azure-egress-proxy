# GitHub Actions OIDC setup (fork/repo bootstrap)

This repo's deploy/allowlist workflows use GitHub OIDC with Azure (`azure/login`) and
repository variables, not static secrets.

## 1. Create an Entra app registration for GitHub Actions

1. In Microsoft Entra ID, create an **App registration** (for example
   `azure-egress-proxy-github-actions`).
2. Record:
   - **Application (client) ID**
   - **Directory (tenant) ID**
3. Create a **federated credential** for your repo and branch:
   - Issuer: `https://token.actions.githubusercontent.com`
   - Subject: `repo:alanta/azure-egress-proxy:ref:refs/heads/main`
   - Audience: `api://AzureADTokenExchange`

For release tags, add a second federated credential with tag subject support in your
tenant (for example `repo:alanta/azure-egress-proxy:ref:refs/tags/v*`, or explicit tag
subjects if wildcard tags are not enabled).

## 2. Assign Azure roles

Assign roles to the **service principal** of that app registration:

1. At subscription scope:
   - `Contributor`
   - `User Access Administrator` (needed for role assignments performed by deployment)
2. After the first infra deployment (storage account exists), at storage account scope:
   - `Storage Blob Data Contributor`

## 3. Configure repository variables

Set these repository variables in GitHub (`Settings -> Secrets and variables -> Actions`):

| Variable | Purpose |
|---|---|
| `AZURE_CLIENT_ID` | App registration client ID |
| `AZURE_TENANT_ID` | Entra tenant ID |
| `AZURE_SUBSCRIPTION_ID` | Azure subscription ID |
| `ALLOWLIST_STORAGE_ACCOUNT` | Storage account name that hosts `egress-config/allowlist.json` |
| `DEMO_APP_URL` | Optional; public sample app URL used by smoke/probe steps |

## 4. Workflow expectations

- `deploy.yml` (`workflow_dispatch`) logs in with OIDC and runs `scripts/deploy.sh`.
- `allowlist.yml` validates `allowlist/allowlist.json` against
  `allowlist/allowlist.schema.json`, then uploads with:
  `az storage blob upload --overwrite --auth-mode login`.
- Both workflows skip gracefully when required files or variables are missing (for forks and
  pre-WP5 branches).

## 5. CI and the required check

`ci.yml` runs on pull requests, on pushes to `main` and weekly. A `changes` job decides which
jobs apply; a job that doesn't apply reports *skipped*.

| Changed paths | Jobs |
|---|---|
| `proxy/**` | Proxy image (amd64 + arm64, as released), govulncheck, Go toolchain consistency |
| `src/**`, `*.slnx`, `Directory.*`, `global.json`, `NuGet.config` | .NET build and test, the sample-app, control-plane and portal images (amd64 + arm64, as released) |
| `mock-idp/**` | Mock IdP image build and token smoke test |
| `infra/**` | Bicep build and NSG description lengths |
| `scripts/**/*.sh` | shellcheck |
| `.github/workflows/**` | actionlint (with shellcheck over `run:` bodies), Go toolchain consistency |
| `.github/workflows/images.yml` | All four image builds |
| `.github/workflows/ci.yml` | Everything |

**One image build.** `images.yml` is a reusable workflow that defines how the proxy,
sample-app, control-plane and portal images are built (amd64 + arm64). `ci.yml` calls it with
`push: false`; `release.yml` calls it with `push: true` and the release version. A change to
a Dockerfile, a builder action or the build itself is therefore built by CI exactly as a
release would build it. Only the GHCR login and the push run in a release alone.
`images.yml` has no `permissions:` block on purpose: a called workflow can only narrow what
its caller grants, so each calling job declares the token it hands over (read-only in CI,
`packages: write` in the release).

**Go toolchain consistency.** The Go line is declared in `proxy/go.mod`, `proxy/Dockerfile`
and every `setup-go` step. Dependabot bumps the Dockerfile tag on its own and can't bump
`go.mod`, so the check fails a PR that moves one without the others: govulncheck and the
release binaries must use the same Go line as the image.

**What CI can't run.** The rest of `release.yml` (binaries, the GitHub Release, the push),
`deploy.yml` and `allowlist.yml` need tags, GHCR writes or Azure. A change to them is
linted, not executed; an `azure/login` bump, for example, is first exercised by the next
deploy.

**`CI result` is the required check.** The `CI Results` ruleset requires it on `main`, from the
GitHub Actions app, with no bypass actors, so it applies to admins too (the `Main` ruleset's
admin bypass covers reviews, not CI). It requires `CI result` rather than individual jobs:
a skipped required check counts as passing, so requiring path-gated jobs proves nothing.
`CI result` fails when any job failed or was cancelled, including a cancelled `changes` job
that would otherwise skip everything.
