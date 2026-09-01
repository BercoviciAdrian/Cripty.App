# Cripty security model

This document defines what Cripty is intended to protect, the assumptions on which that protection depends, and the cryptographic mechanisms implemented on `master` at commit `9d7074e`.

It is a design description, not a security certification. Cripty has not undergone an independent security audit, formal verification, or broad production hardening.

See [ARCHITECTURE.md](ARCHITECTURE.md) for component and persistence design. See [KNOWN-LIMITATIONS.md](KNOWN-LIMITATIONS.md) for residual risks and accepted debt.

## Security objective

Cripty's primary objective is to protect vault content **at rest** when an adversary obtains or modifies a copy of the vault directory or an exported backup but does not know the master password.

The application aims to provide:

| Goal | Intended property |
| --- | --- |
| Confidentiality | Folder names, tags, entry names, field names, field values, TOTP provisioning URIs, and image bytes are not readable from persisted vault payloads without the root key |
| Integrity and authenticity | Modification, substitution, truncation, wrong-key use, and most forms of corruption are detected before protected plaintext is used |
| Key separation | Compromise or misuse of one derived object key does not make it the correct key for a different vault, payload type, entry, or blob |
| Password hardening | Offline password guesses require a bounded Argon2id computation per attempt |
| Failure containment | New encrypted files are completely written before they replace existing individual files |
| Recoverable publication | Password rotation and snapshot import stage and validate complete directories before publishing them |

Cripty does **not** currently provide a trusted freshness guarantee against replay of a complete older authenticated vault snapshot.

## Protected assets

The security model treats the following as sensitive:

- the master password;
- the random vault root key;
- derived encryption and authentication keys;
- folder and tag names and hierarchy;
- entry names, relationships, revisions, and timestamps;
- field names and field values;
- TOTP provisioning URIs and decoded TOTP secrets;
- generated TOTP codes;
- image plaintext;
- decrypted DTOs and domain objects while in use.

## Trust boundaries

~~~mermaid
flowchart TD
    USER["User input and display"]
    APP["Trusted Cripty process"]
    VAULT["Local vault directory"]
    BACKUP["Backup/synchronized folder"]
    CLOUD["External sync client and cloud"]

    USER <--> APP
    APP <--> VAULT
    APP --> BACKUP
    BACKUP --> CLOUD
~~~

### Trusted

The model assumes:

- the operating system, .NET runtime, Avalonia runtime, and cryptographic providers behave correctly;
- the Cripty process and loaded assemblies have not been modified;
- user input reaches the intended application rather than a keylogger or overlay;
- the display shows the intended application rather than a malicious imitation;
- the process has ordinary user permissions limited to files that user may access;
- the master password has sufficient entropy and is not reused from a compromised source.

### Untrusted

The application treats these as attacker-controlled when reading them:

- vault and backup JSON;
- format and schema versions;
- stored KDF parameters;
- salts, IVs, ciphertexts, authentication tags, IDs, lengths, and paths;
- manifest, entry, and blob DTO fields after decryption but before validation;
- backup indexes and synchronized snapshot contents.

Bounds, shapes, identities, and relationships are validated before the corresponding data is trusted.

## Attacker profiles

### Offline vault reader

The attacker can copy `vault.cripty`, entry files, blob files, or a complete backup and perform unlimited offline computation.

Expected protection:

- vault content remains confidential unless the attacker guesses the master password or breaks the cryptographic assumptions;
- every password guess requires Argon2id with the parameters stored in the vault;
- salts prevent precomputation from being directly reusable across vaults.

### Offline vault modifier

The attacker can alter, delete, insert, rename, reorder, or substitute individual files and serialized fields.

Expected protection:

- encrypted payload tampering fails authentication;
- payloads moved between vaults, entries, blobs, or format contexts fail because IDs and context are bound through derived keys and associated data;
- outer and protected identities are compared;
- descriptor and entry revisions must agree;
- malformed shapes and unsupported versions are rejected.

Deletion remains an availability attack: cryptography cannot restore a file the attacker removed.

### Backup or cloud observer

The observer can see, retain, copy, delete, or replace exported backup files and their plaintext backup index.

Expected protection:

