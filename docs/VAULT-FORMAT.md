# Cripty vault format

This document specifies the vault and backup formats implemented on the repository's `master` branch at commit `9d7074e`. It is intended for maintainers, migration authors, recovery-tool developers, and reviewers who need to reproduce or validate the current format.

This is a description of the implementation, not a promise that future versions will remain byte-for-byte compatible. For the surrounding design, see [ARCHITECTURE.md](ARCHITECTURE.md). For the cryptographic threat model and residual risks, see [SECURITY-MODEL.md](SECURITY-MODEL.md) and [KNOWN-LIMITATIONS.md](KNOWN-LIMITATIONS.md).

## Status and terminology

The key words **MUST**, **MUST NOT**, **SHOULD**, and **MAY** describe requirements for compatibility with the current implementation.

Cripty has two independent version layers:

| Layer | Current value | Purpose |
| --- | ---: | --- |
| Vault-file format | `1` | Outer `vault.cripty` JSON container and its cryptographic context |
| Entry-file format | `1` | Outer `.entry` JSON container and its cryptographic context |
| Blob-file format | `1` | Outer `.blob` JSON container and its cryptographic context |
| Manifest schema | `3` | Decrypted manifest DTO; schemas `1` through `3` are readable |
| Entry schema | `1` | Decrypted entry DTO; only schema `1` is readable |
| Backup format | `1` | Plaintext backup index and copied vault payload layout |

An **outer record** is JSON that can be parsed before unlock. A **protected payload** is plaintext recovered only after successful authentication and decryption. An **envelope** is the JSON representation of an IV, ciphertext, and authentication tag.

## Vault directory layout

~~~text
<vault-directory>/
├── vault.cripty
├── entries/
│   └── <entry-id>.entry
└── blobs/
    └── <blob-id>.blob
~~~

| Path | Required | Contents |
| --- | --- | --- |
| `vault.cripty` | Yes | Password key slot and encrypted manifest |
| `entries/<entry-id>.entry` | For every persisted entry | Encrypted entry fields |
| `blobs/<blob-id>.blob` | For every persisted blob reference | Encrypted raw blob bytes |

`<entry-id>` and `<blob-id>` are GUIDs formatted with hyphens (`D` format), for example `a8c12b86-0790-4a57-889a-44e7c4df19b0.entry`. The file stores also compare the ID parsed from the requested filename with the outer record's ID. Files renamed to another object's name are rejected.

There is no binary magic number or fixed binary header. Cripty recognizes files by their location and name, parses JSON, and then validates `formatVersion`, identities, envelope shape, and protected data.

## JSON encoding rules

Outer records, manifest plaintext, entry plaintext, and the backup index use `System.Text.Json` with `JsonSerializerDefaults.Web`. Compatible writers MUST therefore use the following representation:

| Value | JSON representation |
| --- | --- |
| Property names | camel case |
| GUID | Hyphenated string, such as `"a8c12b86-0790-4a57-889a-44e7c4df19b0"` |
| `byte[]` | Standard padded Base64 string |
| `DateTimeOffset` | ISO 8601 string; entry timestamps MUST have UTC offset `+00:00` |
| `DateOnly` | ISO date string, `YYYY-MM-DD` |
| Enum | JSON number, not enum name |
| `Dictionary<Guid, T>` | JSON object whose property names are GUID strings |
| Null optional value | JSON `null` under the current default options |

Vault, entry, and blob files are normally compact JSON. `backup-index.json` is indented. Whitespace and object-property order are not semantically significant. Array order is preserved by serialization, but identity and reference validation—not array position—defines relationships.

Examples below are schematic. Base64 values are shortened and are not valid cryptographic test vectors.

## Common encrypted envelope

Every encrypted object uses this JSON shape:

~~~json
{
  "iv": "<16 bytes, Base64>",
  "ciphertext": "<non-empty multiple of 16 bytes, Base64>",
  "mac": "<32 bytes, Base64>"
}
~~~

The envelope is an application implementation of A256CBC-HS512:

| Parameter | Value |
| --- | --- |
| Combined key | 64 bytes |
| Authentication key | First 32 bytes of the combined key |
| Encryption key | Last 32 bytes of the combined key |
| Encryption | AES-256-CBC with PKCS#7 padding |
| IV | Fresh random 16 bytes per encryption |
| Authentication | HMAC-SHA-512 |
| Stored tag | Leftmost 32 bytes of the 64-byte HMAC |

