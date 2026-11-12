# ADR-0008: Secret references with a DPAPI CurrentUser secret store

- Status: Accepted
- Date: 2026-11-06

## Decision

Configuration, profiles, workspace files, database rows and exports carry `SecretReference` (`provider:name[@version]`)
only. Secret material lives behind `ISecretStore`; the Windows implementation uses DPAPI with CurrentUser scope, one
file per reference (SHA-256 name, reference bound as entropy), atomic writes and a current-user-only ACL. Credential
metadata is stored separately. On non-Windows hosts an `UnavailableSecretStore` refuses to store or read secrets.

## Consequences

- Only the same Windows user can decrypt; a profile or machine change requires re-entering secrets.
- Azure Key Vault (named by the EaaS architecture) can be added as another `ISecretStore` provider without changing
  consumers.
