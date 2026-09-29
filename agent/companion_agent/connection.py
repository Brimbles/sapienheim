"""Request/response helpers on top of the NDJSON stream to the mod."""

from __future__ import annotations

import asyncio
from typing import Any

from pydantic import BaseModel

from companion_agent.protocol import CommandResult, RequestState, State, command, encode

COMMAND_TIMEOUT = 5.0
STATE_TIMEOUT = 2.0


class ModConnection:
    def __init__(self, writer: asyncio.StreamWriter) -> None:
        self._writer = writer
        self._pending: dict[str, asyncio.Future[CommandResult]] = {}
        self._state_waiters: list[asyncio.Future[State]] = []

    async def send(self, msg: BaseModel) -> None:
        self._writer.write(encode(msg))
        await self._writer.drain()

    async def command(self, action: str, **args: Any) -> CommandResult:
        """Send a command and wait for its command_result (a timeout counts as a failure)."""
        cmd = command(action, **args)
        future: asyncio.Future[CommandResult] = asyncio.get_running_loop().create_future()
        self._pending[cmd.cmd_id] = future
        try:
            await self.send(cmd)
            return await asyncio.wait_for(future, COMMAND_TIMEOUT)
        except asyncio.TimeoutError:
            return CommandResult(type="command_result", cmd_id=cmd.cmd_id, ok=False, error="timeout")
        finally:
            self._pending.pop(cmd.cmd_id, None)

    async def request_state(self) -> dict[str, Any] | None:
        """Ask the mod for a fresh snapshot. Returns None if it doesn't answer in time."""
        future: asyncio.Future[State] = asyncio.get_running_loop().create_future()
        self._state_waiters.append(future)
        try:
            await self.send(RequestState())
            state = await asyncio.wait_for(future, STATE_TIMEOUT)
            return state.model_dump(exclude={"type"})
        except asyncio.TimeoutError:
            return None
        finally:
            if future in self._state_waiters:
                self._state_waiters.remove(future)

    def dispatch(self, msg: BaseModel) -> bool:
        """Route replies to whoever is waiting. Returns True if the message was consumed."""
        if isinstance(msg, CommandResult):
            future = self._pending.get(msg.cmd_id or "")
            if future and not future.done():
                future.set_result(msg)
            return True
        if isinstance(msg, State):
            for future in self._state_waiters:
                if not future.done():
                    future.set_result(msg)
            self._state_waiters.clear()
            return True
        return False
