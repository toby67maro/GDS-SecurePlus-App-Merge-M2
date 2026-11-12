# ADR-0011: Per-user configuration directory and data root

- Status: Accepted
- Date: 2026-11-06

## Decision

The application never writes to its install folder (MSIX installs are read-only). Configuration, data, logs, audit,
state and the secret store live under `%LOCALAPPDATA%\Global Data Systems\Secure Plus - Modern Work` (`config\` and
`data\`). Package INI and JSON files are templates copied there only when the user copy is missing: from the `config` folder next to the executable when present (ZIP, MSIX), otherwise from copies embedded in `EngineeringWorkspace.Desktop`, so a single-file executable also starts with a complete configuration. `--config-dir` and
`--data-dir` (or `ENGINEERINGWORKSPACE_BOOTSTRAP__DATADIRECTORY`) override the locations, for example to run directly
from a repository's `config` folder during development.

## Consequences

Upgrades keep user configuration. New template keys reach existing users through the compiled defaults. The state used
to pick the startup workspace and customer layers is read from the default state location before configuration loads.
