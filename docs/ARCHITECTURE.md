# Cripty architecture

This document describes the architecture implemented on the repository's `master` branch at commit `9d7074e`. It focuses on component boundaries, runtime data flow, persistence behavior, and the design decisions that future changes must preserve.

Cripty is a layered desktop application rather than a network service. A vault is a directory of authenticated encrypted files, and the application process is the only component that turns those files into domain objects and UI state.

For algorithms, trust assumptions, and protected assets, see [SECURITY-MODEL.md](SECURITY-MODEL.md). For accepted debt and current constraints, see [KNOWN-LIMITATIONS.md](KNOWN-LIMITATIONS.md).

## Architectural goals

The current design is intended to provide:

- compiler-enforced separation between UI, application workflows, domain logic, cryptography, and persistence;
- encrypted persistence of all user-facing vault content;
- independent entry and blob files so a small edit does not rewrite the complete vault;
- explicit, versioned serialization boundaries;
- staged editing with predictable save, revert, and lock behavior;
- failure-path testability for corrupted, incomplete, or maliciously modified files;
- room to add new field-value types without redesigning the domain or storage model.

## Solution structure

~~~mermaid
flowchart TD
    UI["Cripty<br/>Avalonia UI and desktop services"]
    APP["Cripty.Application<br/>Vault workflows and sessions"]
    CORE["Cripty.Core<br/>Domain model"]
    STORE["Cripty.Storage<br/>Formats and persistence"]
    CRYPTO["Cripty.Cryptography<br/>Cryptographic operations"]

    UI --> APP
    UI --> CORE
    UI --> CRYPTO
    APP --> CORE
    APP --> STORE
    APP --> CRYPTO
    STORE --> CORE
    STORE --> CRYPTO
~~~

This is a modular, layered desktop application. It resembles Clean Architecture in its separation of concerns, but it should not be described as a strict implementation of a named architecture: the UI intentionally references both the domain and cryptography projects for UI-facing domain types, password generation, and TOTP generation.

| Project | Responsibility | Important types |
| --- | --- | --- |
| `Cripty` | Avalonia views, MVVM view models, desktop interaction, clipboard and image integration, vault discovery, and application navigation | `MainViewModel`, `MainVaultViewModel`, `EntryEditorViewModel`, `VaultSelectionViewModel` |
| `Cripty.Application` | Stateful vault sessions and multi-step workflows that combine domain, storage, and cryptography | `VaultSession`, `VaultBackupService`, `VaultCopyService`, `VaultKeyRotationService` |
| `Cripty.Core` | Persistence-independent domain objects, validation rules, hierarchy operations, indexing, and sorting preferences | `VaultManifest`, `VaultIndex`, `VaultEntry`, descriptors and field values |
| `Cripty.Cryptography` | Password-based key derivation, key separation, authenticated encryption, password generation, and TOTP | `PasswordWrappingKeyDeriver`, `HkdfKeySchedule`, `A256CbcHs512Cipher`, `TotpGenerator` |
| `Cripty.Storage` | Versioned outer formats, DTOs, domain mapping, associated data, codecs, validators, and atomic single-file stores | `VaultFileCodec`, `EntryFileCodec`, `BlobFileCodec`, file stores and DTO mappers |
| `*.Tests` | Cryptographic, storage, application-workflow, and UI-facing behavioral tests | Four MSTest projects |

## Dependency rules

The dependency direction is deliberate:

1. **Core has no project dependencies.** It defines the domain without knowing about encryption, JSON, the filesystem, or Avalonia.
2. **Cryptography is independent of the domain and storage projects.** It owns primitive operations and key schedules.
3. **Storage depends on Core and Cryptography.** It translates domain objects into versioned DTOs, encrypts them, and persists outer envelopes.
4. **Application depends on Core, Cryptography, and Storage.** It owns workflows that require all three.
5. **The desktop project composes the application.** It turns user actions into application-service calls and maps results back into view-model state.

