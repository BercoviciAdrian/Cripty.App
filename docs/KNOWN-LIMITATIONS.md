# Cripty known limitations and accepted debt

This document records current limitations in Cripty at commit `9d7074e`. It exists so security assumptions and engineering compromises do not survive only in commit history or developer memory.

Not every item is a vulnerability. Some are deliberate product behavior, some are reliability debt, and some are assurance gaps that matter only if Cripty's deployment or security claims expand.

See [SECURITY-MODEL.md](SECURITY-MODEL.md) for the threat model and [ARCHITECTURE.md](ARCHITECTURE.md) for the relevant workflows.

## Rating guide

These ratings are project priorities, not CVSS scores:

- **High:** a realistic condition inside the stated threat model could broadly compromise vault confidentiality or integrity, or cause unrecoverable loss.
- **Medium:** meaningful loss, integrity failure, or exposure is possible, but requires a narrower failure sequence, user action, or additional attacker capability.
- **Low:** constrained impact, substantial preconditions, metadata/usability impact, or a risk primarily outside the stated threat model.
- **Informational:** intentional behavior or an assurance/documentation constraint rather than a defect.

Impact and likelihood are stated separately where a low-likelihood event can still have a high consequence.

## Summary

| ID | Area | Rating | Status | Summary |
| --- | --- | --- | --- | --- |
| KL-01 | Save consistency | Medium; potentially high impact | Accepted debt | Individual files are atomic, but a complete multi-file save is not one transaction |
| KL-02 | Concurrent access | Medium | Open | No cross-process vault lock prevents two processes from writing the same vault |
| KL-03 | Snapshot freshness | Medium | Accepted debt | A complete older authenticated snapshot cannot always be detected on a new device |
| KL-04 | Synchronization conflicts | Medium | By design | Import replaces a snapshot; it does not merge concurrent vault histories |
| KL-05 | Historical snapshots | Medium | By design | Password rotation cannot revoke or securely erase old vault copies and provider history |
| KL-06 | Clipboard | Low in the stated model | Open | Copied plaintext is controlled by the operating-system clipboard |
| KL-07 | Managed memory | Low in the stated model | Inherent/partially mitigated | Managed strings, paging, dumps, and graphics buffers cannot be reliably scrubbed |
| KL-08 | Hostile filesystem links | Low in the stated model | Open | Backup export does not explicitly reject symbolic links or reparse points |
| KL-09 | Metadata privacy | Low | By design | Vault and backup operational metadata remains visible |
| KL-10 | Deleted-data remanence | Low | Open | Failed cleanup or storage history can retain obsolete encrypted files |
| KL-11 | Cryptographic assurance | Assurance gap | Open before production claims | The application-specific CBC/HMAC integration and formats have not been independently audited |
| KL-12 | KDF calibration | Low, time-sensitive | Open | Argon2id defaults are fixed rather than benchmarked per device |
| KL-13 | Recovery and backup | Informational; high user impact | By design | There is no password recovery, and backups are user-triggered |
| KL-14 | Image support | Low | Current scope | Images are PNG-only and processed in memory within explicit limits |
| KL-15 | Inactivity timeout | Informational | By design | Timeout discards unsaved work and closes the application |

## KL-01 — A complete vault save is not transactional

**Classification:** reliability and availability
**Rating:** Medium likelihood/priority, with potentially high impact to affected entries if no usable backup exists
**Status:** accepted debt

### Current behavior

Each `vault.cripty`, `.entry`, and `.blob` file is written through a temporary file, flushed, and atomically replaced. A Save can still touch several independent files.

The current order is:

1. write new blobs;
2. write changed entries;
3. write the new manifest generation;
4. delete obsolete files.

If a changed entry file is replaced successfully and the later manifest write fails, the old persisted manifest can reference the previous entry revision while the entry file contains the new revision.

While the same session remains alive, `VaultSession` detects this state, blocks further mutation, and requires Save to be retried. A crash, forced shutdown, lost device, or unrecoverable I/O failure before retry can leave a vault that needs recovery from a valid snapshot.

### Why it was accepted

A true transaction across several ordinary filesystem files is not portable or simple. The current design gains:

- atomicity for each individual file;
- deterministic write ordering;
- explicit retry state;
- no manifest reference to a blob or entry that was never written;
- lower implementation complexity for the current project stage.

### Current mitigations

- Per-file temporary write, flush-to-disk request, and atomic replacement.
- New blobs and entries precede the manifest.
- Descriptor revisions advance only after an entry write succeeds.
- Further mutation is blocked after a partial entry/manifest commit.
- Explicit Save errors are surfaced.
- Encrypted snapshot export/import provides manual recovery.

