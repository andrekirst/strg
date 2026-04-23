# Security Review Team

## Purpose

Run security-focused review passes on recently implemented code — either
reactive (after a sensitive change landed) or proactive (during design of a
new feature that touches auth, storage, sharing, or plugins).

## When to Use

- Any implementation that touches authentication, authorisation, storage
  providers, path handling, or API/GraphQL surfaces exposed to users.
- Design-time for new features before writing code (threat model first).

## Agent Roster

Conceptual roles are mapped to real Claude Code `subagent_type` values.
`silent-failure-hunter` is kept as a distinct role (per CC-003's spec) even
though its concerns overlap with `security-reviewer` — the prompt template
is narrower and noisy/false-positive-prone, so running it separately keeps
its findings visible.

| Role                   | subagent_type     | Stage       | Tool-scope intent        |
|------------------------|-------------------|-------------|--------------------------|
| silent-failure-hunter  | `general-purpose` | Post-impl   | read-only (**advisory**) |
| security-reviewer      | `general-purpose` | Post-impl   | read-only (**advisory**) |
| threat-modeler         | `general-purpose` | Design-time | read-only (**advisory**) |

**Tool-scope intent** is the role's *design* contract. Claude Code's
built-in `subagent_type` values do not let a project declare a per-role
`allowed-tools` allowlist, so the "advisory" label is conveyed only by the
prompt template, not enforced by the harness.

## Team Inputs

- The diff or set of files under review (`git diff HEAD`, branch range, or
  explicit file list).
- Reference: `docs/requirements/07-security.md` (security requirements) and
  the Security Rules section of `CLAUDE.md`.
- For threat modelling: the feature proposal or design doc.

## Team Outputs

- Findings ranked CRITICAL / HIGH / MEDIUM / LOW.
- Each finding carries: file path, line number, description, recommended fix.
- For threat modelling: a STRIDE table with mitigations present vs. missing.

---

## Agent: silent-failure-hunter

**Role**: Hunt for silent error handling — swallowed exceptions, empty
catches, `catch { return null; }`, logs-without-rethrow that erase stack
traces, and `try { ... } catch { }` (CLAUDE.md forbidden pattern #3).

**subagent_type**: `general-purpose`.

**Expected Inputs**:
- Changed files (diff range or explicit list).

**Expected Outputs**:
- File + line for every suspicious handler with a one-line severity tag
  and a fix suggestion.

**Prompt Template**:
```
Hunt for silent failures in the recently changed C# code.

Read only the changed files (list: {FILE_LIST}). Do not modify anything.

Flag every occurrence of:
- Empty catch blocks:                 catch { }   /   catch (Exception) { }
- Catch-return-null / catch-return-default patterns that erase the error.
- Catch blocks that only log and continue without rethrow, when the caller
  cannot detect that the operation failed.
- Missing `CancellationToken` propagation that swallows cancellation.
- Fire-and-forget `Task`s that are not awaited and have no continuation
  handling exceptions.
- EF Core `SaveChangesAsync` calls inside a catch that itself may throw
  without the outer transaction being rolled back.

For each finding, report:
- Severity: HIGH (data loss or silent corruption) / MEDIUM (observability
  loss) / LOW (style).
- file:line.
- One-line fix suggestion.

If there are no findings, say so explicitly — an empty report is a valid
outcome.
```

---

## Agent: security-reviewer

**Role**: Pass-through security review of implemented code.

**subagent_type**: `general-purpose`.

**Expected Inputs**:
- List of changed files (or `git diff HEAD`).
- Issue ID if review is tied to a specific issue.

**Expected Outputs**:
- Findings list grouped by severity.
- Each finding references `file:line` and cites which CLAUDE.md rule or
  security requirement it violates.

**Prompt Template**:
```
Perform a security review of the recently implemented code in strg.

Focus areas:
1. Path Traversal: Is every user-supplied file path validated with
   StoragePath.Parse()?
2. Tenant Isolation: Are global EF Core query filters active? Is
   IgnoreQueryFilters() used anywhere it shouldn't be? (Pre-auth carve-outs
   like UserRepository.GetByEmailAsync must re-apply TenantId + IsDeleted
   inline with a justification comment — reject any other usage.)
3. Auth Enforcement: Do all endpoints/mutations require authentication? Is
   the correct policy applied?
4. Information Disclosure: Is TenantId, ProviderConfig, PasswordHash, or
   StorageKey returned in any API response?
5. Injection: Are all DB queries using parameterised EF Core LINQ? No string
   interpolation in SQL?
6. Secrets: Are any secrets, keys, or tokens logged?
7. Rate Limiting: Are auth endpoints rate-limited?
8. Error Handling: Are stack traces or internal error messages returned in
   production responses?

Report each finding with:
- Severity: CRITICAL / HIGH / MEDIUM / LOW
- File path and line number
- Description of the vulnerability
- Recommended fix
```

---

## Agent: threat-modeler

**Role**: STRIDE-based threat model for a new feature at design time.

**subagent_type**: `general-purpose`.

**Expected Inputs**:
- Feature name and short description (what it does, who can call it, what
  data it touches).
- Any existing design doc or issue file.

**Expected Outputs**:
- STRIDE table with one row per threat category.
- For each threat: attack scenario, mitigations in place, recommended
  additional mitigations.

**Prompt Template**:
```
Perform threat modelling for the new {FEATURE} in strg.

Use the STRIDE framework:
- Spoofing: Can an attacker impersonate another user or tenant?
- Tampering: Can an attacker modify data they shouldn't have access to?
- Repudiation: Are all actions audited?
- Information Disclosure: Is any sensitive data exposed?
- Denial of Service: Can an attacker exhaust resources?
- Elevation of Privilege: Can a regular user gain admin access?

For each threat, provide:
- Threat description
- Attack scenario
- Mitigations already in place
- Additional mitigations recommended
```
