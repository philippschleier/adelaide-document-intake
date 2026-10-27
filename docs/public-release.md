# How this public version was produced

Adelaide's production repository is private. It processes health data (GDPR Art. 9) for a real care provider, so this
repository was built as a **separate codebase from an explicit allowlist of reviewed files**. It was not built by
copying everything and deleting known secrets. It has a fresh Git history, and no commits, branches or tags from the
private repository.

## Preserved (production code, reviewed)

- `src/Adelaide.Core`, `Ai`, `Audit`, `SharePoint`, `Web`, `Web.Client`, `Drain`, `Worker`: the application code,
  including the production filing path, the gate, ledger, outbox, reconcile sweep, auth and security hardening, and
  the Angular review UI.
- `tests/`: the Core, Audit, SharePoint, Web (out-of-process HTTP surface) and Migrate test suites.
- `tools/Adelaide.Migrate` (ledger migrator), `tools/Adelaide.ClassifyEval` (offline classifier evaluation),
  `tools/Adelaide.LinuxSmoke` (native-load smoke test).
- `infra/sql/*.sql`: the Azure SQL ledger and capture schemas, which the contract tests execute.
- CI workflows for the test suites, the durability-guard check and the native smoke test.

## Removed

| What | Why |
|---|---|
| Terraform (`infra/azure`, `infra/entra`, `infra/bootstrap`) and the deploy workflow | Organisation-specific network topology, resource names, tenant, subscription and site identifiers. |
| Internal design documents, runbooks, troubleshooting notes, audits | Operational detail about the production environment and incidents. They are referenced in code comments but not published. |
| `investigation/` scratch harnesses and queries | Investigation tooling written against the production corpus. |
| Production maintenance commands (backfills, filed-corpus scans, recycle-bin and PDF/A censuses), PowerShell tenant scripts, corpus baseline scripts | Written for one-off operations on the live tenant and corpus. |
| The barcode memory-safety stress harness and its notes | Investigation narrative and an unfiled upstream bug report. The fix, and the compile-time ban on the unsafe API, remain in `BarcodeScanner` / `BannedSymbols.txt`. |
| An environment-specific test that read data paths on the production machine | It could not run anywhere else. |

## Replaced or added

- **Configuration:** the hard-coded tenant, client and site defaults were removed. Every identifier is now a required
  environment variable ([`.env.example`](../.env.example)), and a missing one fails at startup by name.
- **Comments:** specific hostnames, machine paths, internal network names and incident narratives were rewritten
  neutrally. Technical rationale was kept.
- **Seams:** two small interfaces were added so the web endpoints no longer depend on concrete SharePoint and Graph
  classes:
  - `IFiledDocumentStore` (filed-PDF read-back);
  - `IPatientSearch` (the review patient picker).

  The patient-matching strategy moved into `PatientMatching.MatchAsync`, so the Graph and demo directories share it.
- **Demo mode:** `src/Adelaide.Demo`, `DemoMode.cs`, the synthetic corpus in `demo/`, its generator, and tests that
  pin each sample's outcome, including an end-to-end run through the real host.
- **Dependency updates:** native SQLite is pinned to 2.1.14 (closing the previously suppressed GHSA-2m69-gcr7-jv3q),
  `System.Security.Cryptography.Pkcs` to 10.0.14 (CVE-2026-84008), and Angular to 22.2.x (security fixes).
- **Tests:** the scaffold Angular spec was replaced by real unit tests.
- **Docs and CI:** README, these docs, and CI jobs for the Angular client and a Gitleaks secret scan.

## Checks run before publication

- **Gitleaks**, over the full history of the private repository and over this tree.
- **A local pattern scan** with masked output, so values were never printed:
  - patient-identifier shapes: KVNR, LANR/BSNR, IBAN, phone numbers, dates of birth;
  - emails, GUIDs, IP addresses, UNC and Windows paths, Azure and SharePoint hostnames, resource names;
  - a deny-list of organisation and person names.
- **Synthetic-value checks:** every KVNR-shaped value is a known synthetic test value, and every name in fixtures is
  a standard German placeholder.
- **Manual review** of every file on the allowlist.
- **Dependency vulnerabilities and licenses:** `dotnet list package --vulnerable` and `npm audit`. All direct .NET
  dependencies are MIT or Apache-2.0, and the npm tree is permissive. See
  [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).
