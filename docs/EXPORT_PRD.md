# WeArchive Phase 1 Export PRD

## 1. Purpose

This document defines the first-phase export requirements for WeArchive.

The export is designed for **machine processing, LLM / Harness analysis, scripting, search, statistics and downstream automation**. It is not designed as a human-facing chat viewer.

The export must therefore optimize for:

- stable paths;
- stable identifiers;
- plain-text storage;
- deterministic structure;
- easy selective loading;
- compact context for LLMs;
- reliable identity resolution;
- long-term compatibility;
- no dependency on binary media files.

This document is normative for Phase 1 export implementation.

---

## 2. Phase 1 scope

### 2.1 In scope

Phase 1 export shall support:

- selecting specific direct chats and group chats for export;
- exporting message timelines as JSONL;
- splitting long timelines by conversation / year / month;
- stable conversation directory IDs independent of display names;
- a maintainable identity mapping file;
- a maintainable conversation catalog;
- optional named collections for repeatedly exporting related chats;
- text representation of files, images, voice, video and other non-text message events;
- retaining file names when a file message is sent;
- extracting available link metadata and original URLs from link/app-share messages;
- preserving quote/reply relationships where available;
- preserving provenance fields needed to trace an exported message back to the archive/source record.

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
- perform remote crawling of linked webpages as part of the canonical export;
- use current display names as physical folder names.

---

## 3. Canonical export package

The standard export root shall be a plain-text dataset.

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

Physical conversation paths shall be derived only from stable IDs.

Display names, remarks, aliases and group names shall **never** be part of the physical path.

Rationale:

- group names can change;
- personal remarks can change;
- nicknames can change;
- names can contain unsupported filesystem characters;
- multiple people/groups can have identical names;
- LLM/Harness references should remain valid after metadata changes.

---

## 4. Conversation IDs and directory naming

### 4.1 Direct chat

Direct chat directory:

```text
chats/direct/u_<stable-id>/
```

The stable ID represents the other participant within the archived account context.

### 4.2 Group chat

Group chat directory:

```text
chats/groups/g_<stable-id>/
```

The stable ID represents the group conversation and shall not change when the group title changes.

### 4.3 Human-maintainable alias

A conversation may have an optional human-maintained alias in `conversations.yaml`.

Example:

```yaml
g_01fd893a:
  type: group
  alias: east-product-center
  current_name: 华东产品创新中心工作群
```

Aliases are lookup conveniences only. They shall not determine physical paths.

---

## 5. Identity mapping

Identity resolution shall be centralized in `identities.yaml`.

The export must not rely on the sender name embedded in historical messages as the canonical identity.

### 5.1 Required fields

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

### 5.2 Display-name rule

Default `display_name` shall be generated from the **latest available remark**.

Rule:

```text
if latest_remark is non-empty:
    display_name = latest_remark
else:
    display_name = ""
```

The nickname shall not automatically replace a missing remark.

This rule applies to both:

- friends/direct-chat identities;
- people appearing in group chats.

### 5.3 Manual maintenance

`identities.yaml` is a user-maintainable configuration file.

The system shall support preserving user-edited identity fields when the export is regenerated.

Automatic refresh shall not silently overwrite an explicit user-maintained `display_name` or alias without a documented merge policy.

### 5.4 Group-specific names

If group-specific member names are available, they may be preserved as secondary metadata, but they shall not replace the stable user ID.

Example:

```yaml
group_names:
  g_01fd893a:
    u_7a19d382: 老张
```

The canonical person remains `u_7a19d382`.

---

## 6. Conversation catalog

`conversations.yaml` shall describe exported and exportable conversations.

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

### 6.1 Catalog responsibilities

The catalog shall allow a user or Harness to answer:

- which stable ID corresponds to a known person/group;
- whether the conversation is direct or group;
- what the current name is;
- what user-maintained alias should be used in prompts/configuration;
- what time range is available.

---

## 7. Selective export

The user shall be able to explicitly select which conversations are included.

Phase 1 shall support selection by at least:

- stable conversation ID;
- maintained alias;
- collection.

The implementation may expose this through CLI flags, an export configuration file, or both.

Example conceptual configuration:

```yaml
include:
  - g_01fd893a
  - u_7a19d382
```

Exporting all chats must not be the only supported mode.

---

## 8. Collections

`collections.yaml` shall allow users to define stable analysis sets containing multiple conversations.

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

