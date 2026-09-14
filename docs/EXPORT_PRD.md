# WeArchive Phase 1 Export PRD

## 1. Purpose

This document defines the first-phase export requirements for WeArchive.

The export is designed for **machine processing, LLM / Harness analysis, scripting, search, statistics and downstream automation**. It is not designed as a human-facing chat viewer.

The export must optimize for:

- stable paths;
- stable identifiers;
- plain-text storage;
- deterministic structure;
- selective loading;
- compact LLM context;
- reliable identity resolution;
- long-term compatibility;
- no dependency on binary media files.

Canonical message semantics are defined by [MESSAGE_SCHEMA.md](MESSAGE_SCHEMA.md). This document defines how those messages are packaged and selected for export.

---

## 2. Phase 1 scope

### 2.1 In scope

Phase 1 shall support:

- selecting specific direct chats and group chats for export;
- exporting message timelines as UTF-8 JSONL;
- splitting timelines by conversation / year / month;
- stable conversation directory IDs independent of display names;
- a maintainable identity mapping file;
- a maintainable conversation catalog;
- named collections for reusable analysis sets;
- textual representation of image, voice, video, emoji and other non-text events;
- file-event export with filename retention where available;
- structured extraction of locally available link/app-share metadata and original URLs;
- structural reply/quote relationships;
- structural forwarded-chat bundles where available;
- retention of system/revoke/special/unknown events;
- provenance sufficient for traceability and reprocessing.

### 2.2 Out of scope

Phase 1 shall not:

- export image binaries;
- export voice/audio binaries;
- export video binaries;
- export transferred file binaries;
- generate HTML chat views;
- generate human-oriented Markdown transcripts;
- create an LLM-specific derived/chunk layer;
- perform OCR over images;
- perform ASR over voice messages;
- require remote crawling of linked webpages as part of canonical export;
- use mutable human display names as physical folder names.

---

## 3. Canonical export package

```text
wechat-export/
├─ manifest.json
├─ identities.yaml
├─ conversations.yaml
├─ collections.yaml
└─ chats/
   ├─ direct/
   │  └─ u_<stable-id>/
   │     └─ <year>/
   │        └─ <year>-<month>.jsonl
   └─ groups/
      └─ g_<stable-id>/
         └─ <year>/
            └─ <year>-<month>.jsonl
```

Example:

```text
wechat-export/
├─ manifest.json
├─ identities.yaml
├─ conversations.yaml
├─ collections.yaml
└─ chats/
   ├─ direct/
   │  └─ u_019af771c3d04e8b/
   │     └─ 2026/
   │        ├─ 2026-08.jsonl
   │        └─ 2026-09.jsonl
   └─ groups/
      └─ g_01fd893a7b21c054/
         └─ 2026/
            ├─ 2026-07.jsonl
            ├─ 2026-08.jsonl
            └─ 2026-09.jsonl
```

Stable IDs use a 16-hex-character prefix, not the 8 characters shown in earlier revisions of
these documents; see [DATA_MODEL.md](DATA_MODEL.md) section 16.1 for the collision rationale.

### 3.1 Path stability rule

Physical paths are derived only from stable IDs.

Display names, remarks, aliases and group titles shall never be part of the physical path.

This ensures that Harness prompts, scripts and historical references remain valid after name changes.

### 3.2 Re-export rule

Re-exporting a conversation **deletes that conversation's directory and rewrites it**. Nothing
outside the conversation's own folder is touched, so duplicate temporal partitions and stale
months are structurally impossible, and the export of one conversation can never damage
another conversation's output.

The rewrite is crash-safe: each conversation's new timelines are written to a sibling staging
directory, and the root catalogs and `manifest.json` are written to sibling temp files. The
previously-exported directory and files are only replaced once every replacement is durable,
so a cancellation, I/O failure or process termination leaves the last good dataset intact
rather than a half-written package. Leftover staging artifacts from a crashed run are swept
at the start of the next export.

`collections.yaml` is written only when it does not already exist; a user-maintained file is
never overwritten.

---

## 4. Conversation IDs and folder naming

### Direct chat

```text
chats/direct/u_<stable-id>/
```

### Group chat

```text
chats/groups/g_<stable-id>/
```

Stable IDs must not change when remarks, nicknames or group titles change.

A conversation may have a user-maintained alias in `conversations.yaml`, but aliases are lookup conveniences only and do not determine physical paths.

Example:

```yaml
conversations:
  g_01fd893a7b21c054:
    type: group
    current_name: 华东产品创新中心工作群
    alias: east-product-center
```

