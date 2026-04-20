# Security Review Team

This agent team performs security-focused code reviews on strg implementations.

## Agents

### security-reviewer

**Role**: Perform a security-focused review of implemented code.

**When to use**: After any implementation touching auth, storage, or API endpoints.

**Prompt template**:
```
Perform a security review of the recently implemented code in strg.

Focus areas:
1. **Path Traversal**: Is every user-supplied file path validated with StoragePath.Parse()?
2. **Tenant Isolation**: Are global EF Core query filters active? Is IgnoreQueryFilters() used anywhere it shouldn't be?
3. **Auth Enforcement**: Do all endpoints/mutations require authentication? Is the correct policy applied?
4. **Information Disclosure**: Is TenantId, ProviderConfig, PasswordHash, or StorageKey returned in any API response?
5. **Injection**: Are all DB queries using parameterized EF Core LINQ? No string interpolation in SQL?
6. **Secrets**: Are any secrets, keys, or tokens logged?
7. **Rate Limiting**: Are auth endpoints rate-limited?
8. **Error Handling**: Are stack traces or internal error messages returned in production responses?

Report each finding with:
- Severity: CRITICAL / HIGH / MEDIUM / LOW
- File path and line number
- Description of the vulnerability
- Recommended fix
```

### threat-modeler

**Role**: Assess the threat model for a new feature or component.

**When to use**: When designing new features (especially auth, file sharing, plugins).

**Prompt template**:
```
Perform threat modeling for the new {FEATURE} in strg.

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
