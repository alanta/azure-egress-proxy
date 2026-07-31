# Vendored htmx

`htmx-2.0.10.min.js` — [htmx](https://htmx.org) 2.0.10, BSD Zero Clause License (0BSD),
© Big Sky Software.

It is checked in rather than fetched, for the same reason every action in `.github/workflows/`
is pinned to a SHA: a security reference implementation should not have a CDN in its trust
path, and this repo deliberately has no npm dependency tree to hang it from.

## Provenance

| | |
|---|---|
| Version | 2.0.10 |
| SHA-256 | `71ea67185bfa8c98c39d31717c6fce5d852370fcdfd129db4543774d3145c0de` |
| Sources | `https://unpkg.com/htmx.org@2.0.10/dist/htmx.min.js` and `https://cdn.jsdelivr.net/npm/htmx.org@2.0.10/dist/htmx.min.js` — byte-identical |

The file is served from the app at `/lib/htmx/htmx-2.0.10.min.js`. The version is in the
filename so an upgrade cannot be cached over silently, and `_Layout.cshtml` references it
literally.

## Upgrading

```bash
V=<new version>
curl -sSL -o /tmp/a.js https://unpkg.com/htmx.org@$V/dist/htmx.min.js
curl -sSL -o /tmp/b.js https://cdn.jsdelivr.net/npm/htmx.org@$V/dist/htmx.min.js
cmp /tmp/a.js /tmp/b.js            # two independent origins must agree
sha256sum /tmp/a.js                # record it in the table above
```

Then move the file in, update the reference in `_Layout.cshtml`, delete the old one, and
re-run `PortalTests` — one of them asserts that the referenced file exists and that no other
htmx copy is left behind.

**Do not vendor the eval-based extensions.** The portal's CSP carries no `unsafe-eval`
(see `SecurityHeaders.cs`), and `hx-vals='js:…'`, `hx-on:*`, and the `client-side-templates`
and `path-deps` extensions all require it. Rendering tables for an egress control does not
need script evaluation.
