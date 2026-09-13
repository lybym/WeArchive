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
   │  └─ u_019af771/
   │     └─ 2026/
   │        ├─ 2026-08.jsonl
   │        └─ 2026-09.jsonl
   └─ groups/
      └─ g_01fd893a/
         └─ 2026/
            ├─ 2026-07.jsonl
            ├─ 2026-08.jsonl
            └─ 2026-09.jsonl
```

### 3.1 Path stability rule

Physical paths are derived only from stable IDs.

Display names, remarks, aliases and group titles shall never be part of the physical path.

This ensures that Harness prompts, scripts and historical references remain valid after name changes.

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
  g_01fd893a:
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
  u_7a19d382:
    source_user_id: wxid_xxxxx
    remark: 张三
    nickname: 三哥
    display_name: 张三

  u_82f119ab:
    source_user_id: wxid_yyyyy
    remark: ""
    nickname: Kevin
    display_name: ""
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

### 5.2 Manual maintenance

`identities.yaml` is user-maintainable.

Regeneration must preserve explicitly user-maintained identity fields according to a documented merge policy.

### 5.3 Group-specific names

Group nicknames may be retained as secondary metadata, but the canonical identity remains the stable `u_...` ID.

---

## 6. Conversation catalog

`conversations.yaml` maps stable IDs to current semantic metadata.

Example:

```yaml
conversations:
  g_01fd893a:
    type: group
    current_name: 华东产品创新中心工作群
    alias: east-product-center
    first_message_at: 2023-04-11T09:23:14+08:00
    last_message_at: 2026-09-13T18:32:41+08:00

  u_7a19d382:
    type: direct
    user_id: u_7a19d382
    alias: zhang-san
```

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
  - g_01fd893a
  - u_7a19d382
```

CLI syntax may evolve, but selective export is a product requirement.

---

## 8. Collections

`collections.yaml` defines reusable analysis sets without duplicating message data.

Example:

```yaml
collections:
  east-product-center:
    conversations:
      - g_01fd893a
      - g_814ac121
      - u_019af771
      - u_22b7a982

  ai-toy-project:
    conversations:
      - g_77cd993a
      - u_a919de31
      - u_991aa207
```

Primary uses:

- project analysis;
- relationship analysis;
- organizational analysis;
- recurring Harness workflows;
- repeatedly exporting the same set of conversations.

---

## 9. Timeline files

Conversation timelines use UTF-8 JSON Lines (`.jsonl`).

One physical line equals one canonical logical message/event record as defined by `MESSAGE_SCHEMA.md`.

### 9.1 Partitioning

Default partitioning:

```text
conversation / year / month
```

Example:

```text
chats/groups/g_01fd893a/2026/2026-09.jsonl
```

Monthly partitioning balances selective LLM loading, manageable file sizes, time-range analysis, incremental regeneration and simple scripting.

### 9.2 Ordering

Records shall be ordered by canonical message time with a stable secondary ordering key for equal timestamps.

---

## 10. Canonical message contract

Every JSONL line must conform to the canonical envelope defined in [MESSAGE_SCHEMA.md](MESSAGE_SCHEMA.md).

Conceptual example:

```json
{
  "id": "m_f09271",
  "conversation_id": "g_01fd893a",
  "sender_id": "u_7a19d382",
  "time": "2026-09-13T09:22:14+08:00",
  "type": "text",
  "text": "下午的材料我已经修改好了",
  "reply_to": null,
  "payload": null,
  "source": {
    "source_message_id": "...",
    "source_type": "...",
    "source_subtype": "...",
    "source_partition": "..."
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

---

## 14. Manifest

`manifest.json` describes the dataset before a consumer loads message files.

Required categories:

- `export_schema_version`;
- `message_schema_version`;
- export creation time;
- archive/source account identifier;
- selected conversations/collections;
- requested time range if any;
- included timeline files;
- per-conversation/time-partition record counts;
- unsupported/unknown/partial counters;
- exporter version.

---

## 15. Regeneration and idempotency

Given the same archive state, export configuration and exporter version, export content should be deterministic except for explicitly generated metadata such as export time.

Re-exporting shall:

- preserve stable conversation directory paths;
- preserve stable user IDs;
- preserve user-maintained aliases/configuration according to merge rules;
- regenerate timeline partitions without duplicate logical records;
- never require binary media files.

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
chats/groups/g_01fd893a/2026/2026-07.jsonl
chats/groups/g_01fd893a/2026/2026-08.jsonl
chats/groups/g_01fd893a/2026/2026-09.jsonl
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
