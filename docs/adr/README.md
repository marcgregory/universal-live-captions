# ADR Index

Use ADRs only for consequential decisions that materially affect architecture, cost, security, operations, data ownership, or delivery speed.

## Decisions

| ADR | Status | Decision | Date |
| --- | --- | --- | --- |
| ADR-0001 | Approved | Native stack: .NET 8 + C# + WPF + NAudio | 2026-07-31 |
| ADR-0002 | Approved | WASAPI loopback capture, no VB-CABLE, Windows 10 target | 2026-07-31 |
| ADR-0003 | Approved | Streaming `ISpeechToTextEngine` abstraction; local Whisper as first engine | 2026-07-31 |
| ADR-0004 | Approved | WPF always-on-top caption overlay + separate control window | 2026-07-31 |
| ADR-0005 | Approved | Testing strategy: xUnit with fakes at hardware boundaries | 2026-07-31 |
| ADR-0006 | Approved | `ITranslationEngine` abstraction; Argos Translate as first engine (local process) | 2026-07-31 |
| ADR-0007 | Proposed | Boundary-aware streaming transcript commit (longest-stable-prefix + meaningful boundary + bounded latency fallback) | 2026-08-03 |
| ADR-0008 | Approved | Production STT default = faster-whisper native streaming + live partials (supersedes ADR-0003 default-model clause; ggml-base kept as explicit fallback) | 2026-08-05 |
| ADR-0009 | Approved | Windows Credential Manager as the Gemini API-key source of truth in the App (production App reads once at session start, drops on Stop; `UC_GEMINI_API_KEY` env-var fallback removed from the App path) | 2026-08-08 |

## Rule

Add or update an ADR when the team chooses a durable technical direction or rejects a plausible alternative for a reason future maintainers need to understand.