---

## 5. Identity mapping

Identity resolution is centralized in `identities.yaml`.

Example:

```yaml
users:
  u_7a19d3824b6c0f91:
    source_user_id: wxid_xxxxx
    remark: 张三
    nickname: 三哥
    display_name: 张三
    display_name_override: ""

  u_82f119ab5d3e7c40:
    source_user_id: wxid_yyyyy
    remark: ""
    nickname: Kevin
    display_name: ""
    display_name_override: ""
```

### 5.1 Default display-name rule

```text
if latest_remark is non-empty:
    display_name = latest_remark
else:
    display_name = ""
```

Nickname is metadata only and must not automatically replace a missing remark.

This rule applies to both direct-chat friends and people appearing in groups.

### 5.2 Coverage

`identities.yaml` publishes **only the participants referenced by the exported timelines** —
every sender of an exported message, every sender referenced by an exported `reply_to`, and,
for a direct conversation, its peer — plus any user-maintained `display_name_override` that
must survive regeneration.

It is deliberately not a dump of the local contact list, so a consumer can load the whole
catalog without pulling in unrelated contacts. A referenced sender with no archived
participant record is still published by stable ID with empty metadata rather than being
dropped or given a fabricated name.

### 5.3 The user-maintained hook

`display_name_override` is the documented user-maintained field.

```text
display_name_override non-empty -> override wins
otherwise                       -> display_name (latest remark, else "")
```

`display_name` is always regenerated from the archive; `display_name_override` is never
regenerated and always wins when set.

### 5.4 Merge policy

Regeneration preserves explicitly user-maintained fields according to this policy:

1. For an identity referenced by this export that already appears in the existing
   `identities.yaml`, `display_name_override` is carried over verbatim.
2. An identity that this export does not reference but that carries a non-empty
   `display_name_override` is carried over unchanged, so a manual correction is never
   silently discarded.
3. `source_user_id`, `remark`, `nickname` and `display_name` are regenerated from the archive.
4. A hand-edited `identities.yaml` that no longer parses must not block an export: the file is
   regenerated from the archive.

### 5.5 Group-specific names

Group nicknames may be retained as secondary metadata, but the canonical identity remains the stable `u_...` ID.

---

## 6. Conversation catalog

`conversations.yaml` maps stable IDs to current semantic metadata. It carries a top-level `schema_version` and, per conversation, `type`, `current_name`, `alias`, `user_id` (direct conversations only, equal to the conversation ID) and the observed `first_message_at` / `last_message_at`.

Example:

```yaml
schema_version: 1.0
conversations:
  g_01fd893a7b21c054:
    type: group
    current_name: 华东产品创新中心工作群
    alias: east-product-center
    first_message_at: 2023-04-11T09:23:14+08:00
    last_message_at: 2026-09-13T18:32:41+08:00

  u_7a19d3824b6c0f91:
    type: direct
    current_name: 张三
    alias: zhang-san
    user_id: u_7a19d3824b6c0f91
```

`alias` is user-maintained. Regeneration carries the existing value over for every conversation
still present in the export and never rewrites it from the archive.

The catalog allows programs and Harness workflows to resolve stable IDs without depending on filesystem names.

---

## 7. Selective export

Exporting all conversations must not be the only mode.

Phase 1 shall support selection by at least:

- stable conversation ID;
- maintained alias;
- named collection.

Conceptual configuration:

```yaml
include:
  - g_01fd893a7b21c054
  - u_7a19d3824b6c0f91
```

Selection by stable conversation ID is the shipped behaviour: the application exports the
conversation the user selected. Batch selection, alias resolution and collection selection are
still product requirements to be delivered on top of the same export engine; the export engine
already accepts a list of conversation IDs.

---

## 8. Collections

`collections.yaml` defines reusable analysis sets without duplicating message data. It carries a top-level `schema_version`.

Example:

```yaml
schema_version: 1.0
collections:
  east-product-center:
    conversations:
      - g_01fd893a7b21c054
      - g_814ac1219d0e3f76
      - u_019af771c3d04e8b
      - u_22b7a9826a1f5d38

  ai-toy-project:
    conversations:
      - g_77cd993a4f8b21e0
      - u_a919de31c72b4605
      - u_991aa2073e5c81fd
```

Primary uses:

- project analysis;
- relationship analysis;
- organizational analysis;
- recurring Harness workflows;
- repeatedly exporting the same set of conversations.

