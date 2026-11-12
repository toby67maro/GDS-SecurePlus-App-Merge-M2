# ADR-0010: Microsoft Entra SQL authentication through MSAL and the access-token callback

- Status: Accepted
- Date: 2026-11-06

## Context

Microsoft.Data.SqlClient 7.x moved its driver-provided `Active Directory *` authentication modes into
`Microsoft.Data.SqlClient.Extensions.Azure`, which is not on the approved package list. The core driver still
supports `SqlConnection.AccessTokenCallback` (learn.microsoft.com/sql/connect/ado-net/sql/azure-active-directory-authentication).

## Decision

Entra Interactive, Service Principal and Managed Identity database authentication acquire tokens with MSAL
(`Microsoft.Identity.Client`, already required for Microsoft 365) inside `AccessTokenCallback`. The resource and
authority come from the server's authentication request, so no Azure SQL endpoint is hard-coded. SQL login passwords
are passed as `SqlCredential`, never in the connection string.

## Consequences

No Azure.Identity or SqlClient extension package is needed. Interactive sign-in requires a public-client app
registration (`EntraClientId`) with the `http://localhost` redirect URI.
