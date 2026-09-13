# ADR 0005 — Local WeChat archive-key acquisition from client memory

Status: accepted
Date: 2026-03-01

## Context

WeChat for Windows 4.x does not expose an export API. Its local chat databases under
`%USERPROFILE%\xwechat_files\<account>\db_storage\**` are SQLCipher 4 databases, so reading a
conversation requires the per-database encryption key.

Three properties of the problem had to be settled before any code was written:

1. **Where the key lives.** On current clients the key is not present as a readable
   `x'<96 hex>'` string anywhere in the process (empirically: a scan of ~1.6 GB of committed
   readable memory across all five `Weixin.exe` processes returned zero matches). It is held
   in an obfuscated WCDB cipher-configuration buffer.
2. **Whether the key can be derived offline.** No published method derives the database key
   from local state alone; the relevant identifiers are not sufficient. Anything that tried to
   compute the key from `key_info.db` or the config files would be guesswork.
3. **What the project is allowed to do.** `docs/PRD.md` requires a local-first, read-only,
   no-authentication-bypass posture: the user's own data, on the user's own machine, while
   their own client is signed in.

This is a decision with security and privacy consequences that are expensive to reverse, so
it is recorded here rather than being implied by the code.

## Decision

1. The adapter recovers the key **only** from the running WeChat client's own process memory,
   using `VirtualQueryEx` plus `ReadProcessMemory` with `PROCESS_QUERY_INFORMATION |
   PROCESS_VM_READ`.
2. It performs **no code injection, no hooking, no API patching, no debugger attachment and
   no static code offsets**. Keys are found by scanning memory for the masked form of the
   WCDB cipher literal and decoding it with the known repeating XOR mask.
3. Every candidate key is **cryptographically verified before use**: its HMAC key is derived
   for the target database's salt and the stored page-1 HMAC must match. A candidate that does
   not open a real database is discarded. The implementation therefore fails closed rather
   than producing plausible-looking garbage.
4. WeChat data is opened **read-only** with `FileShare.ReadWrite`. The adapter never writes,
   renames, moves or deletes anything under the WeChat data directory. A test asserts that the
   encrypted source file is byte-identical after a read.
5. Decrypted plaintext copies exist **only transiently**, in a per-run directory under
   `%LOCALAPPDATA%\WeArchive\scratch\<random>`, and are deleted when the adapter is disposed.
   Nothing decrypted is ever written into the archive, the export or the repository. A test
   asserts that the source database is unmodified; a manual check confirms zero scratch files
   remain after the process exits normally.
6. The key is **never persisted, never logged and never exported**. There is no key file and no
   "remember my key" setting.
7. All of this is confined to `WeArchive.Infrastructure/WeChat/{KeyAcquisition,Crypto}`.
   Nothing outside that boundary knows that WeChat is encrypted at all.
8. If the key cannot be recovered (client not running, unknown future layout), the adapter
   reports a clear, actionable diagnostic and the operation fails. It does not fall back to
   guessing.

## Alternatives considered

### Offline key derivation from local files

Rejected as currently impossible. `key_info.db` holds a *login* key, which is a different value
from the database key and changes on every sign-in; no published method derives the database key
from local state. Attempting it would be fabrication, which `docs/PRD.md` NFR-10 forbids.

### Frida-based hooking of an internal function

Rejected for the MVP. Published approaches hardcode a `Weixin.dll` offset for one exact build,
require injecting into the client, must attach before sign-in, and reportedly trip the client's
anti-hook detection. That is a much larger security and stability surface than reading memory,
and it would make the adapter version-brittle. It remains the fallback worth revisiting if a
future client moves the key out of the heap entirely.

### Asking the user to paste a key

Rejected as the primary path. It pushes a reverse-engineering step onto the user and would make
the product unusable for the intended audience. It is a reasonable *fallback* to add later, and
the design keeps room for it behind the same key-provider boundary.

### Shipping a prebuilt SQLCipher native library and using `PRAGMA key`

Rejected. It adds a native dependency and still requires the key. The managed page cipher is
small, has no native dependency and is directly testable — which is exactly what the test suite
does (it builds an encrypted database in the same format and verifies the reader recovers it,
including write-ahead-log recovery).

## Consequences

Positive:

- Read-only end to end; the source is provably untouched.
- The design is version-tolerant: it depends on a data format and a cryptographic check rather
  than on a code address, so a new client build does not automatically break it.
- Fail-closed verification means a wrong assumption surfaces as a clear error, never as
  silently corrupted output.
- No plaintext WeChat data is left behind after a run.

Costs:

- Reading requires the client to be running and signed in.
- The XOR mask and the cipher-literal layout are reverse-engineered facts, not a published API;
  a future client version may change them. The mask lives in exactly one place
  (`WcdbCipherConfigKeyAcquirer`) so that fix is local.
- A machine where the user may not read their own client's memory yields a documented failure
  rather than a partial result.
- The scan is a full-memory sweep and costs roughly ten to thirty seconds on a warm cache; its
  result is cached for the adapter's lifetime.

## References

- `docs/PRD.md` (NFR-02 read-only source boundary, NFR-07 privacy, NFR-10 no fabricated semantics)
- `docs/ARCHITECTURE.md` (adapter boundary, security architecture)
- `docs/research/wechat-4x-windows-data.md` (sources, licensing and the clean-room boundary)
- `src/WeArchive.Infrastructure/WeChat/KeyAcquisition/`
- `src/WeArchive.Infrastructure/WeChat/Crypto/SqlCipherPageCipher.cs`
- `tests/WeArchive.Tests/SqlCipherTests.cs`
