# WeArchive Canonical Message Schema

## 1. Purpose

This document defines the canonical semantic representation of messages and events in WeArchive Phase 1.

The primary consumers are machines, scripts, LLMs and Harness workflows. The schema therefore optimizes for:

- semantic stability across upstream client versions;
- compact machine-readable records;
- useful `text` for LLM-first consumption;
- structured fields for deeper analysis;
- explicit reply/reference relationships;
- preservation of unknown or partially parsed records;
- source traceability without leaking upstream schema into downstream consumers.

This document is normative for message normalization and export.

---

## 2. Core design

Every exported message uses the same envelope:

```json
{
  "id": "m_01JXYZ...",
  "conversation_id": "g_01fd893a",
  "sender_id": "u_7a19d382",
  "time": "2026-09-13T14:32:17+08:00",
  "type": "text",
  "text": "这个方案我下午再确认一下。",
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

The three semantic layers are:

```text
Message
├─ text              # LLM/search-first semantic representation
├─ payload           # type-specific structured semantics
└─ source            # engineering provenance and reprocessing support
```

The schema must not mirror upstream WeChat XML/database structures directly.

---

## 3. Common envelope

### 3.1 Required fields

- `id`: stable WeArchive message ID.
- `conversation_id`: stable direct/group conversation ID.
- `time`: canonical message/event timestamp in ISO 8601 with timezone.
- `type`: normalized semantic message type.
- `text`: compact semantic representation suitable for search and LLM consumption.

### 3.2 Sender

`sender_id` is the canonical sender identity when resolvable.

If no stable sender can be resolved, `sender_id` may be null. The system must not invent an identity.

Human names are resolved through `identities.yaml`, not embedded as canonical identity keys in timeline files.

### 3.3 Payload

`payload` contains type-specific structured semantics.

It must contain product-meaningful information only. Upstream implementation details should remain in `source` or optional raw/debug storage.

### 3.4 Source

`source` provides enough provenance to diagnose or reprocess a record.

Recommended fields where available:

- `source_message_id`
- `source_type`
- `source_subtype`
- `source_partition`
- `source_order_key`

Source fields are not the primary LLM interface.

---

## 4. Canonical message types

Phase 1 normalized types:

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

`quote` is intentionally not a standalone content type. Quoting/replying is represented by `reply_to` because it is a relationship between messages, not a content medium.

Unknown upstream types must map to `unknown`; they must never be silently discarded.

---

## 5. Text message

```json
{
  "type": "text",
  "text": "下午三点开会。",
  "payload": null
}
```

No additional payload is required unless product-meaningful structured information exists.

---

## 6. Reply / quote relationship

A reply should preserve both relationship and locally available quote snapshot.

```json
{
  "id": "m_002",
  "type": "text",
  "text": "我同意这个方案。",
  "reply_to": {
    "message_id": "m_001",
    "sender_id": "u_01",
    "text": "下午三点开会。"
  }
}
```

Rules:

1. `message_id` is used when the referenced message can be resolved to a canonical archived message.
2. `reply_to.text` preserves the locally available quote snapshot when present.
3. If the original cannot be resolved, `message_id` remains null while the available quoted sender/text may still be retained.
4. The system must not fabricate a target message ID.

This supports both conversation graph analysis and semantic fallback when historical source data is incomplete.

---

## 7. Media events without binary export

Phase 1 exports no media binaries.

### 7.1 Image

```json
{
  "type": "image",
  "text": "[图片]",
  "payload": null
}
```

### 7.2 Video

```json
{
  "type": "video",
  "text": "[视频]",
  "payload": null
}
```

### 7.3 Voice

```json
{
  "type": "voice",
  "text": "[语音]",
  "payload": {
    "duration_seconds": 28
  }
}
```

If duration is not reliably available, omit it or use null. Do not estimate.

### 7.4 Emoji / sticker

```json
{
  "type": "emoji",
  "text": "[表情]",
  "payload": null
}
```

No OCR, ASR or visual understanding is generated in Phase 1.

---

## 8. File message

Binary files are not exported. The original file name is the primary retained semantic property.

```json
{
  "type": "file",
  "text": "[文件] 华东中心项目汇报V8.pptx",
  "payload": {
    "filename": "华东中心项目汇报V8.pptx",
    "extension": ".pptx",
    "size_bytes": 1839201
  }
}
```

Only `filename` is required when available. Other fields are optional and must not be guessed.

If the filename cannot be resolved:

```json
{
  "type": "file",
  "text": "[文件]",
  "payload": {
    "filename": null
  }
}
```

---

## 9. Web link

URLs must exist as structured fields; they must not exist only inside `text`.

```json
{
  "type": "link",
  "text": "[链接] 人工智能产业发展报告\nhttps://example.com/article/123",
  "payload": {
    "title": "人工智能产业发展报告",
    "description": "可获得的摘要",
    "original_url": "https://example.com/article/123",
    "fallback_url": null,
    "source_app": null
  }
}
```

Rules:

1. Prefer a locally identifiable underlying/original content URL.
2. If only a wrapper/tracking/redirect URL is available, store it as `fallback_url` and keep `original_url` null unless its original-target semantics are known.
3. Missing URL metadata must not cause the message to be dropped.
4. Phase 1 does not remotely crawl the destination webpage as part of canonical export.

---

## 10. App-share / third-party forwarded content

Used for content shared from another application or represented as an app/card payload.

```json
{
  "type": "app_share",
  "text": "[APP分享][小红书] 上海周末去哪玩\nhttps://example.com/original",
  "payload": {
    "source_app": "小红书",
    "app_id": "available-if-present",
    "title": "上海周末去哪玩",
    "description": "可获得的摘要",
    "original_url": "https://example.com/original",
    "fallback_url": "https://weixin.qq.com/redirect/...",
    "page_path": null
  }
}
```

Extraction priority:

```text
confirmed original_url
    > fallback/wrapper URL
    > app_id + page_path
    > title/description only