Backups reduce consequence; they do not make the save atomic.

### Revisit when

- Cripty is presented as production-ready;
- vaults contain irreplaceable data;
- autosave or background writes are introduced;
- simultaneous processes or synchronization target the live vault;
- the app claims crash consistency across power loss.

### Candidate direction

Use generation-scoped immutable entry/blob files plus an atomic manifest pointer, or publish a fully staged vault generation through a recoverable directory swap. Retain and garbage-collect old generations only after the new generation is validated.

### Guardrails

Application tests should continue to inject failures after blob writes, after entry writes, and during manifest publication, and verify retry-state behavior.

## KL-02 — No cross-process vault lock

**Classification:** integrity and reliability
**Rating:** Medium
**Status:** open

### Current behavior

`VaultSession` has an in-process semaphore that serializes operations within one session. It does not acquire an operating-system lock, lease file, or other cross-process ownership marker for the vault directory.

Two Cripty instances—or Cripty and another writer—can open the same generation and later overwrite one another's files. Generation and revision checks operate within each session's loaded state; they do not provide compare-and-swap publication against an independently updated on-disk manifest.

### Impact

- lost updates;
- entry/manifest revision mismatch;
- partial or inconsistent snapshots;
- user confusion when one process replaces another's changes.

### Current mitigations

- User-facing workflow normally opens one vault in one application instance.
- In-process operations are serialized.
- Entry revisions and manifest generations expose some inconsistencies after the fact.
- Backups can recover a prior complete version.

### Revisit when

- multi-window or multi-instance use is supported;
- a live vault is placed in a synchronized directory;
- background agents or services access vaults;
- autosave is introduced.

### Candidate direction

Acquire a vault-wide lock for the unlocked session, record an owner token, and add generation-based compare-and-swap checks immediately before manifest publication.

## KL-03 — No external snapshot-freshness anchor

**Classification:** integrity and rollback resistance
**Rating:** Medium
**Status:** accepted debt

### Current behavior

Manifest generations and entry revisions are authenticated inside the vault. The visible manifest-generation hint is verified against the encrypted manifest after unlock. These mechanisms detect mixed generations and help compare a backup with a known local vault.

They cannot prove freshness when an attacker replaces the **entire** vault with an older, internally consistent authenticated snapshot and the device has no separately trusted record of a newer generation.

### Impact

The user may unknowingly see old credentials, deleted entries, old TOTP secrets, or an older password/key state. Confidentiality is not directly broken, but integrity and freshness are.

### Current mitigations

- Imports compare vault ID, generation, and exact snapshot hashes with an existing local vault.
- Replacing another version requires confirmation.
- The current local version receives a pre-import recovery backup.
- Backup timestamps and generations help the user identify likely ordering.

These are comparison and recovery mechanisms, not a cryptographic freshness proof.

### Revisit when

- rollback resistance becomes a stated security property;
- remote synchronization becomes built in;
- users commonly restore onto new devices;
- shared or regulated vault use is considered.

### Candidate direction

Persist the latest accepted generation in an independent trusted location, use an authenticated remote monotonic record, or maintain a signed append-only history. Every option adds state, recovery, and privacy trade-offs to the local-first design.

## KL-04 — Backup import replaces; it does not merge

**Classification:** data consistency and usability
**Rating:** Medium
**Status:** by design

### Current behavior

Export creates complete encrypted snapshots. Import either adds a missing vault, recognizes an identical snapshot, or replaces the current version after confirmation. It does not reconcile folders, entries, revisions, or deletions from two divergent histories.

### Impact

Changes made independently on two machines can be lost if the wrong snapshot replaces the other. A larger generation number does not by itself mean every desired change is present.

### Current mitigations

- Existing and imported generations are displayed.
- Replacement requires explicit confirmation.
- A complete encrypted recovery backup is created before replacement.
- Identical snapshots are detected.

### Revisit when

- multi-device editing becomes a supported workflow;
- cloud synchronization is integrated directly;
- vaults can remain open on several machines.

### Candidate direction

Define stable change IDs, tombstones, conflict semantics, and an authenticated append-only change log. Entry-level merging alone is insufficient because folders, tags, moves, deletes, and blob references can also conflict.

## KL-05 — Password rotation does not revoke historical copies

**Classification:** key lifecycle and operational security
**Rating:** Medium
**Status:** by design

### Current behavior

Password change generates a new root key and re-encrypts the current vault. It does not modify:

- previously exported backups;
- cloud-provider version history;
- filesystem snapshots;
- copied vault directories;
- deleted blocks retained by SSDs or journaling filesystems.

An old snapshot remains decryptable with the password and root-key envelope that protected that snapshot.

### Impact

Changing the password after suspecting that both an old vault copy and its old password were exposed does not retroactively make that historical copy confidential.

### Current mitigations

- The current vault receives a fresh root key, so future state is separated from the old root key.
- The rotation staging directory is validated before publication.
- Users can remove known old backups and provider versions.

Deletion from ordinary application code cannot guarantee physical erasure or removal from every synchronized replica.

### Revisit when

- key revocation is advertised;
- shared recipients or devices are introduced;
- backup retention is managed by Cripty;
- a remote service can enforce revocation or expiry.

## KL-06 — Clipboard plaintext is outside Cripty's control

**Classification:** confidentiality and usability
**Rating:** Low within the stated at-rest threat model; higher on a hostile desktop
**Status:** open

### Current behavior

Copying a field or TOTP code places plaintext on the operating-system clipboard. Cripty does not currently guarantee timed clearing or ownership-checked clearing.

Clipboard managers, remote-desktop software, accessibility tools, other applications, and later paste operations can observe or retain the value.

### Why copy remains

Copy is a core password-vault workflow. Removing it would materially reduce usability without protecting against software that can already read the unlocked UI or process.

### Current mitigations

- Copy is explicit.
- Invalid TOTP content disables the authentication-code copy action.
- Secrets remain encrypted at rest before and after the clipboard operation.
- The general compromised-host attacker is outside the primary threat model.

### Candidate direction

Offer a configurable timeout and clear only if the clipboard still contains the exact value placed by Cripty. Document that cross-platform clipboard ownership and clipboard-manager history prevent a universal erasure guarantee.

## KL-07 — Managed memory is not secure memory

**Classification:** confidentiality and assurance
**Rating:** Low within the stated model
**Status:** inherent and partially mitigated

### Current behavior

Cripty clears controlled key and plaintext byte arrays where practical. The UI and domain model still require managed strings for passwords, names, notes, provisioning URIs, and other editable text.

.NET may copy or retain strings and objects during garbage collection. The operating system or runtime can also retain information through:

- paging or swap;
- hibernation;
- crash dumps;
- graphics and text-rendering buffers;
- input-method buffers;
- diagnostic tooling.

### Current mitigations

- Root keys and derived keys use byte/span storage and explicit zeroing.
- Decrypted serialized payload arrays are cleared.
- Decoded TOTP secret and HMAC buffers are cleared.
- Owned image byte buffers are cleared.
- Inactivity disposal limits the time the active root key remains owned by the session.

### Revisit when

- memory forensics enters the threat model;
- Cripty runs with elevated privileges;
- regulated high-assurance use is claimed.

Native locked-memory components could reduce some exposure, but cannot protect against a hostile administrator, debugger, or display/input compromise.

## KL-08 — Backup export does not explicitly reject filesystem links

**Classification:** host-filesystem trust and possible exfiltration
**Rating:** Low in the stated model because of the required host access; potentially more serious under elevated or service-account execution
**Status:** open

### Current behavior

Backup export enumerates top-level files with expected `.entry` and `.blob` extensions and opens the resulting paths for copying. It does not explicitly reject symbolic links, junctions, mount tricks, or reparse points and does not use a platform-specific no-follow file open.

On a platform and filesystem where an attacker can place a link such as `entries/<id>.entry` pointing to another readable file, export can copy the target bytes into its temporary backup area before full backup validation rejects an invalid vault payload.

The temporary export directory is created under the selected backup root. If that root is watched by a fast synchronization client, even a temporary file that is later rejected and deleted could potentially be uploaded.

### Required attacker position

The attacker must already be able to modify the vault directory and cause the Cripty process to read a target file. Under normal execution, Cripty has only the current user's permissions. An attacker with that degree of same-user filesystem control can commonly exfiltrate files or install spyware by simpler means.

That prerequisite is why this is rated Low inside Cripty's current desktop threat model rather than as a broad confidentiality failure.

### Current mitigations

- Only `vault.cripty`, top-level `.entry` files, and top-level `.blob` files are enumerated.
- Relative backup paths are restricted and traversal is rejected.
- The finished snapshot is structurally and cryptographically validated before final publication.
- Failed temporary exports are deleted best-effort.
- Cripty is not intended to run elevated or as a privileged service.

Validation after copying does not prevent transient copying into a synchronized temporary directory.

### Revisit when

