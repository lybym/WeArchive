from __future__ import annotations

from datetime import datetime
from typing import Literal

from pydantic import BaseModel, Field


ChatType = Literal["private", "group", "official_account", "unknown"]
MessageKind = Literal["text", "image", "voice", "video", "file", "system", "unknown"]


class Conversation(BaseModel):
    conversation_id: str
    title: str | None = None
    chat_type: ChatType = "unknown"


class Message(BaseModel):
    message_id: str
    conversation_id: str
    sender_id: str | None = None
    timestamp: datetime
    kind: MessageKind = "unknown"
    text: str | None = None
    source_id: str | None = None
    source_shard: str | None = None
    metadata: dict[str, object] = Field(default_factory=dict)
