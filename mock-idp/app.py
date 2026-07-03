"""Mock Entra IdP for the managed-identity JWT model (A).

Stands in for Azure Container Apps' managed-identity token endpoint + Entra's JWKS:

  GET /token  -> a short-lived RS256 JWT. The `appid` claim is decided by the CALLER'S
                 SOURCE SUBNET, mirroring ACA: a workload can only obtain a token for its
                 own managed identity, never another module's. (So even a compromised
                 client gets only its own module's token.)
  GET /jwks   -> the public signing key, so any proxy can validate tokens offline.

Throwaway in-memory key; do not use for anything real.
"""
import ipaddress
import json
import time
from http.server import BaseHTTPRequestHandler, HTTPServer
from urllib.parse import parse_qs, urlparse

import jwt
from jwt.algorithms import RSAAlgorithm
from cryptography.hazmat.primitives.asymmetric import rsa

KEY = rsa.generate_private_key(public_exponent=65537, key_size=2048)
KID = "mock-key-1"
ISS = "https://mock-idp.local/"
AUD = "egress-proxy"
TTL = 300  # seconds

# Same module<->subnet mapping the proxies use; one ACA environment per module.
SUBNETS = [
    (ipaddress.ip_network("172.30.10.0/24"), "module-a"),
    (ipaddress.ip_network("172.30.20.0/24"), "module-b"),
]


def module_for(ip):
    addr = ipaddress.ip_address(ip)
    for net, mod in SUBNETS:
        if addr in net:
            return mod
    return None


def jwks():
    jwk = json.loads(RSAAlgorithm.to_jwk(KEY.public_key()))
    jwk.update({"kid": KID, "use": "sig", "alg": "RS256"})
    return {"keys": [jwk]}


class Handler(BaseHTTPRequestHandler):
    def _send(self, code, body, ctype="application/json"):
        b = body if isinstance(body, bytes) else body.encode()
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(b)))
        self.end_headers()
        self.wfile.write(b)

    def do_GET(self):
        parsed = urlparse(self.path)

        if parsed.path == "/jwks":
            self._send(200, json.dumps(jwks()))
        elif parsed.path == "/token":
            query = parse_qs(parsed.query)
            explicit_appid = (query.get("appid") or [""])[0].strip()
            ip = self.client_address[0]
            appid = explicit_appid or module_for(ip)
            if not appid:
                self._send(403, json.dumps({"error": f"no managed identity for {ip}"}))
                return
            now = int(time.time())
            claims = {
                "iss": ISS, "aud": AUD, "appid": appid, "sub": appid,
                "iat": now, "nbf": now, "exp": now + TTL,
            }
            token = jwt.encode(claims, KEY, algorithm="RS256", headers={"kid": KID})
            self._send(200, token, "text/plain")
        else:
            self._send(404, json.dumps({"error": "not found"}))

    def log_message(self, fmt, *args):  # quiet; the proxies are the audit trail
        pass


if __name__ == "__main__":
    HTTPServer(("0.0.0.0", 8080), Handler).serve_forever()
