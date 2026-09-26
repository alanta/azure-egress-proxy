# Privacy policy

This policy covers the azure-egress-proxy project: the source code in this repository, the
release binaries, and the Alanta Egress Proxy offer on Microsoft Marketplace.

**Who we are.** The controller for the personal data described here is Marnix van Valen, trading
as Alanta, Tilburg, the Netherlands ("we"). Contact: **privacy@alanta.nl**.

Last updated: 26 September 2026.

## The software sends us nothing

The proxy, the control plane, the management console and the client library run in *your* Azure
subscription or on your own machines. They contain no telemetry and don't contact us.

- Everything they log (including the egress audit trail) goes where you configure it: the
  systemd journal on the host, your own Log Analytics workspace, your own Application Insights
  resource. We have no access to any of it.
- The allowlist, rulesets and workload identities stay in your storage account and your Entra
  tenant.

**Microsoft telemetry in the reference deployment.** The Bicep templates in `infra/` use Azure
Verified Modules. By default those modules record a
[telemetry deployment](https://azure.github.io/Azure-Verified-Modules/help-support/telemetry/)
that Microsoft uses to count module usage. That information goes to Microsoft, not to us. To turn
it off, set `enableTelemetry: false` on the modules.

## What we do receive

### Marketplace leads

When you deploy or request the Alanta Egress Proxy through Microsoft Marketplace, Microsoft
sends us your contact details as a *lead*. This typically includes your name, email address,
company, country and the offer you deployed. Microsoft's privacy statement covers what Microsoft
collects; this section covers what we do with what we receive.

- **Why:** to know who uses the offer, and to contact you about it, for example about a security
  issue or a change to the offer.
- **Legal basis:** our legitimate interest in supporting and improving the offer (GDPR article
  6(1)(f)).
- **Where:** in an Azure storage account in our own subscription. Only we have access.
- **How long:** at most 24 months. We purge leads at least once a year.
- **Sharing:** we don't sell leads or share them with anyone.

Microsoft also gives Marketplace publishers reports about how their offers are used. We use them
only to understand how the offer is used.

### GitHub issues, discussions and pull requests

Support and contributions go through GitHub. What you post there is public, and GitHub's
[privacy statement](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement)
applies to it. We use it to answer you and to maintain the project, and it stays part of the
project's history.

### Email

If you email us (privacy@alanta.nl, security@alanta.nl), we use your message only to answer it
and keep it only as long as that takes.

## Your rights

Under the GDPR you can ask us to access, correct or delete the personal data we hold about you,
and you can object to how we use it. Email privacy@alanta.nl. We answer within one month.

You can also complain to the Dutch data protection authority,
[Autoriteit Persoonsgegevens](https://www.autoriteitpersoonsgegevens.nl/).

## Changes

We change this policy by changing this file. Its history in this repository shows every
change.
