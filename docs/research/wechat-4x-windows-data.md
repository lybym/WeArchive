# Research notes — WeChat 4.x on Windows: local data layout and key acquisition

This note records the external material that informed the Windows source adapter, what was
independently verified on this machine, and the clean-room boundary the project keeps. It exists
because `AGENTS.md` requires important external references to be recorded with their sources and
licensing, and because several widely repeated claims in this space turned out to be wrong when
tested.

Verified environment: WeChat for Windows **4.1.13.12** (`C:\Program Files\Tencent\Weixin`),
data root `%USERPROFILE%\xwechat_files`, Windows 11 x64.

---

## 1. What was verified locally (empirical, reproducible)

These are measurements, not claims from third parties.

| Finding | Evidence |
|---|---|
| Data root is `%USERPROFILE%\xwechat_files`; account directories are named `<login>_<suffix>` (e.g. `qq176580224_318a`) | directory inspection |
| The login name is confirmed by `all_users\login\<login>\key_info.db` and by `user_name_md5 == md5(login)` | `md5("qq176580224") == d36cd8a3e4f3d2325105f959a972c148` matched the stored `user_name_md5` |
| Databases are SQLCipher 4: `salt[0:16]`, page size 4096, reserve 80, `[4016:4032]` IV, `[4032:4096]` HMAC-SHA512, AES-256-CBC | decryption of all 25 encrypted databases succeeded and `PRAGMA integrity_check` returned `ok` |
| The page-1 HMAC covers `page[16:4032] + LE32(1)` — i.e. it **excludes** the 16-byte salt | a key that fails the "include salt" variant verifies with the "exclude salt" variant |
| Keys are **raw 32-byte** keys, one **per database file**, with no passphrase PBKDF2 step | the recovered key verifies on its own database with `mac_key = PBKDF2-HMAC-SHA512(rawKey, salt ^ 0x3a, 2, 32)`, and a *different* key is needed for a different database |
| `key_info.db` does **not** contain the database key | its `key_info_data` blob (180 bytes) is mostly constant across rows; every 32-byte window of the first rows was tested against a real page-1 HMAC under raw-key and PBKDF2 readings — no match |
| No plaintext `x'<96 hex>'` literal exists in the client's memory | full scan of ~1.6 GB of committed readable memory across all five `Weixin.exe` processes: zero matches for `x'[0-9a-f]{64,200}'` |
| The key is recoverable from memory as a XOR-masked WCDB cipher literal | scanning for the masked form of the `x'` prefix and decoding with the mask below yielded a key that verifies against real databases |
| WAL frames are encrypted with the same key as the main database, and the file holds several generations | the frame salt pair differs from the WAL header salt pair for older generations; only frames matching the header salt are valid |

### The obfuscation mask

Repeating 32-byte XOR mask applied to the cached WCDB cipher literal:

```text
d2c7442458020000004889442450488b450048844c2448488944254048584c24
```

Decoding yields `x'<64 hex raw key><32 hex salt>'`.

The implementation does **not** rely on the pointer-chasing offsets that circulate with this
mask. Instead it searches memory for the masked `x'` prefix directly, decodes all 99 bytes with
the mask, and then validates the recovered key against a real database's page-1 HMAC. That makes
the approach independent of one build's structure layout, and it cannot produce a false positive:
an unverified candidate is discarded.

### Message model verified on real databases

- One table per conversation, named `Msg_<md5(conversationId)>`.
- Columns: `local_id, server_id, local_type, sort_seq, real_sender_id, create_time, status,
  server_seq, origin_source, source, message_content, compress_content, packed_info_data,
  WCDB_CT_message_content, WCDB_CT_source`.
- `local_type` packs `(subtype << 32) | type`: `57 << 32 | 49 == 17179869233`.
- A non-zero `WCDB_CT_message_content` means the payload is Zstandard-compressed.
- Group records prefix the payload with `<sender>:\n`; the prefix is only stripped when the
  captured name is a known participant, so ordinary text containing a colon is never mangled.
- `real_sender_id` resolves through the shard's `Name2Id` table; the content prefix is treated as
  more trustworthy when the two disagree.
