---
id: STRG-026
title: Implement EncryptingStorageProvider (AES-256-GCM decorator)
milestone: v0.1
priority: high
status: done
type: implementation
labels: [infrastructure, storage, security]
depends_on: [STRG-021, STRG-024]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-026: Implement EncryptingStorageProvider — AES-256-GCM decorator

## Summary

Implement `EncryptingStorageProvider`, a decorator that wraps any `IStorageProvider` with per-file AES-256-GCM encryption. Each file gets a unique data encryption key (DEK) encrypted with the key encryption key (KEK).

## Technical Specification

### File: `src/Strg.Infrastructure/Storage/EncryptingStorageProvider.cs`

```csharp
public class EncryptingStorageProvider(IStorageProvider inner, IKeyProvider keyProvider) : IStorageProvider
{
    public string ProviderType => $"encrypted+{inner.ProviderType}";

    public async Task WriteAsync(string path, Stream content, FileVersion fileVersion, CancellationToken ct)
    {
        var dek = keyProvider.GenerateDataKey();           // 32 random bytes
        var nonce = RandomNumberGenerator.GetBytes(12);   // 96-bit AES-GCM nonce
        var encryptedContent = EncryptStream(content, dek, nonce);
        await inner.WriteAsync(path, encryptedContent, ct);
        // Store encrypted DEK in the file_keys table (NOT alongside the file)
        var encryptedDek = keyProvider.EncryptDek(dek);
        _fileKeyRepository.Add(new FileKey
        {
            FileVersionId = fileVersion.Id,
            EncryptedDek = encryptedDek,
            Algorithm = "AES-256-GCM"
        });
        // Caller commits via SaveChangesAsync
    }

    public async Task<Stream> ReadAsync(string path, long offset, CancellationToken ct)
    {
        var encryptedDek = await ReadBytesAsync($"{path}.dek", ct);
        var dek = keyProvider.DecryptDek(encryptedDek);
        var cipherStream = await inner.ReadAsync(path, 0, ct);  // always read from start for GCM
        return DecryptStream(cipherStream, dek);  // returns plaintext stream
    }
}
```

### File: `src/Strg.Core/Storage/IKeyProvider.cs`

```csharp
public interface IKeyProvider
{
    byte[] GenerateDataKey();
    byte[] EncryptDek(byte[] dek);
    byte[] DecryptDek(byte[] encryptedDek);
}
```

### Built-in implementation: `EnvVarKeyProvider` — reads KEK from environment variable `STRG_SECURITY__ENCRYPTIONKEY` (base64-encoded 32 bytes).

## Acceptance Criteria

- [ ] Files written through `EncryptingStorageProvider` are not readable without the KEK
- [ ] Files written through `EncryptingStorageProvider` read back to the original plaintext
- [ ] Each file has a unique DEK (not all files share one key)
- [ ] DEK is stored in the `file_keys` table (NOT alongside the file on disk) — one `FileKey` row per `FileVersion`
- [ ] Nonce is stored prepended to the ciphertext (first 12 bytes)
- [ ] `IKeyProvider` is injected (pluggable: EnvVar, Vault, K8s Secret)
- [ ] Encryption uses AES-256-GCM (authenticated encryption — integrity guaranteed)
- [ ] `ReadAsync` with `offset > 0` works correctly (decrypt entire file, seek to offset)
- [ ] `ListAsync` returns only actual files (no `.dek` files on disk — DEKs are in DB)

## Test Cases

- **TC-001**: Write file → read raw inner provider → content is not the original
- **TC-002**: Write file → read through `EncryptingStorageProvider` → content is original
- **TC-003**: Two writes of the same file → different DEK (randomized)
- **TC-004**: Corrupt the `.dek` file → `ReadAsync` throws authentication error
- **TC-005**: `ReadAsync` with offset=100 → bytes start at offset 100 of plaintext
- **TC-006**: `ListAsync` → `.dek` files not returned
- **TC-007**: Incorrect KEK → `ReadAsync` throws authentication error

## Implementation Tasks

- [ ] Create `IKeyProvider.cs` in `Strg.Core.Storage`
- [ ] Create `EnvVarKeyProvider.cs` in `Strg.Infrastructure.Storage` (reads `STRG_SECURITY__ENCRYPTIONKEY` env var)
- [ ] Create `FileKey.cs` entity in `Strg.Core.Domain` (`FileVersionId`, `EncryptedDek`, `Algorithm`, `CreatedAt`)
- [ ] Create `IFileKeyRepository.cs` in `Strg.Core.Domain`
- [ ] Create `FileKeyRepository.cs` in `Strg.Infrastructure`
- [ ] Create `EncryptingStorageProvider.cs`
- [ ] Implement AES-256-GCM encrypt/decrypt using `System.Security.Cryptography.AesGcm`
- [ ] Write comprehensive unit and integration tests

## Security Review Checklist

- [ ] Nonce is NEVER reused for the same key (use random nonce per write)
- [ ] DEK is wiped from memory after use (`CryptographicOperations.ZeroMemory`)
- [ ] GCM authentication tag is verified on every read (tampering detection)
- [ ] KEK is read from environment variable — never hardcoded
- [ ] No encryption used without authentication (no AES-CBC, only AES-GCM)
- [ ] `.dek` files not exposed through ListAsync
- [ ] Encryption key length is exactly 256 bits (32 bytes)

## Code Review Checklist

- [ ] `AesGcm` object is disposed after use
- [ ] `MemoryStream` wrapping decrypted content doesn't copy unnecessarily
- [ ] `offset` handling doesn't bypass GCM authentication

## Definition of Done

- [ ] Encryption/decryption roundtrip tested
- [ ] Security review passed
- [ ] Authenticated encryption tampering test passes
