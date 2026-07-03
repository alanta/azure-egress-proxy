#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output_file="${OUTPUT_FILE:-$repo_root/infra/identity.generated.json}"
app_display_name="${APP_DISPLAY_NAME:-egress-proxy}"
tenant_id="${TENANT_ID:-$(az account show --query tenantId -o tsv)}"
token_version="${TOKEN_VERSION:-2}"

if [[ "$token_version" != "2" && "$token_version" != "1" ]]; then
  echo "TOKEN_VERSION must be 1 or 2" >&2
  exit 1
fi

issuer_suffix=""
expect_aud=""
jwks_url="https://login.microsoftonline.com/${tenant_id}/discovery/v2.0/keys"

app_id="$(az ad app list --display-name "$app_display_name" --query "[0].appId" -o tsv)"
if [[ -z "$app_id" ]]; then
  app_id="$(az ad app create \
    --display-name "$app_display_name" \
    --sign-in-audience AzureADMyOrg \
    --identifier-uris "api://${app_display_name}" \
    --requested-access-token-version "$token_version" \
    --query appId -o tsv)"
else
  object_id="$(az ad app list --display-name "$app_display_name" --query "[0].id" -o tsv)"
  az ad app update \
    --id "$object_id" \
    --identifier-uris "api://${app_display_name}" \
    --requested-access-token-version "$token_version" \
    >/dev/null
fi

if [[ "$token_version" == "2" ]]; then
  issuer_suffix="/v2.0"
  expect_aud="$app_id"
else
  issuer_suffix="/"
  expect_aud="api://${app_display_name}"
fi

expect_iss="https://login.microsoftonline.com/${tenant_id}${issuer_suffix}"

mkdir -p "$(dirname "$output_file")"
cat >"$output_file" <<EOF
{
  "tenantId": "$tenant_id",
  "tokenVersion": $token_version,
  "appDisplayName": "$app_display_name",
  "appId": "$app_id",
  "EXPECT_AUD": "$expect_aud",
  "EXPECT_ISS": "$expect_iss",
  "JWKS_URL": "$jwks_url"
}
EOF

echo "Wrote $output_file"
