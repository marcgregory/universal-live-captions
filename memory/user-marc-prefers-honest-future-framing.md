---
name: user-marc-prefers-honest-future-framing
description: Marc prefers explicit, falsifiable product copy — never advertise capabilities the current release does not have, even if they're benchmarked or planned.
metadata:
  type: user
---

Marc (the project owner) values product copy that mirrors what the current release actually does. He'd rather undersell a feature than overstate it.

**How to apply:**
- On shipping surfaces (landing page, App UI, README badges, marketing copy), describe only what works **today**.
- If something is benchmarked, designed, ADR'd, or planned but not shipped, label it clearly as *planned / future / not in this release* — and link to the document where the future work lives (`docs/implementation/RELEASE_PLAN.md`, ADR file, etc.).
- Don't put disabled/greyed-out controls in Settings just to "match the website." Ship only working controls.
- When the future work actually lands, update landing page + App UI in the same release.

This is reinforced by the constitution (§11.7 no inventing business rules, §11.10 no work-without-evidence) and the project's *frozen production path* discipline (see `docs/implementation/CHANGELOG.md` v0.5.29 — Argos+naturalizer is the frozen production path; Gemini Live is the documented-but-not-shipped experimental reference).

Related: [[no-future-features-on-shipping-surface]].