- App-message semantics (type 49) come from the appmsg XML `<type>` element, not from the packed
  subtype: observed values include 1, 2, 3, 5 (link), 6 (file), 19 (merged forward), 24, 33/36
  (mini program), 43, 49, 51, 53, 57 (quote), 63.
- `sysmsg type="revokemsg"` carries recalls; `sysmsgtemplate` carries group notices whose
  `$name$` placeholders are resolved from the accompanying `<link name="...">` titles.

A real group conversation (2071 records, 2026-07 → 2026-09) normalised to:
`video 1085, text 738, image 171, app_share 27, revoke 24, emoji 12, unknown 6, voice 6, system 2`.

---

## 2. External references consulted

Used for orientation only. No code was copied, and no third-party binary is bundled.

| Source | Used for | Licence / status |
|---|---|---|
| Tencent WCDB `src/common/core/cipher/CipherHandle.cpp` (<https://github.com/Tencent/wcdb>) | Confirmed that the cipher key arrives as a 99-byte `x'<96 hex>'` literal whose trailing salt is stripped, i.e. a **raw** SQLCipher key | Apache-2.0 (Tencent's own open source) |
| SQLCipher page format documentation (<https://www.zetetic.net/sqlcipher/design/>) | Page layout, reserve size, HMAC coverage, KDF parameters | Documentation |
| `stargazer-2026/wechat-4.1.12-decrypt` | First pointer to the fact that the plaintext `x'...'` literal disappears from 4.1.11 onward, and to the correct page-1 HMAC coverage (salt excluded) | MIT; single-source, its Frida offset approach was **not** adopted |
| `TANGandXUE/wcdb-key-tool` | The XOR-mask value and the `com.Tencent.WCDB.Config.Cipher` anchor that motivate the memory search | MIT |
| `maomao3334/wechat-cli-plus` | Reported a pass on 4.1.13.12 with the same mask (its scanner is a copy of the above, so this is corroboration, not independent evidence) | Apache-2.0 |
| 看雪 (kanxue) threads on WeChat 4.x key extraction | Background on `key_info.db` holding a *login* key rather than the database key, and on register/offset discrepancies between write-ups | Forum posts |
| `sjzar/chatlog`, `xaoyaoo/PyWxDump`, `LC044/WeChatMsg` | Historic orientation; all are capped below 4.1.11 and several have been removed following takedown notices | Apache-2.0 / removed |

Note on world state: this ecosystem has been heavily affected by takedown notices, so many
previously available tools are gone or gutted. The project therefore depends on **none** of them
at build time or run time.

---

## 3. Claims that did **not** survive verification

Recorded so that nobody re-derives them from the same sources.

- *"The key is a string `x'<96 hex>'` in memory."* Not on 4.1.13.12 — a 1.6 GB memory scan found
  zero such strings.
- *"The key is a passphrase requiring PBKDF2-HMAC-SHA512 with 256 000 iterations."* Not on this
  build: the recovered key is a raw key and validates with the two-iteration HMAC key derivation.
  (Both mechanisms apparently exist in WCDB for different key kinds; the entropy-based
  discrimination described in the reverse-engineering write-ups matches what was observed.)
- *"`key_info.db` stores the database key."* It does not; it stores a login key that changes on
  every sign-in.
- *"The page-1 HMAC includes the file salt."* It does not, for these databases.
- *"The decrypted database can be opened if the reserved tail is left as-is."* The reserved
  80 bytes are the IV and HMAC; the reader zeroes them, and the decrypted page-1 header declares
  the reserved size so SQLite ignores them.

---

## 4. Clean-room boundary

- No source code from any third-party project was copied into WeArchive, in whole or in part.
- The SQLCipher page format is implemented from the published format description and validated
  against real databases; the WeChat-specific knowledge is expressed as table names, column
  names, numeric type codes and XML element names discovered by inspecting this user's own local
  data.
- No third-party binary or dataset is committed to the repository.
- The project reads only the current user's own local data, read-only, and never modifies it,
  authenticates as the user, or accesses anything remotely.
- The above is operational knowledge about a file format, not a reproduction of another
  project's implementation; the architecture is WeArchive's own, as recorded in
  `docs/adr/0005-wechat-local-key-acquisition.md`.
