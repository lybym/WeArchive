from datetime import UTC, datetime

from wearchive.model import Message


def test_message_model_round_trip() -> None:
    message = Message(
        message_id="m1",
        conversation_id="c1",
        timestamp=datetime(2026, 1, 1, tzinfo=UTC),
        kind="text",
        text="hello",
    )

    assert message.message_id == "m1"
    assert message.kind == "text"
    assert message.text == "hello"