Let `AAD` be the associated data, `IV` the 16-byte IV, `C` the ciphertext, and `AL` the bit length of `AAD` encoded as an unsigned 64-bit big-endian integer. The tag is:

~~~text
MAC = leftmost-32(HMAC-SHA-512(K_auth, AAD || IV || C || AL))
~~~

A compatible reader MUST reject a null component, an IV not exactly 16 bytes, a MAC not exactly 32 bytes, or empty/non-block-aligned ciphertext. It MUST verify the MAC in constant time before attempting CBC decryption. Authentication, envelope-shape, and padding failures MUST NOT produce usable plaintext.

## Key hierarchy

### Vault root key and password key slot

Each vault has a random 32-byte root key. The root key is not derived from the password. The password derives a 64-byte wrapping key using Argon2id, and that wrapping key encrypts the root key inside `passwordKeySlot.rootKeyEnvelope`.

Passwords are encoded as strict UTF-8 without Unicode normalization. An empty password, an invalid UTF-16 sequence, or a password exceeding 1,024 encoded bytes is rejected. Compatible implementations MUST NOT normalize the password before derivation. The salt is exactly 16 random bytes.

The current recommended and accepted Argon2id parameters are:

| Parameter | Recommended | Accepted values |
| --- | ---: | ---: |
| Version | `19` (Argon2 v1.3 / `0x13`) | Exactly `19` |
| Memory | `65536` KiB | `19456`–`262144` KiB |
| Iterations | `3` | `2`–`10` |
| Parallelism | `4` | `1`–`16` |

Memory MUST also be at least 8 KiB per parallel lane. Parameter bounds are validated before the KDF is run.

### Per-purpose keys

Manifest, entry, and blob keys are independently derived from the 32-byte root key with HKDF-SHA-512, an empty salt, a 64-byte output, and the following `info` value:

~~~text
info = UTF8(label) || 0x00 || vaultId || optionalObjectId
~~~

GUID bytes in the key schedule are the 16-byte big-endian GUID representation.

| Protected object | Exact UTF-8 label | Object ID appended |
| --- | --- | --- |
| Manifest | `CRIPTY v1 manifest A256CBC-HS512` | No |
| Entry | `CRIPTY v1 entry A256CBC-HS512` | Entry ID |
| Blob | `CRIPTY v1 blob A256CBC-HS512` | Blob ID |

The password-derived wrapping key is used directly as the 64-byte combined key for the root-key envelope. It does not use the HKDF schedule above.

## Associated data

Every envelope is bound to its payload type, outer format version, vault, and—where applicable—object ID.

~~~text
AAD = UTF8("CRIPTY storage AAD")
      || 0x00
      || payloadType
      || int32be(formatVersion)
      || guidbe(vaultId)
      || optional guidbe(objectId)
~~~

| Payload | `payloadType` | Object ID |
| --- | ---: | --- |
| Wrapped root key | `1` | None |
| Manifest | `2` | None |
| Entry | `3` | Entry ID |
| Blob | `4` | Blob ID |

For format version `1`, manifest and root-key AAD are 40 bytes; entry and blob AAD are 56 bytes. A file copied to a different vault, object ID, payload type, or format-version context will not authenticate.

## `vault.cripty`

### Outer record

~~~json
{
  "formatVersion": 1,
  "vaultId": "b55f64f2-6d0b-4f32-a9b5-c60db44d57f1",
  "manifestGeneration": 7,
  "passwordKeySlot": {
    "kdfParameters": {
      "version": 19,
      "memorySizeKiB": 65536,
      "iterations": 3,
      "degreeOfParallelism": 4
    },
    "salt": "<16 bytes, Base64>",
    "rootKeyEnvelope": {
      "iv": "<16 bytes, Base64>",
      "ciphertext": "<encrypted 32-byte root key, Base64>",
      "mac": "<32 bytes, Base64>"
    }
  },
  "manifestEnvelope": {
    "iv": "<16 bytes, Base64>",
    "ciphertext": "<encrypted manifest JSON, Base64>",
    "mac": "<32 bytes, Base64>"
  }
}
~~~

