---
id: STRG-344
title: Sandbox hardening follow-up — design-only
milestone: v0.2
priority: low
status: open
type: docs
labels: [thumbnails, phase-15, security, sandbox, design-only]
depends_on: []
blocks: []
assigned_agent_type: feature-dev:code-explorer
estimated_complexity: small
---

# STRG-344: Sandbox hardening follow-up — design-only

## Summary

A design-only issue: produce a written threat-model + mitigation-options document for sandboxing image (and future PDF/Office) thumbnail generation. **No code is written under this issue.** The deliverable is a document under `docs/architecture/` that lays out the runway for the actual hardening work.

## Background / Context

Decision **D14** of issue #52 chose **in-process** safeguards for v1 (pixel cap + timeout + max-source-size, see STRG-338). This is sufficient against known image bombs but is NOT a mitigation against:

1. A 0-day in libheif / libwebp / libtiff / Magick.NET / ImageMagick that escapes the pixel-cap (e.g., heap overflow during header parse before our probe completes).
2. Future PDF/Office support (Phase 16) which dramatically widens the attack surface — PDFium, libreoffice, Docnet are all complex, network-aware, and historically CVE-rich.

Process isolation is the right answer; v1 doesn't ship it because it's a multi-week investment and the current safeguards bound the blast radius adequately. STRG-344 captures the design so the next engineer (likely after the first image-library CVE bites in 2027) can move quickly.

This issue is intentionally **design-only**, deliverable is documentation. The actual implementation will be a future issue (numbered when scheduled).

## Technical Specification

### Deliverable — `docs/architecture/07-thumbnail-sandboxing.md`

YAML frontmatter:

```yaml
---
title: "Thumbnail Sandboxing — Threat Model & Options"
tags: [architecture, thumbnails, security, design-only]
status: design
created: 2026-05-01
updated: 2026-05-01
---
```

### Required sections

1. **Scope** — what's covered (image generation, future PDF/Office) and what isn't (storage providers, GraphQL parsing, etc.).

2. **Threat Model**

   - **Attacker capability**: an authenticated tenant user who can upload arbitrary bytes (the upload endpoint validates MIME loosely; the consumer sniffs magic bytes but the decoder runs against attacker-controlled bytes).
   - **In-flight assets**: the generator process, the database connection, the storage provider's blob credentials, sibling-tenant data on the same host.
   - **Threat tree**:
     - T1: RCE via decoder library (Magick.NET / libheif / libwebp / future libreoffice).
     - T2: Resource exhaustion that bypasses the pixel-cap (e.g., a header that lies, or a decoder bug that allocates before our probe).
     - T3: SSRF via decoder library that opens external resources (PDF embedded URLs, libreoffice document metadata fetches).
     - T4: Filesystem access from a compromised generator (read other tenants' source files, write to the storage provider with the host's credentials).

3. **Current Mitigations (in v1, in-process)**
   - Pixel-cap + source-size + timeout (STRG-338).
   - `MagickImage.Read(Stream)` (no path injection).
   - `image.Strip()` (no metadata leak).
   - Network egress: NOT currently restricted — generator runs in-process with the API host's egress rules. A `HttpClient` factory misuse from inside Magick.NET would have full egress.

4. **Options for hardening** — table form:

   | Option | Maturity | Cost | Pros | Cons |
   |---|---|---|---|---|
   | Dedicated worker process (sidecar, restricted FS + memory cgroups) | Production-grade | High (deployment + IPC + observability) | Strong isolation; OS-level enforcement; familiar to ops | Multi-process complicates container deployment; need a stable IPC contract |
   | Per-generation Docker exec (one container per thumbnail) | Production-grade | Very high (container start latency, image hygiene) | Hardest isolation; clean teardown | Container start ≫ image decode for small images; breaks the latency budget |
   | Browser-style WebAssembly sandbox | Experimental | Very high | Pure-CPU sandbox; no FS / no network by default | No WASM build of Magick.NET / ImageMagick; would require a different image library |
   | Linux seccomp filter inside same process | Medium | Medium (need to allow-list every syscall) | Cheap, no IPC | Tricky to maintain across libheif / libwebp upgrades; lockfile not portable to non-Linux |
   | gVisor / Kata sandboxed runtime at the host | Production-grade (where supported) | Low (operator-side) | Transparent to the app; works for any container | Operator-only; can't enforce in upstream code |

5. **Recommended sequencing**
   - **Now**: keep in-process. No work.
   - **Trigger 1** — first CVE in `libheif` / `libwebp` / `Magick.NET` that escapes the pixel-cap or causes RCE: ship the **dedicated worker process** option. Estimated 2-3 engineer-weeks.
   - **Trigger 2** — Phase 16 (PDF/Office) lands: re-evaluate; the broader attack surface may make the dedicated worker urgent.
   - **Always**: document the runtime version of Magick.NET + libheif in observability so the operator knows when an advisory applies.

6. **What NOT to do**
   - Don't run the generator as root or with elevated capabilities.
   - Don't pass `IStorageProvider` credentials to a sandboxed process — give it ONLY the source bytes and let the parent process write the result.
   - Don't add a `Magick.NET` resource cap silently — the global `ResourceLimits` is process-wide and would affect every other Magick.NET caller (there are none today, but future plugins might collide).

7. **Open questions** (for the future implementer)
   - IPC: protobuf-over-stdin/stdout, gRPC, or shared memory?
   - Worker-process language: also .NET (uniform), or Rust (smaller surface, no GC stalls)?
   - Pool warmth: pre-fork worker pool vs. on-demand (latency tradeoff)?

## Acceptance Criteria

- [ ] `docs/architecture/07-thumbnail-sandboxing.md` exists with the YAML frontmatter and all seven required sections.
- [ ] Threat tree (T1–T4) is enumerated with concrete examples.
- [ ] Mitigation table compares at least the five options listed.
- [ ] "Recommended sequencing" gives a clear "what to do now / what to do on trigger".
- [ ] No code in this issue.

## Test Cases

- **TC-001**: A future engineer reading only this document can answer: "What attack are we actually defending against?", "What did v1 ship?", and "What's the cheapest next mitigation?"

## Implementation Tasks

- [ ] Write `docs/architecture/07-thumbnail-sandboxing.md` per the spec above.
- [ ] Internal review with someone outside the thumbnail tranche (cold reader catches missing context).

## Security Review Checklist

- [ ] Document does not enumerate strg-specific exploit chains in a publishable form (this is a public repo). Threat-tree should describe class-of-attack, not "here's how to compromise our host today".
- [ ] No internal infrastructure leakage (no host names, no AWS account IDs).

## Code Review Checklist

- [ ] Frontmatter matches existing ADR style.
- [ ] H1 + H2-only structure.
- [ ] Tables format consistently.

## Definition of Done

- [ ] Document published.
- [ ] Linked from the main thumbnail ADR (`docs/architecture/06-thumbnails.md`) under "Resource Safeguards" → "see 07-thumbnail-sandboxing.md for the long-term plan".
