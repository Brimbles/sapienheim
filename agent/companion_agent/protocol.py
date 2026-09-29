"""Agent <-> mod protocol v0 (see PLAN.md section 6): one JSON object per line over TCP."""

from __future__ import annotations

import itertools
import json
from typing import Annotated, Any, Literal, Union

from pydantic import BaseModel, ConfigDict, Field, TypeAdapter, ValidationError


class _Msg(BaseModel):
    model_config = ConfigDict(extra="allow")


# Mod -> agent
class Hello(_Msg):
    type: Literal["hello"]
    token: str
    mod_version: str = ""
    world: str = ""


class Event(_Msg):
    type: Literal["event"]
    name: str
    data: dict[str, Any] = Field(default_factory=dict)


class CommandResult(_Msg):
    type: Literal["command_result"]
    cmd_id: str | None = None
    ok: bool
    error: str | None = None


class State(_Msg):
    type: Literal["state"]


Incoming = Annotated[Union[Hello, Event, CommandResult, State], Field(discriminator="type")]
_incoming = TypeAdapter(Incoming)


# Agent -> mod
class HelloAck(_Msg):
    type: Literal["hello_ack"] = "hello_ack"
    agent_version: str


class Command(_Msg):
    type: Literal["command"] = "command"
    cmd_id: str
    action: str
    args: dict[str, Any] = Field(default_factory=dict)


class RequestState(_Msg):
    type: Literal["request_state"] = "request_state"


_cmd_ids = itertools.count(1)


def command(action: str, **args: Any) -> Command:
    return Command(cmd_id=f"c{next(_cmd_ids)}", action=action, args=args)


def parse(line: bytes | str) -> Incoming | None:
    """Parse one line from the mod. Returns None for invalid JSON or unknown message types."""
    try:
        return _incoming.validate_python(json.loads(line))
    except (json.JSONDecodeError, ValidationError):
        return None


def encode(msg: BaseModel) -> bytes:
    return msg.model_dump_json(exclude_none=True).encode() + b"\n"