| Property | Visibility | Requirement |
| --- | --- | --- |
| `formatVersion` | Plaintext | MUST equal `1` |
| `vaultId` | Plaintext | MUST be non-empty |
| `manifestGeneration` | Plaintext hint | Non-negative when present; MAY be `null` only for legacy files |
| `passwordKeySlot` | Plaintext parameters plus encrypted key | MUST be present and structurally valid |
| `manifestEnvelope` | Ciphertext | MUST be present |

`vaultId`, KDF parameters, salt, envelope lengths, and `manifestGeneration` are visible without the password. `manifestGeneration` is an operational hint, not an independent anti-rollback anchor. After decryption, a non-null outer generation MUST equal the authenticated manifest generation. The authenticated manifest is authoritative.

### Root-key open sequence

1. Validate the outer record and KDF bounds.
2. Derive the 64-byte wrapping key from the password, salt, and stored Argon2id parameters.
3. Authenticate and decrypt `rootKeyEnvelope` with root-key AAD.
4. Require exactly 32 plaintext root-key bytes.
5. Derive the manifest key using the HKDF schedule.
6. Authenticate and decrypt `manifestEnvelope` with manifest AAD.
7. Parse and validate the manifest DTO, then compare outer and protected values.

### Decrypted manifest payload

The current manifest schema is `3`:

~~~json
{
  "schemaVersion": 3,
  "vaultId": "b55f64f2-6d0b-4f32-a9b5-c60db44d57f1",
  "generation": 7,
  "folders": [
    {
      "folderId": "f2375978-21cc-48b5-b5a0-aa25d5c245b6",
      "name": "Accounts",
      "parentFolderId": null
    }
  ],
  "tags": [
    {
      "tagId": "0a12583d-41d2-4ad2-b751-a02527b34ed0",
      "name": "Important",
      "color": "#D97706"
    }
  ],
  "entries": [
    {
      "entryId": "a8c12b86-0790-4a57-889a-44e7c4df19b0",
      "name": "Example account",
      "folderId": "f2375978-21cc-48b5-b5a0-aa25d5c245b6",
      "tagIds": [
        "0a12583d-41d2-4ad2-b751-a02527b34ed0"
      ],
      "revision": 4,
      "createdUtc": "2026-08-20T09:12:00+00:00",
      "modifiedUtc": "2026-08-31T18:42:10+00:00",
      "timelineDateOverride": null
    }
  ],
  "sortPreferences": {
    "allEntriesSortMode": 6,
    "rootSortMode": 6,
    "folderSortModes": {
      "f2375978-21cc-48b5-b5a0-aa25d5c245b6": 0
    }
  }
}
~~~

#### Manifest members

| Member | Type | Rules |
| --- | --- | --- |
| `schemaVersion` | Integer | Readable range is `1`–`3`; current writers use `3` |
| `vaultId` | GUID | Non-empty and equal to the outer `vaultId` |
| `generation` | 64-bit integer | Non-negative |
| `folders` | Array | Required; folder IDs unique |
| `tags` | Array | Required; tag IDs and names unique |
| `entries` | Array | Required; entry IDs unique |
| `sortPreferences` | Object or null | Required for schema `3`; absent/null in schemas `1` and `2` defaults to `ModifiedNewest` |

#### Folder descriptor

| Member | Type | Rules |
| --- | --- | --- |
| `folderId` | GUID | Non-empty and unique |
| `name` | String | Non-empty/non-whitespace; unique among siblings, case-insensitively |
| `parentFolderId` | GUID or null | `null` means vault root; referenced parent MUST exist |

A folder MUST NOT parent itself, and the complete parent graph MUST be acyclic.

#### Tag descriptor

| Member | Type | Rules |
| --- | --- | --- |
| `tagId` | GUID | Non-empty and unique |
| `name` | String | Non-empty/non-whitespace; globally unique case-insensitively |
| `color` | String or null | Stored as supplied; no storage-layer color-format validation |

#### Entry descriptor

| Member | Type | Rules |
| --- | --- | --- |
| `entryId` | GUID | Non-empty and unique |
| `name` | String | Non-empty/non-whitespace |
| `folderId` | GUID or null | `null` means vault root; non-null folder MUST exist |
| `tagIds` | GUID array | Each ID non-empty, unique within the entry, and present in `tags` |
| `revision` | 64-bit integer | Non-negative; MUST equal the protected entry payload revision |
| `createdUtc` | Date-time string | Required, non-default, UTC offset |
| `modifiedUtc` | Date-time string | Required, non-default, UTC offset, not earlier than `createdUtc` |
| `timelineDateOverride` | Date or null | Optional on schema `1`; otherwise `YYYY-MM-DD` or null |

