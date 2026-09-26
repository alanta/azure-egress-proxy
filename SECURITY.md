# Security policy

This project is a security control: it decides which hosts a workload may reach. We take reports
about it seriously.

## Reporting a vulnerability

**Don't open a public issue.** Report privately in one of two ways:

- **GitHub private vulnerability reporting** (preferred):
  [report a vulnerability](https://github.com/alanta/azure-egress-proxy/security/advisories/new).
- **Email:** security@alanta.nl.

Please include:

- what is affected: the proxy, the control plane, the console, the client library, the
  infrastructure templates, or the Marketplace image (with its image version);
- how to reproduce it, and what an attacker gains: for example reaching a host outside the
  allowlist, passing as another workload, widening an allowlist without authorization, or
  hiding a decision from the audit trail;
- the version or commit you tested.

## What to expect

The project has one maintainer, so responses are best effort. We aim to:

- acknowledge your report within 5 working days;
- tell you whether we accept it, and what we plan to do, within 14 days;
- fix accepted issues in a new release, and in a new Marketplace image version where the image
  is affected.

We publish a GitHub security advisory for each fixed vulnerability, and credit you if you want.
Please give us a reasonable time to ship a fix before you disclose publicly. We'll agree a date
with you.

## Supported versions

Only the **latest release** gets security fixes. For the Marketplace image, that means the latest
image version. Older image versions are withdrawn after a while, not patched.

## Scope

In scope: everything in this repository, and the Marketplace image built from it.

Not vulnerabilities:

- **Documented demo-grade simplifications.** The reference deployment trades some hardening for
  simplicity, for example a public storage endpoint or open ingress on the sample app. Each one is
  listed, with its production counterpart, in
  [docs/production-hardening.md](docs/production-hardening.md).
- **Behaviour that is by design.** See the FAQ in the [README](README.md).
- **The local mock identity provider** (`mock-idp/`). It is for local development only and must
  never be deployed.

Report vulnerabilities in dependencies to their own projects: Smokescreen to
[Stripe](https://github.com/stripe/smokescreen/security), Azure Linux packages to
[Microsoft](https://msrc.microsoft.com/). If one affects this project in a way specific to how we
use it, report that to us too.