New code should remain in the lowest layer that has enough information to implement it. For example:

- a folder-name invariant belongs in Core;
- an envelope-shape check belongs in Cryptography or Storage;
- a multi-step backup or key-rotation workflow belongs in Application;
- clipboard and window behavior belong in the desktop project.

## Domain model

### Manifest and descriptors

The encrypted manifest is the authoritative catalogue of a vault. It contains:

- the stable vault ID and manifest generation;
- folders and their parent relationships;
- tags;
- entry descriptors;
- entry-to-folder and entry-to-tag relationships;
- entry revisions and timestamps;
- timeline-date overrides;
- per-view sorting preferences.

An `EntryDescriptor` deliberately contains searchable and organizational metadata but not field content. The actual `VaultEntry` is loaded from its own encrypted entry file.

This separation allows Cripty to display and filter an unlocked vault without decrypting every entry immediately.

### Stable identities

Vaults, folders, tags, entries, fields, and blobs use GUID identities. Names are editable; identities are not. This prevents renaming or moving an object from changing its storage identity and makes relationships explicit.

The manifest validates references and uniqueness rules before it is accepted. Examples include:

- every referenced folder and tag must exist;
- duplicate tag assignments are rejected;
- folder-name uniqueness is enforced within a parent;
- hierarchy operations cannot introduce invalid parent relationships;
- entry revisions in descriptors and entry files must agree.

### Indexing

`VaultIndex` is an in-memory reverse index built from the manifest. It accelerates folder and tag queries without becoming a second persisted source of truth. Whenever a hierarchy or tag operation changes the relevant relationships, the session rebuilds the index.

### Entry field values

`EntryFieldValue` is an extensible discriminated model:

- `TextFieldValue` stores text-based fields;
- `BlobFieldValue` references a separately encrypted blob and records its ID, display filename, content type, and expected length.

Storage uses a mapper per field-value type. Adding another value type should be additive: introduce the domain value, DTO, mapper, validation, codec registration, and UI behavior without changing existing serialized cases.

## Persistence model

### Vault directory

| Path | Contents |
| --- | --- |
| `vault.cripty` | Outer vault metadata, password key slot, and encrypted manifest envelope |
| `entries/<entry-id>.entry` | One outer entry record and authenticated encrypted entry DTO |
| `blobs/<blob-id>.blob` | One outer blob record and authenticated encrypted binary payload |

Outer files are JSON records. Byte arrays are serialized by the .NET JSON serializer, while sensitive payloads remain ciphertext inside their envelopes.

### Vault file

`vault.cripty` contains:

- storage-format version;
- vault ID;
- a non-secret manifest-generation hint;
- Argon2id parameters and salt;
- the authenticated encrypted vault-root-key envelope;
- the authenticated encrypted manifest envelope.

The generation hint allows locked-vault and backup screens to compare snapshots without decrypting them. After unlock, the codec verifies it against the authenticated manifest and treats the encrypted manifest as authoritative.

### Entry and blob files

Each entry file exposes only its format version, vault ID, entry ID, and encrypted envelope. Each blob file has the equivalent structure for a blob ID.

The IDs are repeated inside authenticated content or associated data. A renamed, misplaced, or substituted file is rejected when its outer identity, protected identity, vault identity, revision, or authentication tag does not match expectations.

### Schema versions

Storage-format versions and protected-payload schema versions are separate:

- outer vault, entry, and blob formats control envelope parsing;
- manifest and entry schema versions control DTO/domain interpretation;
- supported versions are centralized in `StorageSchemaVersions` and codec validators.

This prevents an outer-format change from being confused with a domain-schema migration.

## Vault lifecycle

### Startup and discovery

1. `VaultLocationService` resolves the default vault root under `Documents/Cripty Vaults` or a previously selected custom path.
2. `VaultDiscoveryService` finds vault directories.
3. The selection screen reads only visible outer metadata until the user chooses a vault and submits its password.

The small application settings file stores selected filesystem locations, not vault passwords or decrypted vault content.