Entry names, folder names, tag names, relationships, timestamps, revisions, timeline dates, and sort preferences are encrypted because they exist only in the manifest envelope. Counts and ciphertext lengths may still leak approximate information.

#### Sort-mode numbers

| Number | Meaning |
| ---: | --- |
| `0` | Name ascending |
| `1` | Name descending |
| `2` | Created newest |
| `3` | Created oldest |
| `4` | Timeline newest |
| `5` | Timeline oldest |
| `6` | Modified newest |
| `7` | Modified oldest |

`allEntriesSortMode` and `rootSortMode` are required in schema `3`. Each `folderSortModes` key MUST identify an existing folder. Undefined numeric enum values are rejected. Missing per-folder preferences use `ModifiedNewest` without requiring a dictionary entry.

## Entry files

### Outer `.entry` record

~~~json
{
  "formatVersion": 1,
  "vaultId": "b55f64f2-6d0b-4f32-a9b5-c60db44d57f1",
  "entryId": "a8c12b86-0790-4a57-889a-44e7c4df19b0",
  "envelope": {
    "iv": "<16 bytes, Base64>",
    "ciphertext": "<encrypted entry JSON, Base64>",
    "mac": "<32 bytes, Base64>"
  }
}
~~~

`formatVersion` MUST equal `1`; both IDs MUST be non-empty. The outer `entryId` MUST match the filename. The vault ID and entry ID are inputs to both HKDF and AAD.

### Decrypted entry payload

~~~json
{
  "schemaVersion": 1,
  "entryId": "a8c12b86-0790-4a57-889a-44e7c4df19b0",
  "revision": 4,
  "fields": [
    {
      "fieldId": "640da846-bdb8-413a-9df2-dddd1529f6e0",
      "name": "Username",
      "type": "text",
      "data": {
        "text": "user@example.com"
      }
    },
    {
      "fieldId": "91c6fa27-c0ca-4ac3-8f92-b20fb7929efb",
      "name": "Recovery image",
      "type": "blob",
      "data": {
        "blobId": "be241a07-01ea-4eb2-b076-d326c06e9586",
        "fileName": "recovery.png",
        "contentType": "image/png",
        "length": 48321
      }
    }
  ]
}
~~~

| Member | Type | Rules |
| --- | --- | --- |
| `schemaVersion` | Integer | MUST equal `1` |
| `entryId` | GUID | Non-empty; MUST equal the outer `entryId` |
| `revision` | 64-bit integer | Non-negative; MUST equal its manifest descriptor revision |
| `fields` | Array | Required; field IDs unique |

Each field has a non-empty GUID `fieldId`, a non-whitespace `name`, an exact case-sensitive `type` discriminator, and a `data` object whose schema depends on `type`.

| `type` | `data` members | Validation |
| --- | --- | --- |
| `text` | `text: string` | `text` MUST NOT be null; empty text is valid |
| `blob` | `blobId: GUID`, `fileName: string`, `contentType: string or null`, `length: integer` | ID non-empty; filename non-whitespace; length non-negative |

Only `text` and `blob` are supported. Unknown discriminators are rejected rather than preserved. The `blob` value is a reference: actual bytes live in the corresponding `.blob` file.

## Blob files

### Outer `.blob` record

~~~json
{
  "formatVersion": 1,
  "vaultId": "b55f64f2-6d0b-4f32-a9b5-c60db44d57f1",
  "blobId": "be241a07-01ea-4eb2-b076-d326c06e9586",
  "envelope": {
    "iv": "<16 bytes, Base64>",
    "ciphertext": "<encrypted raw bytes, Base64>",
    "mac": "<32 bytes, Base64>"
  }
}
~~~

`formatVersion` MUST equal `1`; both IDs MUST be non-empty. The outer `blobId` MUST match the filename. The vault ID and blob ID are inputs to both HKDF and AAD.

The decrypted blob payload is the original raw byte sequence. It is not JSON and has no inner schema header. An empty blob is representable: PKCS#7 produces one ciphertext block. When a blob is reached through an entry field, Cripty also checks the field's expected length against the recovered byte length.

