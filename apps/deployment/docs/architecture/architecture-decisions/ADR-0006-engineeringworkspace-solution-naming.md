# ADR-0006: EngineeringWorkspace solution and root namespace

- Status: Accepted
- Date: 2026-11-06

## Context

The consolidated implementation prompt requires the solution, projects and root namespace to be `EngineeringWorkspace` (`src/EngineeringWorkspace.Desktop`, `.Application`, `.Domain`, `.Contracts`, `.Configuration`, `.Infrastructure` and the matching `tests/EngineeringWorkspace.*.Tests` projects). The repository found at the start of this run used `SecurePlus.ModernWork.*` for the same six source and five test projects.

## Decision

The projects, solution file (`EngineeringWorkspace.slnx`) and namespaces were renamed with `git mv` so history is preserved. No project was added, removed or duplicated. The product display name stays **Secure Plus - Modern Work (Global Data Systems)** and is configured in `Application.ini`/`Branding.ini`.

Identifiers that are part of an external contract were intentionally kept:

- MSIX package identity `GlobalDataSystems.SecurePlus.ModernWork` (changing it would break upgrades of installed packages).
- Artifact file names `SecurePlus.ModernWork-{Version}-win-x64.*`.
- `Application:Id = SecurePlus.ModernWork` (a stable product identifier, not a namespace).

The environment-variable prefix becomes `ENGINEERINGWORKSPACE_`, as the prompt specifies.

## Consequences

- The desktop executable is now `EngineeringWorkspace.Desktop.exe`; the MSIX manifest and packaging script reference it.
- Anyone who set `SECUREPLUS_*` environment variables must rename them to `ENGINEERINGWORKSPACE_*`.