- the provider receives already-encrypted vault payloads;
- content remains confidential without the master password;
- file-level corruption is detected by the authenticated vault formats and backup verification.

Not provided:

- concealment of backup names, timestamps, IDs, generations, file paths, counts, sizes, and hashes;
- trusted snapshot freshness on a new device;
- resistance to deletion or denial of service by the provider.

### Whole-snapshot replay attacker

The attacker replaces every vault file with an internally consistent older snapshot.

Current result:

- the snapshot still authenticates;
- a device that retains a newer trusted generation may warn during import;
- a new device, or one whose only state is the replayed vault, has no external trust anchor with which to prove that the snapshot is old.

This is a known integrity/freshness limitation, not a confidentiality break.

### Compromised host

Malware, an administrator, a debugger, or another process controlling the host while the vault is unlocked can observe input, UI, clipboard data, process memory, or decrypted files as they are used.

This attacker is outside the primary model. Cripty does not claim to remain secure after host compromise.

## Cryptographic key hierarchy

~~~mermaid
flowchart TD
    P["Master password"]
    KDF["Argon2id<br/>salt and validated parameters"]
    WRAP["512-bit wrapping key"]
    ROOT["Random 256-bit vault root key"]
    HKDF["HKDF-SHA-512"]
    MAN["512-bit manifest key"]
    ENTRY["512-bit per-entry keys"]
    BLOB["512-bit per-blob keys"]

    P --> KDF
    KDF --> WRAP
    WRAP --> ROOT
    ROOT --> HKDF
    HKDF --> MAN
    HKDF --> ENTRY
    HKDF --> BLOB
~~~

The wrapping-key arrow represents authenticated decryption of the stored root-key envelope. The root key is generated randomly; it is not derived from the password.

### Master password and Argon2id

`PasswordWrappingKeyDeriver`:

- encodes the password as strict UTF-8;
- rejects an empty password;
- rejects passwords longer than 1,024 encoded bytes;
- uses a random 16-byte salt;
- derives a 64-byte wrapping key;
- clears temporary password bytes, salt copies, and derived-key copies.

Current recommended parameters:

| Parameter | Default | Accepted range |
| --- | ---: | ---: |
| Argon2 version | 1.3 / decimal 19 | Exactly version 1.3 |
| Memory | 64 MiB | 19–256 MiB |
| Iterations | 3 | 2–10 |
| Parallelism | 4 lanes | 1–16 lanes |

Stored parameters are validated before derivation. The lower bounds reject weak imported configurations; upper bounds limit denial-of-service through maliciously expensive vault files.

The parameters are configurable at vault creation and full password rotation. They are not automatically calibrated to the current machine.

### Vault root key

The root key is 32 random bytes generated with `.NET RandomNumberGenerator`. It is stored only inside an authenticated encrypted envelope protected by the password-derived 64-byte wrapping key.

While a vault is unlocked, an owned root-key byte array remains in `VaultSession`. Session disposal clears that array.

### HKDF key separation

`HkdfKeySchedule` uses HKDF-SHA-512 with the 32-byte root key as input keying material and an empty salt. It derives 64-byte combined encryption/authentication keys using purpose-specific `info` values:

- manifest: purpose label + vault ID;
- entry: purpose label + vault ID + entry ID;
- blob: purpose label + vault ID + blob ID.

Purpose labels include the Cripty versioned context and algorithm name. GUIDs are encoded big-endian. This means:

- a manifest key is not an entry or blob key;
- an entry key is specific to its vault and entry ID;
- a blob key is specific to its vault and blob ID;
- ciphertext copied raw between those contexts will not authenticate.

## Authenticated-encryption construction

`A256CbcHs512Cipher` implements an A256CBC-HS512 encrypt-then-MAC construction.

| Component | Implementation |
| --- | --- |
| Combined key | 64 bytes |
| Authentication key | First 32 bytes |
| Encryption key | Last 32 bytes |
| Encryption | AES-256-CBC with PKCS#7 padding |
| IV | Fresh random 16 bytes per encryption |
| Authentication | HMAC-SHA-512 |
| Stored tag | Leftmost 32 bytes of the 64-byte HMAC |
| Tag comparison | `CryptographicOperations.FixedTimeEquals` |

The HMAC input is:

`associatedData || IV || ciphertext || AL`

