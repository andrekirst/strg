# Strg.Architecture.Tests

Holds solution-wide invariants that no single-project test can enforce:

- **Layer-direction guards** — e.g. `Strg.Infrastructure` must not reference `Strg.GraphQl`.
- **DI wiring invariants** — e.g. every `Strg.Core.Events.*` domain event has at least one registered consumer.
- **Transitive-package guards** — e.g. the CVE-vulnerable `NWebDav.Server.AspNetCore` must never appear in the bin/ graph.
- **Shape guards on base types** — e.g. `TenantedEntity.TenantId` must stay init-only.

## Why this lives in its own project

1. **Reflection over solution assemblies is slow.** Isolating the cost keeps the fast unit-test projects fast.
2. **Discoverability.** Developers (and reviewers) see every architectural invariant in one place.
3. **Reference scope.** This project references *every* `src/Strg.*` project, so each assembly's bin output lands in the same load context — the only way to observe the full transitive graph the production host sees.

## When to add a test here

A test belongs here — not in a per-project test assembly — when its invariant spans multiple projects, targets the referenced-assembly graph, or asserts a shape that must hold across the whole solution. A plain unit test that happens to use reflection does not qualify.

## Source-text vs. runtime-DI invariants

A few invariants (e.g. "Program.cs wires consumer X into the bus callback") are easier to pin by grepping `Program.cs` than by booting a real `WebApplicationFactory<Program>`. Those tests read the file and assert a literal shape; they're noted inline where they appear, and they're intentional rather than a shortcut — the source line *is* the invariant. Restructuring that would move the registration out of `Program.cs` is exactly the regression the test guards against.
