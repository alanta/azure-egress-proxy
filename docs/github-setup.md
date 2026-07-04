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
