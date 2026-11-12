# Experience mapping – brief (target 4.2, proposal first)

Recorded 8 Nov 2026 from the user's brief. Not started: in 4.0.0-beta.2 only the impact of policy, policy-set and control edits on the
mapping is evaluated.

1. **Answer → control mapping.** Every experience option links to the catalogue control IDs / PolicyRepository files it means (CA, Intune,
   Endpoint Security, checks; later Exchange/Teams/Purview). The free-text *configuration* stays as display text. Options with no control:
   "No control – gap" (link to COVERAGE.md); options with no configuration: "No policy (default)". A profile selected for a user group
   produces the policy list with that group as the assignment target – the input to the Specification, SOW and deployment plan. The app
   shows option → policies and reports unmapped controls.
2. **Question scope**: PerUserGroup / TenantWide / Either. Tenant-wide: guests, emergency access, external file sharing, forwarding, Teams
   lobby defaults. Either: SSPR, mail retention, Teams external access. Per user group: the rest. For tenant-wide questions the workspace
   posture (GDS - Essentials / Balanced / Locked Down) wins; the Specification shows the conflict; the engineer can override with a reason.
3. **Profile corrections**: Contractor / Vendor device questions = "Not applicable – unmanaged device" (web-only); Privileged Administrator
   ID-SSPR HELPDESK cannot be enforced (admin SSPR policy always on) → NOPASSWORD with a note; GDS - Essentials: Copilot pilot users need
   an oversharing check first (labels = none).
4. **Framework notes**: remove all Essential Eight notes; PCI notes say 4.0.1; confirm new-framework notes cover every option.
5. **New questions** (wording, options, defaults per profile): shared-device sign-in (QR code + PIN / Authenticator / FIDO2); company
   iPhones/iPads (app protection only / full device management); Teams apps (all / approved only); Copilot web search and agents
   (on / off / approved only).
6. **Personas** in BLUEPRINT §5: Knowledge Worker and Remote Worker → GDS - Balanced; Kiosk → User Group - Frontline; Field/Vessel →
   User Group - Remote Site. No new profiles beyond Remote Site.

Report the mapping coverage (options mapped / gap / default) before building; record decisions in DECISIONS.md.

## Evaluation in 4.0.0-beta.2: does editing policies affect the experience mapping?

**Today: no change is needed.** Experience options link to configuration as display text only (no control IDs), so creating,
editing, hiding or deleting a policy or policy set cannot break an option, a profile, the Experience Map or the SOW.

**From 4.2 (answer → control mapping) the editor must:**
1. Warn before hiding or deleting a policy that an experience option maps to, and list the options and profiles affected.
2. Show retired mapped policies (a GDS policy removed by a catalogue update) in the mapping coverage report as "gap".
3. Treat a Lead's edited copy of a GDS policy as the mapped policy (same ID), so edits flow into the specification.
4. Let a Lead map an option to a Lead policy (L-xxxx) or Lead policy set (L-SET-xxx).
The reload-safe edit store built in beta.2 (edits keyed by the GDS control ID, Lead items with their own IDs) supports all four.
