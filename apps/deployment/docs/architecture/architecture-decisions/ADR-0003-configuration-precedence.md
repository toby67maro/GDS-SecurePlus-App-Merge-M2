# ADR-0003-configuration-precedence

Decision recorded for the foundation milestone; refined in the consolidated implementation.

Configuration is layered INI with a single precedence definition (`ConfigurationPrecedence`): compiled defaults <
Application.ini < Branding.ini < Features.ini < Navigation.ini < Repositories.ini < Integrations.ini < Database.ini <
Logging.ini < GoldenTenant.ini < Environments/{Environment}.ini < Workspace.ini < Customer.ini < Overrides.ini <
`ENGINEERINGWORKSPACE_*` environment variables < command line. Values carry provenance, are validated in four stages
and are exposed as an immutable snapshot of typed settings. See `docs/configuration/application-ini-reference.md`.