`collections.yaml` is user-maintained. An export writes the empty shape only when the file does
not exist; an existing file is never overwritten, so a user-maintained collection set survives
regeneration. Collection *selection* is not yet wired into the application.

---

## 9. Timeline files

Conversation timelines use UTF-8 JSON Lines (`.jsonl`).

One physical line equals one canonical logical message/event record as defined by `MESSAGE_SCHEMA.md`. A writer is scoped to a single message, so a partition is a stream of independent
JSON values rather than one large JSON array.

Files are written with the **relaxed JSON encoder** (`UnsafeRelaxedJsonEscaping`): CJK text and
ordinary punctuation stay human-readable and the files stay small, while control characters and
quotes are still escaped. The dataset is plain text for machines and LLMs and is never embedded
in HTML, so relaxed escaping is the correct trade-off here.

### 9.1 Partitioning

Default partitioning:

```text
conversation / year / month
```

Example:

```text
chats/groups/g_01fd893a7b21c054/2026/2026-09.jsonl
```

The month component is zero-padded (`2026-09`, not `2026-9`).

Monthly partitioning balances selective LLM loading, manageable file sizes, time-range analysis, incremental regeneration and simple scripting.

### 9.2 Ordering

Records shall be ordered by canonical message time with a stable secondary ordering key for equal timestamps.

The shipped order is `(occurred_utc, source_order_key, stable message id)`. The upstream order
key is the third component's purpose: two records that share a timestamp still appear in the
same order on every export.

### 9.3 Timestamps

`time` is ISO-8601 with the source's timezone offset, formatted `yyyy-MM-dd'T'HH:mm:sszzz`
(for example `2026-09-13T09:22:14+08:00`).

### 9.4 Missing months

A conversation with no records in a month produces no file for that month. A month with records
produces exactly one file.

---

## 10. Canonical message contract

Every JSONL line must conform to the canonical envelope defined in [MESSAGE_SCHEMA.md](MESSAGE_SCHEMA.md).

Conceptual example:

```json
{
  "id": "m_f09271a4c8b3e065",
  "conversation_id": "g_01fd893a7b21c054",
  "sender_id": "u_7a19d3824b6c0f91",
  "time": "2026-09-13T09:22:14+08:00",
  "type": "text",
  "text": "下午的材料我已经修改好了",
  "reply_to": null,
  "payload": null,
  "source": {
    "source_message_id": "...",
    "source_type": "...",
    "source_subtype": "...",
    "source_partition": "...",
    "source_order_key": "..."
  }
}
```

The normative Phase 1 types are:

```text
text
image
voice
video
file
link
app_share
mini_program
forward_bundle
location
contact_card
system
revoke
red_packet
transfer
emoji
unknown
```

`quote` is a relationship expressed through `reply_to`, not a standalone content type.

Unknown records must be retained as `unknown` and counted in diagnostics.

`payload` and `source` are always present as keys; a value that is genuinely absent is written
as `null` rather than omitted, so a consumer never has to distinguish "missing key" from
"known-empty value".

---

## 11. Media/file text-only policy

No binary attachments are part of the Phase 1 canonical export.

Examples of semantic text:

```text
图片   -> [图片]
视频   -> [视频]
语音   -> [语音]
表情   -> [表情]
文件   -> [文件] filename.ext
```

For file messages, the original filename shall be retained when locally available.

Voice duration and similar metadata may be retained when reliably available, but no ASR/OCR/visual derived content is generated.

---

## 12. Links and forwarded app content

This is a Phase 1 core requirement.

For ordinary web links, app-share cards and mini-program/card-like content, WeArchive shall preserve all locally obtainable semantic fields defined in `MESSAGE_SCHEMA.md`.

Priority for URL preservation:

```text
confirmed original URL
    > wrapper/fallback URL
    > app_id + page_path
    > title/description only
```

If a confirmed original URL is locally obtainable, it must be exported structurally as `original_url`.

If only a wrapper/tracking URL is available, it must not be mislabeled as a confirmed original URL.

The canonical export does not require remote webpage crawling.

---

## 13. Reply, forwarded bundle and event semantics

Phase 1 must retain important conversational structure rather than flattening everything into plain text.

Required behaviors are defined in `MESSAGE_SCHEMA.md`, including:

- `reply_to` relationships plus available quote snapshots;
- forwarded chat bundles with nested textual items;
- group/system events;
- revoke events;
- location/contact cards;
- red packet/transfer events where semantic fields are reliably understood;
- unknown records.

The `text` field remains the LLM-first representation, while `payload` provides structured detail.