where `AL` is the associated-data length in bits encoded as an unsigned 64-bit big-endian integer.

Decryption order is security-critical:

1. Validate key and envelope lengths.
2. Recompute the expected authentication tag.
3. Compare tags in fixed time.
4. Return failure without decrypting if authentication fails.
5. Only then perform CBC decryption and PKCS#7 unpadding.
6. Collapse cryptographic and padding failures into a generic authentication/decryption failure.

This ordering prevents a padding-oracle interface in the cipher API.

The construction uses established primitives and a recognized encrypt-then-MAC layout, but its implementation, key schedule, serialization, and application-specific associated data remain custom application code. They have not been independently audited.

## Authenticated associated data

Associated data is not secret. It prevents a valid encrypted envelope from being accepted in the wrong storage context.

The encoding is:

`"CRIPTY storage AAD" || 0x00 || payloadType || formatVersion || vaultId || optional objectId`

with:

- a distinct byte for root-key, manifest, entry, and blob payloads;
- a signed 32-bit format version encoded big-endian;
- GUIDs encoded big-endian;
- entry or blob ID included when applicable.

Consequently, changes to the visible vault ID, object ID, payload type, or format version invalidate authentication even if the ciphertext, IV, and tag are copied unchanged.

## Protected content and visible metadata

### Encrypted and authenticated

| Payload | Protected contents |
| --- | --- |
| Root-key envelope | 256-bit vault root key |
| Manifest envelope | Folder/tag names and hierarchy, entry names and relationships, tag assignments, revisions, timestamps, timeline overrides, and sort preferences |
| Entry envelope | Entry schema and ID, field IDs, field names, text values, and blob references including original display metadata |
| Blob envelope | PNG image bytes |

### Visible without unlocking

| Location | Visible data |
| --- | --- |
| Vault directory | Vault folder name and location |
| `vault.cripty` | Outer format version, vault ID, manifest-generation hint, KDF parameters, salt, IVs, ciphertext lengths, and tags |
| `entries` and `blobs` | Object-ID filenames, file counts, sizes, and filesystem timestamps |
| Backup directory/index | Vault name, vault ID, generation hint, backup timestamp, recovery marker, relative paths, lengths, and SHA-256 hashes |
| Application settings | Selected vault-root and backup-root paths |

Cripty does not claim metadata-hiding or traffic-analysis resistance.

## Vault operations

### Creation

Creation generates a new vault ID, empty manifest, root key, and salt. The password-derived wrapping key encrypts the root key; the root key hierarchy encrypts the manifest. Only authenticated encrypted vault data is written.

### Unlock

Unlock validates outer structure and KDF bounds, derives the wrapping key, authenticates and unwraps the root key, derives the manifest key, authenticates and decrypts the manifest, deserializes it, validates domain relationships, and checks the visible generation hint.

The user-facing error does not distinguish a wrong password from damaged or unauthentic vault data.

### Entry and blob access

Entries are decrypted on demand. The manifest descriptor identifies the expected entry and revision. Entry and blob codecs authenticate before deserialization or use and validate that protected IDs match outer IDs.

Image bytes are held in owned buffers and cleared when their lifetime ends where the code controls the byte array.

### Save

New encrypted blobs are written first, then the entries that reference them, then the new manifest generation. Obsolete encrypted files are deleted only after the new manifest commits.

Every individual file is written through a same-directory temporary file, flushed to disk, and moved or replaced atomically. The complete set of files is not one transaction.

### Password rotation

Changing a password:

- requires pending edits to be saved first;
- generates a fresh root key;
- re-encrypts the manifest, every entry, and every referenced blob into a sibling staging directory;
- validates the complete staged vault with the new password and root key;
- swaps directories with rollback restoration on publication failure.

It therefore replaces both the password-derived wrapping key and all content-encryption keys.

Because rotation generates a new root key and re-encrypts every protected payload, an attacker who later recovers the old root key—for example, by cracking a weaker password protecting an older snapshot—cannot use that key to decrypt the rotated vault. The old key can still decrypt any retained snapshots encrypted under it.

### Backup export and import

Export copies already-encrypted vault payloads and hashes them into a temporary snapshot before publishing the backup directory.

Import validates:

