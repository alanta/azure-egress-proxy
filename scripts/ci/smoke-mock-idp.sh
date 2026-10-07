#!/usr/bin/env bash
# Build the mock IdP image, start it, issue a token and verify that token against the JWKS
# inside the container. Signing and verification both run in the image, so this exercises
# the Python base image together with the inline PyJWT[crypto] pin, the way the local
# stack uses them. Works with Docker or Podman's docker CLI.
#
# Run from anywhere: scripts/ci/smoke-mock-idp.sh
# MOCK_IDP_PORT overrides the host port (default 8080).
set -euo pipefail
cd "$(dirname "$0")/../.."

port=${MOCK_IDP_PORT:-8080}
image=mock-idp:smoke
name=mock-idp-smoke-$$

cleanup() {
  status=$?
  if [ "$status" -ne 0 ]; then
    echo "--- mock IdP logs"
    docker logs "$name" 2>&1 || true
  fi
  docker rm -f "$name" >/dev/null 2>&1 || true
  exit "$status"
}
trap cleanup EXIT

docker build -t "$image" mock-idp/
docker run -d --name "$name" -p "127.0.0.1:$port:8080" "$image" >/dev/null

ready=false
for _ in $(seq 1 30); do
  if curl -fsS "http://127.0.0.1:$port/jwks" >/dev/null 2>&1; then
    ready=true
    break
  fi
  sleep 1
done
if [ "$ready" != true ]; then
  echo "error: the mock IdP did not serve /jwks within 30 seconds." >&2
  exit 1
fi

token=$(curl -fsS "http://127.0.0.1:$port/token?appid=ci-smoke")
docker exec -i -e TOKEN="$token" "$name" python - <<'PY'
import json, os, urllib.request
import jwt
from jwt.algorithms import RSAAlgorithm
keys = json.load(urllib.request.urlopen("http://127.0.0.1:8080/jwks"))["keys"]
key = RSAAlgorithm.from_jwk(json.dumps(keys[0]))
claims = jwt.decode(os.environ["TOKEN"], key, algorithms=["RS256"], audience="egress-proxy")
assert claims["appid"] == "ci-smoke", claims
print("token verified for appid", claims["appid"])
PY
