# Secrets reference

## Layers

| Layer | Holds | Where |
|---|---|---|
| Configuration | Non-secret settings and **secret references** only | INI files |
| Credential metadata | Identity and lifecycle: CredentialId, DisplayName, CredentialType, Provider, Created/Updated/Expiration (UTC), SecretReference, AssociatedCustomerId, AssociatedTenantId | `data/Credentials/metadata/*.json` (schema also has `secureplus.CredentialMetadata`) |
| Secret store (`ISecretStore`) | Secret material | `data/Credentials/secrets/*.secret` (DPAPI) |
| Authentication services | Resolve a reference only when an operation needs it | Infrastructure (MSAL, SqlClient) |

## Secret references

`provider:name[@version]`, for example `ClientSecretReference=os:customer/contoso/primary/graph-client-secret`.
`os` is the operating-system store (DPAPI). Names are slash-separated segments of letters, digits, `.`, `_` and `-`;
empty, `.` and `..` segments are rejected, so a reference cannot escape the store. Consumers depend only on
`ISecretStore` (`GetSecretAsync`, `SetSecretAsync`, `DeleteSecretAsync`, `SecretExistsAsync`); an Azure Key Vault
provider (for example `kv:`) can be added by registering another implementation without changing consumers.

`ClientSecret=<value>`, `Password=<value>` or any other secret-bearing key with a value is a configuration error
(`PlaintextSecret`), as is `Password=` inside a value. The value is redacted in every message.

## DPAPI provider

- **Scope:** `CurrentUser`. Only the same Windows user on the same profile can decrypt. After a profile reset,
  roaming to another machine or a machine rebuild, the secrets must be entered again (Credential Management shows
  *Invalid* or *Missing*). Machine scope was not used because it would let any local user decrypt.
- Each secret is a file named by the SHA-256 of its reference; the reference is also bound as DPAPI entropy, so a file
  cannot be swapped to another reference.
- Writes are atomic (temporary file in the same folder, write-through, then replace); a failed write cannot corrupt
  an existing secret. Replace overwrites the file; Delete removes it. Plaintext buffers are zeroed after use.
  Deleted files are not securely wiped from disk, but they only ever contain DPAPI ciphertext.
- The store directory ACL is restricted to the current user (inheritance removed). The store is under
  `%LOCALAPPDATA%`, outside any Git working tree; validation rejects a credential directory inside one.
- Secret values never appear in settings, logs (all log lines pass through the redaction service), audit,
  exceptions, exports or the UI after saving. There is no Show Secret action; saved secrets show as `********`.

## Credential Management

**Workspace > Settings > Credential Management** lists metadata and status (Configured, Missing, Expires Soon,
Expired, Invalid) and the secret-store provider, and offers Add, Replace, Delete (with confirmation) and Validate.
`[Credentials] ExpirationWarningDays` (default 30) sets the Expires Soon threshold. Validate reads and decrypts the
secret (without showing it) and records `CredentialExpiring`/`CredentialExpired` audit events when applicable.

## Redaction

`IRedactionService` (Application) is the single redaction service used by configuration diagnostics, logging
(`RedactingTextFormatter`), audit, exports, exception reporting and database diagnostics. It masks secret-bearing
keys and removes passwords, `client_secret`, tokens, bearer headers, JWTs and private-key blocks from free text,
including connection strings.

## Import and export

Exports may include customer ID and name, tenant ID and name, primary domain, client ID, service principal ID,
authentication type, credential ID and metadata, secret references and non-secret integration settings
(`CustomerProfileExportV1`, `WorkspaceConfigurationExportV1` in Contracts). They never include client secrets, tokens,
SQL passwords or private keys — the contracts have no field that can carry them. Importing a profile whose secret
reference does not exist on the workstation reports **Credential Required** (the tenant test reports it today;
the import page is M2).