Collections are logical groupings only and shall not duplicate message data.

Primary use cases:

- project analysis;
- relationship analysis;
- organizational analysis;
- topic-specific Harness workflows;
- repeated export of the same set of chats.

---

## 9. Timeline file format

Conversation timelines shall use UTF-8 JSON Lines (`.jsonl`).

One physical line equals one logical message/event record.

### 9.1 Partitioning

Default partitioning:

```text
conversation / year / month
```

Example:

```text
chats/groups/g_01fd893a/2026/2026-09.jsonl
```

### 9.2 Why monthly files

Monthly partitioning is required to balance:

- selective LLM context loading;
- manageable file sizes;
- long-term archives;
- time-range analysis;
- incremental regeneration;
- simple scripting.

### 9.3 Ordering

Within one JSONL file, records shall be ordered by canonical message time, with a stable secondary ordering key for equal timestamps.

---

## 10. Canonical message record

A Phase 1 message record should remain compact.

Example:

```json
{
  "id": "m_f09271",
  "time": "2026-09-13T09:22:14+08:00",
  "sender": "u_7a19d382",
  "type": "text",
  "text": "下午的材料我已经修改好了",
  "reply_to": null,
  "source": {
    "message_id": "...",
    "partition": "..."
  }
}
```

### 10.1 Required logical fields

Each record shall expose, where applicable:

- stable message ID;
- timestamp;
- sender stable ID;
- normalized message type;
- normalized text representation;
- reply/reference relationship;
- minimal provenance needed for traceability.

### 10.2 Null and missing semantics

The schema shall distinguish between:

- field not supported / unavailable;
- known empty value;
- value intentionally omitted by export policy.

The concrete JSON schema shall be versioned before implementation is considered complete.

---

## 11. Message type normalization

### 11.1 Text

```json
{
  "type": "text",
  "text": "项目材料下午再确认一次"
}
```

### 11.2 Image

No image binary shall be exported.

```json
{
  "type": "image",
  "text": "[图片]"
}
```

### 11.3 Video

No video binary shall be exported.

```json
{
  "type": "video",
  "text": "[视频]"
}
```

### 11.4 Voice

No audio binary or ASR text shall be generated in Phase 1.

If duration is available locally it may be retained.

```json
{
  "type": "voice",
  "text": "[语音]",
  "duration_seconds": 37
}
```

### 11.5 File

The transferred binary file shall not be exported.

The original file name shall be retained when available.

```json
{
  "type": "file",
  "text": "[文件] 华东中心项目汇报V8.pptx",
  "file_name": "华东中心项目汇报V8.pptx"
}
```

If the file name cannot be resolved:

```json
{
  "type": "file",
  "text": "[文件]",
  "file_name": null
}
```

### 11.6 Emoji / sticker

```json
{
  "type": "emoji",
  "text": "[表情]"
}
```

### 11.7 System event

Examples include group membership or conversation system notices.

```json
{
  "type": "system",
  "text": "[系统消息] 张三邀请李四加入了群聊"
}
```

Unsupported types shall not be silently discarded. They shall be exported using an `unsupported`/`unknown` representation with available text metadata and a diagnostic counter.

---

## 12. Links and forwarded app content

This is a Phase 1 core requirement.

When a message contains a web link or content forwarded/shared from another application, WeArchive shall extract as much structured information as is available from the local source record.

### 12.1 Link message

Target representation:

```json
{
  "type": "link",
  "text": "[链接] 某篇文章标题",
  "link": {
    "title": "某篇文章标题",
    "description": "可获得的摘要",
    "source_app": "",
    "original_url": "https://example.com/article/123"
  }
}
```

### 12.2 App-share / forwarded content

For content forwarded from another application or mini-app/card-like source, the exporter shall preserve all locally available semantic fields.

Target representation:

```json
{
  "type": "app_share",
  "text": "[转发内容] 标题",
  "app_share": {
    "source_app": "某APP",
    "title": "标题",
    "description": "摘要",
    "original_url": "https://example.com/original",
    "app_id": "available-if-present",
    "page_path": "available-if-present"
  }
}
```

### 12.3 Original URL requirement

If a usable original URL is present anywhere in the locally available message/card payload, WeArchive shall expose it as `original_url`.

The extractor should prefer the underlying/original content URL over display/tracking/wrapper URLs when the distinction can be determined from local metadata.