Blob plaintext is not Base64-encoded before encryption. The resulting ciphertext is stored as a Base64 string inside the JSON record, making the ciphertext portion approximately 33% larger on disk than its binary representation, in addition to PKCS#7 padding and fixed envelope/JSON overhead.

## Identity and consistency invariants

A compatible reader MUST enforce these cross-layer relationships before accepting content:

| Relationship | Required equality |
| --- | --- |
| Vault file ↔ manifest | Outer `vaultId` = protected `vaultId` |
| Vault hint ↔ manifest | Non-null outer `manifestGeneration` = protected `generation` |
| Entry filename ↔ entry outer record | Filename GUID = outer `entryId` |
| Entry outer record ↔ entry payload | Outer `entryId` = protected `entryId` |
| Entry file ↔ vault | Outer `vaultId` = active vault ID |
| Entry descriptor ↔ entry payload | Descriptor `revision` = protected `revision` |
| Blob filename ↔ blob outer record | Filename GUID = outer `blobId` |
| Blob file ↔ vault | Outer `vaultId` = active vault ID |
| Blob field ↔ recovered blob | Field `blobId` selects the file; field `length` = plaintext byte length |

Successful cryptographic authentication does not replace domain validation. A decrypted manifest with duplicate IDs, invalid references, cycles, unsupported sort values, bad timestamps, or negative generations/revisions is rejected.

## Generations, revisions, and save publication

The manifest `generation` identifies the logical manifest revision. Each entry has an independent `revision` repeated in its manifest descriptor and protected entry payload.

- A new vault begins at manifest generation `0`.
- A successfully saved changed entry advances that entry's revision by exactly one.
- A manifest-changing save advances the manifest generation by exactly one.
- Metadata-only changes can advance the manifest generation without changing an entry revision.
- Password rotation re-encrypts data and advances the manifest generation while retaining entry revisions.
- A legacy `vault.cripty` with `manifestGeneration: null` can be rewritten with the authenticated generation as its visible hint.

A Save publishes files in this order:

1. new or changed blob files;
2. new or changed entry files;
3. the new `vault.cripty` manifest generation;
4. deletion of obsolete entry and blob files.

Each individual file write uses a sibling temporary file named `<destination>.<random-guid-N>.tmp`, flushes it, requests a flush to disk, and then replaces or moves it into place. This is per-file atomicity; the set of files changed by one Save is not a single filesystem transaction. Recovery tools MUST expect crash remnants or a descriptor/entry revision mismatch after an interrupted multi-file save and MUST NOT silently select whichever value is newer.

Generation and revision numbers detect internal mismatches, but they are not externally anchored freshness proofs. A complete, internally consistent older snapshot can still authenticate.

## Backup bundle format

Exports are directories whose names end in `.cripty-backup`. The directory name is for display and discovery; `backup-index.json` defines the bundle.

~~~text
<name>.cripty-backup/
├── backup-index.json
└── vault/
    ├── vault.cripty
    ├── entries/
    │   └── <entry-id>.entry
    └── blobs/
        └── <blob-id>.blob
~~~

The `vault/` subtree is a byte-for-byte snapshot of supported live-vault files. `backup-index.json` is plaintext operational metadata:

~~~json
{
  "formatVersion": 1,
  "createdUtc": "2026-08-31T18:45:00+00:00",
  "vaultName": "Personal",
  "vaultId": "b55f64f2-6d0b-4f32-a9b5-c60db44d57f1",
  "manifestGeneration": 7,
  "isRecoveryBackup": false,
  "files": [
    {
      "relativePath": "blobs/be241a07-01ea-4eb2-b076-d326c06e9586.blob",
      "length": 65012,
      "sha256": "<64 uppercase hexadecimal characters>"
    },
    {
      "relativePath": "entries/a8c12b86-0790-4a57-889a-44e7c4df19b0.entry",
      "length": 1471,
      "sha256": "<64 uppercase hexadecimal characters>"
    },
    {
      "relativePath": "vault.cripty",
      "length": 2098,
      "sha256": "<64 uppercase hexadecimal characters>"
    }
  ]
}
~~~

### Backup-index rules