- supported index version;
- allowed relative paths;
- unique indexed paths;
- exact file set;
- lengths and SHA-256 hashes;
- visible vault ID and generation;
- outer vault, entry, and blob structures and identities.

Before replacing a different local version, Cripty exports a complete encrypted recovery backup. Import still provides snapshot integrity, not an externally anchored freshness guarantee.

## Secret handling in memory

Cripty takes best-effort measures within managed .NET:

- stack or owned byte buffers are used for keys where practical;
- root keys, derived keys, decoded TOTP secrets, decrypted serialized payloads, and image byte buffers are cleared when their controlled lifetime ends;
- session disposal clears the active root key;
- authentication tags are compared in fixed time.

Limits remain:

- editable field values and passwords are represented by managed strings in the UI;
- immutable strings cannot be reliably overwritten;
- garbage collection can copy or retain objects;
- the runtime and OS may page memory, create crash dumps, hibernate, or retain graphics/clipboard buffers;
- zeroing an application-owned buffer does not prove that no prior copy exists elsewhere.

The application therefore does not claim secure-memory guarantees.

## Clipboard and display

Copy commands intentionally place plaintext field values or current TOTP codes on the operating-system clipboard. Clipboard managers, remote-desktop software, accessibility tools, and other applications may observe or retain them.

Cripty currently does not provide a reliable cross-platform guarantee that copied text is cleared after a timeout or cleared only if still owned by Cripty.

Values shown on screen are likewise available to screen capture, shoulder surfing, and software with display access.

## Inactivity behavior

While a vault is open, keyboard input, text input, pointer presses, and pointer-wheel events reset a five-minute inactivity timer. Pointer movement alone does not.

During the final 20% of the timeout, the UI displays a countdown. Expiration:

- stops the active session;
- clears owned session key material;
- discards unsaved changes;
- closes Cripty.

This reduces unattended unlocked-session time. It does not defend against a process or host already compromised while the vault is open.

## Password and TOTP utilities

The password generator uses `RandomNumberGenerator.GetInt32` to select characters from a chosen alphabet and rounds the requested entropy target up to a complete character count. Generation is local.

The TOTP generator:

- accepts `otpauth://totp/` provisioning URIs;
- validates and decodes Base32 secrets;
- supports the implemented hash, digit, and period options;
- calculates codes locally;
- clears decoded secret and HMAC-output byte arrays after use.

Provisioning URIs remain ordinary encrypted text fields while stored and managed strings while displayed or edited.

## Availability and recovery

Cryptography cannot guarantee availability. A user or attacker can:

- delete the vault;
- delete one entry or blob file;
- corrupt files so authentication fails;
- remove every backup;
- forget the master password;
- exhaust disk space during a save, import, or rotation.

Cripty has no password-recovery key or escrow mechanism. Verified backups protect against device and file loss only when the master password remains known.

## Security invariants

Changes should preserve these properties:

1. Never deserialize or use protected plaintext before its envelope authenticates.
2. Never attempt CBC decryption before fixed-time MAC verification succeeds.
3. Never reuse a manifest key as an entry/blob key or one object's key for another object.
4. Bind payload type, format version, vault ID, and object ID through associated data.
5. Treat the encrypted manifest as authoritative over visible hints.
6. Validate attacker-controlled KDF parameters before derivation.
7. Validate shapes and bounded lengths before large allocations or cryptographic work.
8. Write referenced blobs and entries successfully before committing a manifest that references them.
9. Do not delete still-referenced encrypted files.
10. Clear owned secret byte buffers on every success and failure path.
11. Stage and validate full-directory transformations before publication.
12. Use generic user-facing authentication failures.

## Verification and assurance

The automated suites cover cryptographic round trips, wrong-key behavior, tampering, envelope shapes, KDF bounds, HKDF separation, TOTP behavior, storage validation, save retry states, backup verification, copy workflows, and password rotation.

Tests reduce regression risk but do not replace:

- independent cryptographic review;
- adversarial filesystem testing across supported operating systems;
- fuzzing of every serialized format;
- dependency and supply-chain review;
- secure build and release processes;
- a formal incident and vulnerability-reporting process.

Until those exist, Cripty should be presented as a security-conscious portfolio project under active development, not as an audited password manager.