### Vault creation

1. Validate the vault name and destination.
2. Create an empty manifest with a new vault ID and generation zero.
3. Generate a random 256-bit root key.
4. Derive the password-wrapping key using Argon2id.
5. Encrypt and authenticate the root key and manifest.
6. Persist `vault.cripty`.
7. Retain the root key only in the active `VaultSession`.

### Unlock

1. Read and structurally validate `vault.cripty`.
2. Validate the stored Argon2id parameter bounds before performing expensive work.
3. Derive the wrapping key from the supplied password.
4. Authenticate and decrypt the root key.
5. Derive the manifest key, then authenticate and decrypt the manifest.
6. Map the DTO into domain objects and validate all relationships.
7. Verify visible vault metadata against authenticated manifest data.
8. Create the active session and its in-memory index.

Failure is deliberately collapsed into user-facing categories such as incorrect password or damaged vault data.

### Entry editing

Only the selected entry is opened and decrypted on demand. View models hold editable working state while `VaultSession` tracks:

- pending entry changes;
- pending blob writes;
- reversible entry deletions;
- manifest-only metadata changes;
- encrypted files awaiting post-commit cleanup.

UI changes are staged until Save. Revert reconstructs the working entry from the last persisted version.

### Save pipeline

~~~mermaid
flowchart TD
    B["Write pending encrypted blobs"]
    E["Write pending encrypted entries"]
    M["Write encrypted manifest"]
    D["Delete obsolete encrypted files"]

    B --> E
    E --> M
    M --> D
~~~

The order protects referential integrity:

1. New blob files are encrypted and atomically written.
2. Entry files that reference those blobs are encrypted and atomically written.
3. Entry revisions are advanced in memory only after their files succeed.
4. The manifest is advanced to a new generation and atomically written only after every required entry file succeeds.
5. Obsolete entry and blob files are deleted after the new manifest is committed.

If an entry file succeeds but the manifest fails, the session enters a save-retry state and rejects further mutation until Save is retried. This prevents the in-memory session from silently building on a partially committed revision.

Atomicity is per file, not across the whole directory. That accepted limitation is described in [KNOWN-LIMITATIONS.md](KNOWN-LIMITATIONS.md).

### Lock and disposal

Manual lock disposes the session and returns to vault selection without implicitly saving. The inactivity policy also discards unsaved state; after five minutes without keyboard, click, or scroll interaction, it closes Cripty following a one-minute warning period.

Disposal clears the root-key byte array and owned sensitive byte buffers. Managed strings and operating-system memory behavior remain subject to the limitations documented elsewhere.

## Password rotation

Password change is implemented as full root-key rotation:

1. Refuse rotation while unsaved changes exist.
2. Create a sibling staging directory.
3. Generate a fresh root key.
4. Build a new password key slot.
5. Re-encrypt the manifest, every entry, and every referenced blob.
6. Open and validate the staged vault, including counts, identities, revisions, and blob lengths.
7. Move the current vault to a sibling rollback directory.
8. Move the staged vault into the original path.
9. Restore the rollback directory if publication fails.
10. Delete the rollback directory on success.

Cancellation is honored during staging and verification. Once the two-directory publication begins, cancellation is intentionally ignored until the new vault is published or the old vault is restored.

## Backup and import

### Export

Export operates on locked, already-encrypted vault files:

1. Enumerate `vault.cripty` plus top-level `.entry` and `.blob` payload files.
2. Copy every payload into a temporary backup directory while computing its length and SHA-256 hash.
3. Write a plaintext operational index.
4. Validate the complete temporary backup.
5. Atomically rename the temporary directory to its final `.cripty-backup` name.

The backup root cannot be the vault itself or a descendant of it.

### Import

1. Validate the index format, paths, file set, lengths, hashes, and visible vault identity.
2. Compare vault ID and generation with any existing local vault.
3. Return immediately if the snapshots are identical.
4. Ask for confirmation when importing a different version of an existing vault.
5. Export a complete encrypted recovery snapshot of the current version.
6. Copy the imported payload into a staging directory and verify it again.
7. Swap directories, restoring the original on publication failure.