- Cripty runs elevated;
- Cripty runs as a service or under an account with broader read permissions;
- untrusted users can modify a vault directory used by another user;
- the project claims that a hostile filesystem cannot cause plaintext to touch the backup location.

### Candidate direction

- Reject reparse points and symbolic links during enumeration.
- Open source files using platform-supported no-follow semantics.
- Resolve and verify handle-based final paths where available.
- Stage outside the synchronized backup root.
- Move only the fully validated encrypted snapshot into the watched directory.
- Add Windows and Linux link/race regression tests.

## KL-09 — Operational metadata is visible

**Classification:** metadata confidentiality
**Rating:** Low
**Status:** by design

### Visible information

- vault directory name and path;
- vault ID;
- manifest-generation hint;
- format versions;
- Argon2id parameters and salt;
- number and sizes of entry/blob files;
- GUID object filenames;
- filesystem timestamps;
- backup name, timestamp, recovery marker, paths, file sizes, and SHA-256 hashes.

Folder names, tag names, entry names, field names/values, and image content remain encrypted.

### Why it was accepted

Visible IDs and versions simplify file discovery, independent object storage, locked-vault selection, validation, backup comparison, and recovery. Hiding all access patterns would require padding, opaque pack files, or ORAM-like techniques far beyond the project scope.

### Revisit when

- even vault size or activity timing is sensitive;
- shared/cloud storage metadata is considered adversarial intelligence;
- deniable storage or traffic-analysis resistance becomes a goal.

## KL-10 — Obsolete encrypted data can remain

**Classification:** data remanence and storage hygiene
**Rating:** Low
**Status:** open

### Current behavior

The manifest is committed before obsolete entry and blob files are deleted. This is the correct referential-integrity order, but deletion can fail because of permissions, sharing, antivirus, synchronization, or I/O errors.

The active session retains failed cleanup IDs and can retry. If the process exits before successful cleanup, the now-unreferenced encrypted file may remain in the vault directory. Backup enumeration copies all matching top-level entry/blob files, including an orphan that the manifest no longer references.

Storage devices, filesystem snapshots, and cloud history can also retain successfully deleted ciphertext.

### Impact

The data remains encrypted, but:

- deleted content may persist longer than the user expects;
- vault and backup size can grow;
- a future root-key/password compromise can expose historical ciphertext still protected by that key hierarchy.

### Current mitigations

- Physical deletion occurs only after the new manifest commits.
- Failed cleanup remains retryable during the active session.
- Orphan files are not reachable through the committed manifest.

### Candidate direction

On open or maintenance, authenticate the manifest, compute the referenced entry/blob set, identify extra files, and offer or perform safe cleanup after verification. Keep recovery semantics explicit so a damaged manifest does not cause destructive cleanup.

## KL-11 — Cryptographic integration has not been independently reviewed

**Classification:** security assurance
**Rating:** unknown vulnerability severity; high-priority assurance work before production claims
**Status:** open

### Current behavior

Cripty uses established primitives:

- Argon2id;
- HKDF-SHA-512;
- AES-256-CBC;
- HMAC-SHA-512;
- cryptographic random generation;
- fixed-time tag comparison.

The application still owns security-sensitive integration code:

- combined-key splitting;
- CBC/HMAC envelope construction;
- HMAC input framing and truncation;
- purpose labels and HKDF `info`;
- associated-data encoding;
- serialized formats and validators;
- key and plaintext lifetime handling;
- save, import, and rotation publication logic.

This is the “custom implementation surface around CBC/HMAC.” It is technical debt because correctness depends on maintaining several coupled invariants, not because CBC plus HMAC is automatically obsolete or insecure.

### Current mitigations

- Encrypt-then-MAC.
- MAC verification before decryption.
- Fixed-time comparison.
- Generic failure behavior.
- Purpose-separated keys and authenticated storage context.
- Tests for round trips, tampering, wrong keys, malformed envelopes, and key separation.

### Revisit when

- real user secrets are encouraged;
- signed releases are distributed;
- the application is presented as production-grade;
- storage version 2 is designed.

### Candidate direction

Commission independent review and add published test vectors. For a future format, evaluate a platform-supported AEAD with a misuse-resistant, well-reviewed integration and a migration path. Changing algorithms is not a substitute for reviewing key lifecycle, serialization, rollback, and save consistency.

## KL-12 — Argon2id parameters are not device-calibrated

**Classification:** password-hardening maintenance
**Rating:** Low today, time-sensitive
**Status:** open

### Current behavior