```

All locally obtainable semantic fields should be preserved without inventing missing values.

---

## 11. Mini program

Mini programs must not be forced into the normal web-link model because many do not expose a public URL.

```json
{
  "type": "mini_program",
  "text": "[小程序] XX商城 - 商品详情",
  "payload": {
    "app_name": "XX商城",
    "app_id": "wx123456",
    "title": "商品详情",
    "page_path": "pages/product?id=123",
    "original_url": null
  }
}
```

If a valid corresponding public URL is locally available, it may also be retained.

---

## 12. Forwarded chat bundle

Merged/forwarded chat records should be represented structurally rather than flattened into an opaque block.

```json
{
  "type": "forward_bundle",
  "text": "[合并转发] 项目讨论聊天记录，共 12 条",
  "payload": {
    "title": "项目讨论聊天记录",
    "item_count": 12,
    "items": [
      {
        "sender_name": "张三",
        "sender_id": null,
        "time": "2026-09-12T14:31:00+08:00",
        "type": "text",
        "text": "第一版价格是不是太高了？"
      }
    ]
  }
}
```

Rules:

- nested sender identity may be unresolved;
- `sender_name` should be retained as presented in the forwarded record;
- add `sender_id` only when identity resolution is sufficiently reliable;
- nested unsupported item types must not cause the whole bundle to be dropped.

---

## 13. System events

System messages remain first-class timeline events.

```json
{
  "type": "system",
  "text": "张三邀请李四加入了群聊",
  "payload": {
    "event": "member_join",
    "actor_ids": ["u_01"],
    "target_ids": ["u_02"]
  }
}
```

If event structure cannot be reliably resolved, retain the semantic system text and leave structured fields absent/null.

Possible normalized system events may include:

- member join/leave;
- group-name change;
- ownership/admin change;
- conversation notices;
- other locally represented system events.

---

## 14. Revoke event

```json
{
  "type": "revoke",
  "text": "[撤回消息] 张三撤回了一条消息",
  "payload": {
    "operator_id": "u_01",
    "revoked_message_id": "m_998",
    "revoked_text": null
  }
}
```

Retain revoked content only when it is legitimately available in the local archived/source state. Do not infer or reconstruct unavailable content.

---

## 15. Transfer / red packet

### Red packet

```json
{
  "type": "red_packet",
  "text": "[红包]",
  "payload": null
}
```

### Transfer

```json
{
  "type": "transfer",
  "text": "[转账] 200.00 元",
  "payload": {
    "amount": "200.00",
    "currency": "CNY",
    "status": "accepted"
  }
}
```

Financial attributes are optional. Preserve them only when their semantics are reliably understood from the local source record.

---

## 16. Location

```json
{
  "type": "location",
  "text": "[位置] 南京南站",
  "payload": {
    "label": "南京南站",
    "address": "南京市雨花台区...",
    "latitude": 31.968,
    "longitude": 118.796
  }
}
```

Use only reliably available fields.

---

## 17. Contact card

```json
{
  "type": "contact_card",
  "text": "[联系人名片] 张三",
  "payload": {
    "display_name": "张三",
    "source_user_id": "wxid_xxx"
  }
}
```

The card target is not automatically assumed to be an existing canonical `u_...` identity unless identity resolution is reliable.

---

## 18. Unknown message

Unsupported or unrecognized records must be preserved.

```json
{
  "type": "unknown",
  "text": "[未识别消息]",
  "payload": {
    "source_type": "49",
    "source_subtype": "57",
    "raw_summary": null
  }
}
```

Rules:

- never silently `continue`/drop an unknown message;
- count unknown records in diagnostics and export manifest;
- preserve enough source-type information to support later parser improvements;
- raw opaque payload retention is optional and should be separate from the canonical semantic fields.

---

## 19. Semantic text rules

`text` is the default field consumed by LLM/Harness workflows.

It must be:

- deterministic;
- concise;
- semantically useful;
- plain UTF-8 text;
- free from unnecessary source XML/implementation details.

Examples:

```text
文本        -> 下午三点开会。
图片        -> [图片]
视频        -> [视频]
语音        -> [语音]
文件        -> [文件] 华东中心项目汇报V8.pptx
链接        -> [链接] 标题\nhttps://...
APP分享     -> [APP分享][来源] 标题\nhttps://...
小程序      -> [小程序] 应用名 - 标题
系统消息    -> 张三邀请李四加入了群聊
未知消息    -> [未识别消息]
```

Structured data remains available in `payload` for programs requiring more detail.

---

## 20. Null / missing / unknown semantics

The implementation must distinguish:

- unavailable/not present in source;
- known empty value;
- unsupported by current parser;
- intentionally excluded by Phase 1 policy.

Do not substitute guessed values to avoid nulls.

---

## 21. Schema versioning

The canonical exported schema must be versioned.

`manifest.json` shall expose at minimum:

```json
{
  "export_schema_version": "1.0",
  "message_schema_version": "1.0"
}
```

Backward-incompatible changes require a version bump and migration/compatibility notes.

---

## 22. Acceptance criteria

Phase 1 message normalization is complete when:

1. all supported messages use the common envelope;
2. `text` alone provides useful semantic context for LLMs for all normalized types;
3. type-specific data is isolated in `payload`;
4. reply relationships are structural and retain quote snapshots where available;
5. link/app-share records expose the best locally obtainable original URL and metadata;
6. files preserve filenames without exporting file binaries;
7. media events remain textual and do not require binary export;
8. forwarded bundles preserve available nested textual content;
9. unknown records are retained and counted;
10. no parser invents unavailable identities, URLs, amounts or content.
