# Innovative Features

These are future capabilities that differentiate strg from existing self-hosted storage platforms. Each is designed as a plugin implementing a well-defined core interface.

---

## IF-01: AI Auto-Tagging

**Plugin**: `Strg.Plugin.AiTagger`
**Interface**: `IAITagger`
**Trigger**: `file.uploaded` outbox event

### How It Works

When a file is uploaded, the AI tagger plugin receives a callback with the file metadata and a stream of its content. The plugin sends the content to a multimodal LLM (configurable: Claude, GPT-4o, Gemini, or a self-hosted Ollama model) and receives suggested key-value tags.

```
file.uploaded event
  → AI Tagger plugin
    → text extraction (PDF, Office, markdown)
    → image description (JPEG, PNG, WEBP)
    → LLM: "Suggest tags for this file"
    → returns: [{ key: "project", value: "acme" }, { key: "type", value: "invoice" }]
  → write to tags table (pending approval or auto-applied by rule)
```

### Configuration

```json
{
  "AiTagger": {
    "Provider": "claude",
    "Model": "claude-opus-4-7",
    "AutoApply": false,
    "AutoApplyRules": [
      { "mimeType": "application/pdf", "confidence": 0.9 }
    ],
    "ExcludedMimeTypes": ["video/*", "audio/*"]
  }
}
```

### User Experience

- Suggested tags appear in the file's "Pending Tags" list
- Users approve or reject individual suggestions
- With `AutoApply: true`, tags above the confidence threshold are applied immediately
- Suggestions are visible via GraphQL: `file { pendingTags { key value confidence } }`

---

## IF-02: ActivityPub Federation

**Plugin**: `Strg.Plugin.ActivityPub`
**Interface**: `IFederationProvider`

### Concept

strg instances can share drives with users on other strg instances — or any ActivityPub-compatible server — without a central coordination service. This is analogous to email (federated by design) but for file storage.

### How It Works

```
strg.alice.com                    strg.bob.com
  Drive: @projects@strg.alice.com
    └── Publishes ActivityPub Collection
    └── File events as ActivityPub Activities

  bob@strg.bob.com follows @projects@strg.alice.com
    → receives notifications when files change
    → can access shared files via federated API
```

### ActivityPub Objects

| strg concept | ActivityPub type |
|--------------|-----------------|
| Drive | `Collection` |
| Folder | `Collection` (nested) |
| File | `Document` |
| Upload | `Create` Activity |
| Delete | `Delete` Activity |
| Move | `Move` Activity |
| Share | `Announce` Activity |

### Identity

- Each drive gets a federated actor: `@drive-name@strg.host`
- Inter-instance auth uses HTTP Signatures (RFC 9421) + JWK public keys
- Follows the Mastodon/Lemmy/PeerTube federation model

### Security

- Federated shares are explicit — drives are not public by default
- The drive owner approves follow requests before access is granted
- Content is served over HTTPS with signed requests

---

## IF-03: AI File Assistant (Semantic Search)

**Plugin**: `Strg.Plugin.SemanticSearch`
**Interface**: `ISearchProvider`

### How It Works

Files are processed through a text extraction pipeline and embedded using a vector embedding model. Embeddings are stored in pgvector (PostgreSQL extension). Natural language queries are embedded and matched against stored embeddings via approximate nearest-neighbor search.

```
file.uploaded event
  → text extraction (Tika / pdftotext / Pandoc)
  → chunk text into 512-token windows
  → embed each chunk (OpenAI / Claude / Ollama)
  → store in pgvector alongside file_id + chunk_index

user query: "invoice from Acme November 2024"
  → embed query
  → vector similarity search (top-K chunks)
  → retrieve chunks + surrounding context
  → LLM synthesis: "Found 3 files matching your query..."
  → return FileSearchResult[]
```

### GraphQL API

```graphql
query {
  semanticSearch(
    query: "invoice from Acme November 2024"
    driveId: "..."
    limit: 10
  ) {
    file { id name path drive { name } }
    relevanceScore
    snippet           # LLM-synthesized excerpt
    chunkContext      # raw text chunk that matched
  }
}
```

### Configuration

```json
{
  "SemanticSearch": {
    "EmbeddingProvider": "claude",
    "EmbeddingModel": "claude-haiku-4-5-20251001",
    "ChunkSize": 512,
    "ChunkOverlap": 64,
    "SynthesisModel": "claude-sonnet-4-6",
    "IndexedMimeTypes": ["application/pdf", "text/*", "application/msword"]
  }
}
```

---

## IF-04: IPFS Storage Backend

**Plugin**: `Strg.Plugin.IpfsStorage`
**Interface**: `IStorageProvider`

### Concept

Files are stored by their content hash (CID — Content Identifier) rather than by path. This enables:
- **Automatic deduplication**: identical files stored once, even across drives or users
- **Verifiable integrity**: content hash is proof of authenticity
- **Decentralized backup**: files can be pinned on multiple IPFS nodes globally

### How It Works

```
Upload: file content → SHA-256 hash → CID
  → store in IPFS node (local or remote)
  → store { file_id → CID } mapping in PostgreSQL

Download: file_id → lookup CID in DB → retrieve from IPFS
  → verify content hash matches CID (integrity check)

Deduplication: if CID already exists in IPFS, no re-upload
  → just write the { file_id → CID } mapping
```

### Configuration

```json
{
  "IpfsStorage": {
    "ApiUrl": "http://localhost:5001",
    "GatewayUrl": "https://ipfs.io/ipfs",
    "PinLocally": true,
    "RemotePins": ["https://web3.storage/", "https://nft.storage/"]
  }
}
```

### Limitations

- IPFS content is immutable — "file updates" create a new CID
- Version history maps naturally: each version is a different CID
- Private files require encryption before storage on IPFS (use `EncryptingStorageProvider`)
- IPFS retrieval latency may be higher than local storage for rarely accessed content

---

## Roadmap Summary

| Feature | Version | Plugin |
|---------|---------|--------|
| AI Auto-tagging | v1.0 | `Strg.Plugin.AiTagger` |
| AI File Assistant | v1.0 | `Strg.Plugin.SemanticSearch` |
| IPFS Backend | v1.0 | `Strg.Plugin.IpfsStorage` |
| ActivityPub Federation | v2.0 | `Strg.Plugin.ActivityPub` |
| Collaborative editing | v2.x | `Strg.Plugin.CollabEdit` |
| E2E client-side encryption | v1.x | `Strg.Plugin.E2EEncryption` |