`reply_to.message_id` is null until the target resolves against the archive. The upstream
identifier used for that resolution is retained internally and is never exported.

---

## 14. Manifest

`manifest.json` describes the dataset before a consumer loads message files.

The shipped fields are:

| Field | Meaning |
|---|---|
| `export_schema_version` | Export package schema, currently `"1.0"`. |
| `message_schema_version` | Canonical message schema, currently `"1.0"`. |
| `exporter_version` | Version of the exporter that produced the package. |
| `created_at` | Export creation time (the only non-reproducible field). |
| `source_account_id` | Stable `a_...` account ID the data came from. |
| `conversation_ids` | Stable IDs of the exported conversations. |
| `time_range` | `{ first_message_at, last_message_at }` across the exported records. |
| `record_count` | Total exported timeline records. |
| `unknown_count` | Records normalized as `unknown`. |
| `partial_count` | Records flagged partially parsed. |
| `unsupported_count` | Count of unsupported records (equal to `unknown_count` today). |
| `files[]` | One entry per package file: `path`, `kind`, `conversation_id`, `year`, `month`, `record_count`. |
| `conversations[]` | One entry per conversation: `conversation_id`, `type`, `current_name`, `record_count`, `first_message_at`, `last_message_at`. |
| `diagnostics[]` | Rolled-up export diagnostics: `severity`, `code`, `message`, `count`, `source_type`, `source_subtype`. The key is always present and is populated from the diagnostics accumulated while the exported records were ingested, so a consumer can judge completeness without reading a timeline file. |
| `source_adapter`, `source_version` | Adapter name and upstream client version recorded for the archive account. |

Null-valued optional fields are omitted from the manifest JSON.

A consumer must be able to decide which timeline files to load from the manifest alone.

---

## 15. Regeneration and idempotency

Given the same archive state, export configuration and exporter version, export content should be deterministic except for explicitly generated metadata such as export time.

Re-exporting shall:

- preserve stable conversation directory paths;
- preserve stable user IDs;
- preserve user-maintained aliases/configuration according to merge rules;
- regenerate timeline partitions without duplicate logical records;
- never require binary media files.

Shipped behaviour:

- The conversation's directory is **deleted and rewritten**, which is what makes duplicate
  partitions structurally impossible. The rewrite is staged: new timelines and catalogs are
  written to sibling temp paths first, and the prior package is only replaced once every
  replacement file is durable, so a failed or cancelled re-export cannot destroy the last
  good dataset.
- `conversations.yaml` reuses the existing `alias`.
- `identities.yaml` reuses `display_name_override` per the section 5.4 merge policy.
- `collections.yaml` is left untouched when it exists.
- `created_at` and `exporter_version` are the only fields that legitimately differ between two
  exports of unchanged archive state.

---

## 16. Expected Harness workflow

```text
manifest.json
      ↓
collections.yaml / conversations.yaml
      ↓
identify relevant stable conversation IDs
      ↓
identities.yaml
      ↓
load only required year/month JSONL files
      ↓
analysis
```

A Harness prompt should be able to reference a stable path such as:

```text
chats/groups/g_01fd893a7b21c054/2026/2026-07.jsonl
chats/groups/g_01fd893a7b21c054/2026/2026-08.jsonl
chats/groups/g_01fd893a7b21c054/2026/2026-09.jsonl
```

without depending on a mutable group title.

---

## 17. Phase 1 acceptance criteria

Phase 1 export is complete when:

1. the user can choose specific direct/group conversations or collections;
2. physical paths are stable-ID based and independent of mutable names;
3. `identities.yaml` uses the latest remark as the default display name and leaves it blank when no remark exists;
4. `conversations.yaml` provides current names and optional user aliases;
5. `collections.yaml` can group multiple chats without duplicating data;
6. timelines are monthly UTF-8 JSONL files;
7. every record follows the canonical message envelope;
8. image/voice/video/file binaries are not exported;
9. file messages retain filenames when available;
10. links and app-share records preserve the best locally obtainable original URL and semantic metadata;
11. reply relationships and forwarded bundles retain available structure;
12. unknown records are retained and counted rather than silently dropped;
13. `manifest.json` declares export/message schema versions and completeness counters;
14. a Harness can locate, load and analyze a selected conversation/time range without relying on mutable human folder names.

Status against this list at the MVP: 2–14 are met. Criterion 1 is met for specific
conversations and for the export engine's conversation-id list, but collection selection and
multi-conversation selection are not yet exposed in the application.
