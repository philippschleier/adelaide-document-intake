# Security

## Reporting

If you find a security issue in this code, please report it privately with GitHub's **"Report a vulnerability"**
(Security → Advisories) on this repository rather than opening a public issue.

## Data in this repository

This repository contains **no real patient data, credentials, tenant identifiers or organisation-specific
configuration**. All sample documents, patients and identifiers under `demo/` are synthetic. If you believe you have
found real personal data or a secret here, please report it the same way, and it will be removed and the history
rewritten.

## Running it

- Demo mode (`ADELAIDE_DEMO_MODE=1`) refuses to start next to production settings and blocks all outbound network
  calls. It uses a Development-only local identity and must never be exposed beyond `localhost`.
- Configuring the app against real Microsoft 365 / Azure services means processing special-category health data.
  That requires a lawful basis, a data-processing agreement for the cloud services, EU-region processing, access
  control and an audit regime. The code supports these; it does not provide them on its own.
