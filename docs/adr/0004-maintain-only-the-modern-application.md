# Maintain only the modern application

F10 Recorder will maintain and distribute only the modern .NET/Avalonia application. Retire and remove the legacy WinForms/.NET Framework application, its build and installation options, and tooling used exclusively by that application. CI should build and verify the modern application; adding legacy compilation to CI is no longer planned.

Retaining a second application as a regression reference carries maintenance and compatibility costs that no longer serve the product direction. Inventory dependencies before removal: files under `src/` are still linked into the modern projects, so location or age alone does not establish that code is obsolete. Identify other removal candidates through their actual use by the modern application, packaging and verification tools.

Keep verification tools that test the modern application, including temporary WinForms test-window fixtures where they serve current checks. Port still-applicable dependency security checks from the legacy machinery into modern verification before retiring that machinery. CI may build the modern product and its relevant verification tools.

This records the agreed direction on 21 October 2026. Legacy application removal and dependency-check migration were implemented and validated locally; distribution remains a separate decision.
