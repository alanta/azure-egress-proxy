#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

frontdoor_url="${1:-${FRONTDOOR_URL:-}}"
if [[ -z "$frontdoor_url" ]]; then
  echo "Usage: scripts/demo.sh <frontdoor-url>" >&2
  exit 1
fi

deployment_name="${DEPLOYMENT_NAME:-egress-proxy-demo}"
allowlist_file="${ALLOWLIST_FILE:-$repo_root/allowlist/allowlist.json}"
tmp_allowlist="$(mktemp)"
trap 'rm -f "$tmp_allowlist"' EXIT

deployment_output_json="$(az deployment sub show --name "$deployment_name" --query properties.outputs -o json)"
allowlist_account="$(python3 - "$deployment_output_json" <<'PY'
import json,sys
doc=json.loads(sys.argv[1]); print(doc["allowlistStorageAccountName"]["value"])
PY
)"
allowlist_container="$(python3 - "$deployment_output_json" <<'PY'
import json,sys
doc=json.loads(sys.argv[1]); print(doc["allowlistContainerName"]["value"])
PY
)"
allowlist_blob="$(python3 - "$deployment_output_json" <<'PY'
import json,sys
doc=json.loads(sys.argv[1]); print(doc["allowlistBlobName"]["value"])
PY
)"

echo "Allowed request (expect success:true):"
curl -fsS "${frontdoor_url%/}/try/allowed" | head -c 400
echo

# The app catches the proxy's 403-on-CONNECT and reports it in the body,
# so the HTTP status here is 200 and the evidence is success:false + error.
echo "Denied request (expect success:false with a proxy 403 error):"
curl -fsS "${frontdoor_url%/}/try/denied" | head -c 400
echo

cat <<'KQL'
KQL:
EgressProxy_CL
| where EventType == "CANONICAL-PROXY-DECISION"
| project TimeGenerated, ReqId, Role, Host, Allow, DecisionReason
| order by TimeGenerated desc
KQL

if [[ "${ADD_DENIED_HOST:-0}" == "1" ]]; then
  # Allowlist the host behind /try/denied (Demo:DeniedHost, example.org by
  # default), re-upload, and time how long the flip takes end to end.
  denied_host="${DENIED_HOST:-example.org}"
  cp "$allowlist_file" "$tmp_allowlist"
  python3 - "$allowlist_file" "$denied_host" <<'PY'
import json,sys
path,host=sys.argv[1],sys.argv[2]
doc=json.load(open(path,encoding="utf-8"))
for module in doc.get("modules",[]):
    if module.get("id")=="sample-app":
        hosts=set(module.get("allowed_hosts",[]))
        hosts.add(host)
        module["allowed_hosts"]=sorted(hosts)
json.dump(doc,open(path,"w",encoding="utf-8"),indent=2)
open(path,"a",encoding="utf-8").write("\n")
PY
  az storage blob upload \
    --account-name "$allowlist_account" \
    --container-name "$allowlist_container" \
    --name "$allowlist_blob" \
    --file "$allowlist_file" \
    --auth-mode login \
    --overwrite \
    --only-show-errors >/dev/null

  echo "Allowlisted $denied_host; polling /try/denied until it flips..."
  start_ts="$(date +%s)"
  deadline=$((start_ts + 90))
  while true; do
    body="$(curl -fsS "${frontdoor_url%/}/try/denied" || true)"
    if [[ "$body" == *'"success":true'* ]]; then
      echo "Propagated in $(( $(date +%s) - start_ts ))s: $body"
      break
    fi
    if (( $(date +%s) >= deadline )); then
      echo "Timed out waiting for propagation (last: $body)" >&2
      break
    fi
    sleep 2
  done

  mv "$tmp_allowlist" "$allowlist_file"
  az storage blob upload \
    --account-name "$allowlist_account" \
    --container-name "$allowlist_container" \
    --name "$allowlist_blob" \
    --file "$allowlist_file" \
    --auth-mode login \
    --overwrite \
    --only-show-errors >/dev/null
fi