- `formatVersion` MUST equal `1`.
- `vaultId` MUST be non-empty and match the copied `vault.cripty` outer record.
- `vaultName` MUST be non-whitespace.
- `manifestGeneration`, when present, MUST be non-negative and match copied `vault.cripty`.
- `files` MUST be non-empty, contain unique case-sensitive paths, and include `vault.cripty`.
- Each `length` MUST be non-negative and equal the copied file length.
- Each `sha256` MUST be exactly 64 hexadecimal characters. Current writers emit uppercase with `Convert.ToHexString`; readers compare case-insensitively.
- Actual payload files MUST exactly equal the indexed path set.

Only these relative path shapes are accepted:

~~~text
vault.cripty
entries/<single-name>.entry
blobs/<single-name>.blob
~~~

Paths use `/`, cannot contain `\`, and cannot add nested directories below `entries/` or `blobs/`. Entry and blob basenames must parse as GUIDs when the snapshot is validated. Import resolves every path beneath the payload directory and rejects escapes.

The SHA-256 list detects incomplete synchronization and accidental or malicious file replacement before import, but it is not keyed or signed. It does not add authenticity beyond the encrypted file formats, and the plaintext index leaks the vault name, vault ID, generation hint, backup time, recovery status, file names, file count, lengths, and hashes.

## Compatibility and evolution

### Current read matrix

| Record | Accepted versions |
| --- | --- |
| `vault.cripty` outer format | Exactly `1` |
| Entry outer format | Exactly `1` |
| Blob outer format | Exactly `1` |
| Manifest protected schema | `1`, `2`, or `3` |
| Entry protected schema | Exactly `1` |
| Backup index format | Exactly `1` |

Manifest schemas `1` and `2` may omit `sortPreferences`; readers construct default `ModifiedNewest` preferences. `timelineDateOverride` is optional for backward-compatible schema-1 reads. Schema `3` requires `sortPreferences`.

### Rules for future changes

Maintainers SHOULD follow these rules when evolving the format:

1. Increment an outer `formatVersion` when the container, key derivation, AAD, envelope algorithm, or outer interpretation changes.
2. Increment the manifest or entry `schemaVersion` when protected DTO semantics change without changing the outer encryption container.
3. Never reuse an existing numeric version or field discriminator with different semantics.
4. Add an explicit migration and fixture tests before dropping a readable version.
5. Keep old AAD construction and key-schedule labels available for as long as that outer version remains readable.
6. Treat IDs, version fields, KDF parameters, lengths, and all deserialized data as untrusted until validated.
7. Decide explicitly whether unknown fields and field-value discriminators are rejected, ignored, or preserved. The current field mapper rejects unknown `type` values.
8. Preserve a recoverable source snapshot until a migrated vault has been written, reopened, authenticated, and validated.

Changing JSON property order or indentation alone does not require a version bump. Changing property names, requiredness, enum numbers, GUID byte order in cryptographic inputs, labels, or null semantics can break compatibility and MUST be treated as a format or schema change.

## Minimum reader checklist

A standalone compatible reader or recovery tool should, at minimum:

1. Resolve only the documented paths and compare filename IDs with outer IDs.
2. Parse JSON with bounded resource use and validate required structures.
3. Reject unsupported outer versions before cryptographic work.
4. Validate Argon2id bounds before deriving the wrapping key.
5. Reproduce strict UTF-8 password encoding, HKDF labels, big-endian GUID encoding, AAD bytes, and the CBC/HMAC construction exactly.
6. Authenticate before decrypting and avoid distinguishing MAC from padding failures to callers.
7. Compare outer and protected vault/entry identities and manifest generation.
8. Validate supported protected schema versions and every domain invariant.
9. Match every manifest descriptor to its entry file and revision, and every blob reference to its blob ID and expected length.
10. Report incomplete, ambiguous, or inconsistent state; do not repair it by silently discarding authenticated data.

## Test-fixture recommendation

Before another implementation claims compatibility, the project should commit non-secret deterministic fixtures or vector-generation tests covering:

- password-to-wrapping-key derivation;
- HKDF output for manifest, entry, and blob contexts;
- exact AAD byte strings for all four payload types;
- envelope MAC calculation and successful decryption;
- wrong password, wrong vault ID, wrong object ID, tampered ciphertext, and malformed envelope rejection;
- each readable manifest schema and the current entry schema;
- filename/outer/protected ID mismatch rejection;
- descriptor/entry revision and blob-length mismatch rejection;
- complete and incomplete backup-index validation.

The current repository contains component and workflow tests, but this document is not itself a substitute for published cross-implementation test vectors or an independent security review.
