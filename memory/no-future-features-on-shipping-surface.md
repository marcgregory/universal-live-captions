---
name: no-future-features-on-shipping-surface
description: Do not advertise future/in-development features on the v0.5.29 shipping surface (landing page, App UI, settings) — only describe what the current release actually does.
metadata:
  type: feedback
---

The shipping surface (landing page sections, App Settings controls, feature lists, capability comparison cards) must describe **only** what the current release does. Future/in-development items — even if benchmarked or documented elsewhere — belong in `docs/` (RELEASE_PLAN, ADR backlog, CHANGELOG), not in user-visible product surfaces.

**Why:** Constitution §11.7 forbids inventing business rules, API signatures, or requirements; §11.10 forbids marking work complete without validation evidence. A landing-page card that lists "More natural, realtime output / Your own API key / Toggle off to return to offline" makes those look like shipped features even with a "Coming soon" caveat. A disabled Settings field that doesn't do anything is a fake control. The user explicitly distinguished "we have benchmarked X" from "X is a capability of this release" — they are not the same, and conflating them on the shipping surface violates both the constitution and the user's trust.

**How to apply:** When a feature is in research/benchmark/documented-but-not-shipped state (Gemini Live Translate, ADR backlog items, Slice 13 candidates, etc.):
- **Landing page:** describe it as a *future roadmap item* with a model name or one-line description of what's being evaluated. Do not include a capability checklist, "What's included" bullets, or a feature comparison card.
- **App Settings:** no disabled/greyed-out fields, no "Coming soon" placeholders that aren't wired. Ship only controls that work end-to-end.
- **Release plan / CHANGELOG / ADR:** this is the *correct* place for the detailed description of the future work — point there from the landing page, don't duplicate.
- When the feature actually ships in a later release: add the real Settings UI and the real landing-page capability card in the same release. Update both at once.

**Concrete precedent (2026-08-07):** v0.5.29 landing page `#gemini` card. Initial draft included a 4-bullet capability list + Settings teaser; user pushed back: *"the agent's earlier interpretation was wrong because it conflated 'we have benchmarked Gemini' with 'Gemini is a capability of v0.5.29.'"* Final framing: "GEMINI LIVE TRANSLATE — PLANNED · not included in v0.5.29 · Model evaluated: gemini-3.5-live-translate-preview". No bullets. The App's translation section has no Gemini-related controls at all and stays untouched for this release.

Related: [[artifact-registry]], [[user-marc-prefers-honest-future-framing]].