Imports replace complete snapshots. They do not perform field-, entry-, or manifest-level merges.

## Cross-vault copy

Cross-vault copy is an application workflow rather than a raw filesystem copy:

- the source vault is already open;
- the destination vault is authenticated with its own password;
- required folder paths and tags are recreated or resolved;
- entries and image blobs are read through the source codecs;
- new destination objects are written under the destination vault's key hierarchy;
- the source vault remains unchanged.

This preserves encryption separation between vaults and avoids copying ciphertext whose associated vault and object IDs would not authenticate in the destination.

## UI composition

The desktop application uses Avalonia and CommunityToolkit.Mvvm:

- XAML views contain layout and platform event bridges;
- view models own display state and commands;
- `MainViewModel` handles page navigation and session lifetime;
- `MainVaultViewModel` coordinates vault-level commands;
- `EntryEditorViewModel` owns one editable entry;
- code-behind is retained for inherently platform/UI-bound work such as clipboard access, bitmap encoding, escape-key handling, and auxiliary windows.

The application does not use dependency-injection infrastructure. Services are composed directly, with constructor seams or injected delegates where tests need to substitute behavior.

## Concurrency model

`VaultSession` uses an in-process `SemaphoreSlim` gate to serialize save, open, and mutation-sensitive operations. Synchronous mutation attempts fail if another vault operation is active.

This protects one session from its own concurrent commands. It is not a cross-process lock and does not coordinate two Cripty processes or another program writing the same vault directory.

## Testing strategy

The solution separates tests by architectural layer:

- **Cryptography tests:** key generation and separation, KDF validation, authenticated-encryption tamper cases, password generation, and TOTP vectors.
- **Storage tests:** codecs, malformed envelopes, identity binding, schema validation, atomic stores, blobs, entries, vault files, and backup indexes.
- **Application tests:** session state, save ordering and retry behavior, backup/import, cross-vault copy, and key rotation.
- **Desktop tests:** inactivity policy, limited formatting, password UI logic, rename/copy/sort/timeline behavior, and entry-field state.

Security-critical changes should include both success-path tests and negative tests for wrong identities, wrong keys, malformed lengths, unsupported versions, tampering, partial failure, and cancellation.

## Extension guidelines

### Add a field-value type

1. Add a new `EntryFieldValue` subtype in Core.
2. Add a versioned DTO in Storage.
3. Implement and register an `IEntryFieldValueMapper`.
4. Extend entry validation.
5. Add storage round-trip and rejection tests.
6. Add editor/viewer behavior in the desktop project.

Existing DTO cases and serialized type identifiers must remain readable.

### Change a persisted format

1. Decide whether the change affects the outer format or protected schema.
2. Increment the appropriate version.
3. Preserve explicit bounds and shape validation before allocation or cryptographic work.
4. Add migration or backward-reading behavior where intended.
5. Add tests using committed old-format fixtures.
6. Document metadata or threat-model changes in `SECURITY-MODEL.md`.

### Add a vault-wide workflow

Workflows that rewrite or copy several files should use a sibling staging directory, complete validation before publication, a recoverable directory swap, cancellation boundaries, progress reporting, and explicit cleanup behavior.

## Architectural invariants

Future work should preserve these invariants:

- user-facing vault content is encrypted before filesystem persistence;
- protected payloads are authenticated before deserialization or use;
- manifest, entry, and blob keys remain domain-separated;
- object identity and storage context are authenticated;
- the encrypted manifest remains the authority over visible hints and indexes;
- the manifest never commits references to entry or blob files that were not written successfully;
- physical deletion occurs only after the manifest stops referencing the object;
- session disposal clears owned secret byte buffers;
- backup and rotation staging is validated before publication;
- Core remains independent of UI, storage, and cryptography.