The default is 64 MiB, 3 iterations, and 4 lanes. Users can select validated values within fixed bounds, but Cripty does not benchmark the device to a target unlock duration or periodically recommend stronger parameters.

### Impact

- defaults may become comparatively weak as hardware improves;
- high manual settings can make unlock unpleasant on slower devices;
- parallelism affects performance and is not a simple monotonic strength control;
- copied vaults retain the parameters chosen when they were created or rotated.

### Current mitigations

- Argon2id is memory-hard.
- Minimum parameters prevent very weak imported profiles.
- Maximum parameters limit malicious resource consumption.
- Password rotation can select new parameters while also rotating the root key.

### Revisit when

- target hardware and acceptable unlock latency are defined;
- defaults are more than a year old;
- dependency or Argon2 guidance changes;
- mobile/low-memory platforms are added.

## KL-13 — No password recovery; backups are manual

**Classification:** availability and product policy
**Rating:** Informational security property with high user impact
**Status:** by design

### Current behavior

Cripty has no recovery key, escrow secret, account service, or password-reset flow. A forgotten master password makes the vault cryptographically inaccessible.

Backups are explicit exports. Cripty can target a folder synchronized by another program, but it does not schedule exports or prove that a cloud client uploaded them successfully.

### Why it was accepted

A recovery secret would become another decryption credential and would require its own storage, authentication, and threat model. Manual backup keeps Cripty local-first and provider-independent.

### Current mitigations

- Exported snapshots are complete and verified.
- Imports validate exact files, lengths, and hashes.
- Replacing an existing version creates a recovery backup.
- UI makes backup/export available without unlocking a vault.

### User consequence

Users must retain:

- the master password;
- at least one verified backup on independent storage;
- awareness that a synchronized folder is not necessarily a backup if deletion and corruption synchronize too.

## KL-14 — Image support is deliberately narrow and memory-based

**Classification:** functionality and resource use
**Rating:** Low
**Status:** current scope

### Current behavior

- Images enter through the clipboard.
- They are normalized to PNG.
- Encoded PNG data is limited to 20 MiB.
- Width and height are limited to 8,192 pixels.
- Total pixels are limited to 40 million.
- Encoding, preview, encryption, decryption, and viewing use in-memory buffers.
- Other attachments, audio, video, and arbitrary files are not exposed by the UI.

### Impact

Large but valid images can temporarily consume substantially more memory than their encoded size. Unsupported formats must first be decoded by the clipboard/platform path and then stored as PNG.

### Current mitigations

- Encoded-size, dimension, and pixel-count limits.
- Bitmap disposal and owned PNG-byte clearing.
- Blobs are stored independently, so manifest and entry rewrites do not duplicate image ciphertext.

### Revisit when

- arbitrary attachments are introduced;
- streaming encryption is needed;
- very large media is supported;
- thumbnails or deduplication are added.

## KL-15 — Inactivity timeout intentionally discards unsaved changes

**Classification:** security/usability policy
**Rating:** Informational
**Status:** by design

### Current behavior

After five minutes without keyboard, text, click, or scroll interaction, Cripty locks the session and closes without saving. Pointer movement does not reset the timer. The final minute displays a warning and offers an explicit keep-open action.

### Rationale

Automatically saving because the user stopped interacting could persist half-finished or unwanted sensitive edits. Leaving the vault open would increase unattended exposure. The fixed policy favors confidentiality and explicit commit semantics over preservation of unsaved work.

### User consequence

Unsaved edits are lost on timeout. Bringing the window into focus without an actual counted interaction does not itself guarantee a reset.

### Revisit when

- timeout becomes configurable;
- autosave is introduced;
- long-running read-only viewing is a common workflow;
- accessibility testing identifies input methods not counted as interaction.

## Out-of-scope conditions

The following are not treated as defects in the current at-rest model, although deployments may choose a broader model:

- malware reading an unlocked process;
- keylogging or screen capture;
- a hostile administrator or debugger;
- OS paging, hibernation, or crash dumps;
- denial of service through file deletion;
- physical coercion or shoulder surfing;
- compromise of the .NET runtime, cryptographic provider, or build chain;
- user selection of a weak or reused master password.

Moving any of these into scope requires a new design review rather than a documentation-only change.

## Maintenance rule

A limitation may be removed from this document only when:

1. the implementation changes;
2. regression tests protect the new property;
3. the security model and architecture documents are updated;
4. old-format and migration behavior are considered;
5. the claim has been verified on every supported platform relevant to it.

New accepted debt should record its rationale, current mitigation, impact, revisit condition, and protective tests in this file.