If only a wrapper URL is available, it may be retained while clearly marking that it is not confirmed as the original URL.

If no URL can be resolved from local data:

```json
"original_url": null
```

The absence of a resolvable URL must not cause the message itself to be dropped.

### 12.4 Remote content boundary

Phase 1 canonical export shall not require fetching the remote webpage or downloading remote app content.

The requirement is to preserve locally obtainable metadata and the best available original URL, not to crawl the target webpage.

---

## 13. Reply / quote relationships

When the source contains a resolvable reply/quote relationship, export it structurally rather than flattening it only into text.

Example:

```json
{
  "id": "m_200",
  "time": "2026-09-13T09:24:51+08:00",
  "sender": "u_82f119ab",
  "type": "text",
  "text": "我同意这个方案",
  "reply_to": "m_173"
}
```

If the referenced original message cannot be resolved, preserve the available quoted text as secondary metadata rather than inventing a message ID.

---

## 14. Manifest

`manifest.json` shall describe the exported dataset itself.

Required categories include:

- export schema version;
- export creation time;
- archive/source account identifier;
- selected conversations/collections;
- requested time range if any;
- included timeline files;
- record counts;
- unsupported/partial record counters;
- exporter version.

The manifest allows a Harness to inspect the dataset before loading chat content.

---

## 15. Regeneration and idempotency

Export generation shall be deterministic for the same archive state, configuration and exporter version except for explicitly documented generated metadata such as export time.

Re-exporting shall:

- preserve stable conversation directory paths;
- preserve stable user IDs;
- preserve user-maintained aliases/configuration according to merge rules;
- replace or regenerate timeline partitions without creating duplicate records;
- not require binary media files.

---

## 16. LLM / Harness usage model

The expected workflow is:

```text
manifest.json
      ↓
conversations.yaml / collections.yaml
      ↓
identify relevant conversation IDs
      ↓
identities.yaml
      ↓
load only relevant year/month JSONL files
      ↓
analysis
```

Example task context:

```text
Collection: east-product-center
Time range: 2026-07-01 to 2026-09-30
Conversation: g_01fd893a
Files:
- chats/groups/g_01fd893a/2026/2026-07.jsonl
- chats/groups/g_01fd893a/2026/2026-08.jsonl
- chats/groups/g_01fd893a/2026/2026-09.jsonl
Identity map:
- identities.yaml
Conversation catalog:
- conversations.yaml
```

No human-readable transcript layer is required for this workflow.

---

## 17. Acceptance criteria

Phase 1 export is accepted when all of the following are true:

1. A user can select one or more direct/group conversations for export.
2. Exported physical paths use stable IDs rather than mutable names.
3. Direct chats are stored below `chats/direct/u_<id>/`.
4. Group chats are stored below `chats/groups/g_<id>/`.
5. Timelines are UTF-8 JSONL and are partitioned by year/month.
6. `identities.yaml` maps stable IDs to latest remarks and preserves blank display names when no remark exists.
7. `conversations.yaml` maps stable conversation IDs to current names and optional aliases.
8. `collections.yaml` can define reusable groups of conversations without duplicating data.
9. Images, videos, voice messages and transferred file bodies are not exported.
10. File messages retain the original file name when available.
11. Link messages retain title/description/original URL when available.
12. Forwarded/app-share messages retain available source app, title, description and original URL.
13. Missing URLs/media/file contents do not cause otherwise valid messages to be dropped.
14. Reply/quote relationships are represented structurally when resolvable.
15. Unsupported message types are represented and counted rather than silently discarded.
16. A Harness can determine which files to load using only manifest/catalog/configuration files and stable IDs.
17. Re-exporting the same dataset does not change conversation paths because a display name changed.
18. The export contains no required binary media directory.
19. No derived LLM/chunk layer is generated in Phase 1.
20. The implementation and tests trace back to the requirements in this document.

---

## 18. Deferred decisions

The following are intentionally deferred to later design/implementation documents:

- exact stable-ID generation algorithm;
- exact JSON Schema version and optional-field policy;
- merge policy for auto-discovered vs manually edited identity metadata;
- whether direct-chat conversation ID and user ID are always identical internally;
- support for Parquet or DuckDB export;
- remote webpage enrichment;
- OCR/ASR/media understanding;
- LLM-specific preprocessing or chunking.

Any decision in these areas that changes observable export behavior must update this PRD or an associated ADR before implementation.
