"""Agent entry point: accepts the mod's TCP connection and speaks newline-delimited JSON.

Events from the mod are queued and handled one at a time by the Brain, which calls Claude and
sends commands back. Command results and state snapshots are routed to whoever awaits them.
"""

import asyncio
import hmac
import logging
import os
import sys
from typing import Callable

import anthropic

from companion_agent import __version__
from companion_agent.brain import Brain
from companion_agent.connection import ModConnection
from companion_agent.protocol import Event, Hello, HelloAck, parse

log = logging.getLogger("companion_agent")


def _load_dotenv(path: str = ".env") -> None:
    """Minimal .env support (KEY=VALUE lines) for local dev; real environment variables win."""
    try:
        with open(path, encoding="utf-8") as f:
            for line in f:
                key, sep, value = line.strip().partition("=")
                if sep and key and not key.startswith("#"):
                    os.environ.setdefault(key.strip(), value.strip())
    except FileNotFoundError:
        pass


_load_dotenv()

HOST = os.environ.get("AGENT_HOST", "127.0.0.1")
PORT = int(os.environ.get("AGENT_PORT", "7777"))
TOKEN = os.environ.get("AGENT_TOKEN", "")

HANDSHAKE_TIMEOUT = 10.0
# asyncio's default line limit is 64 KB; command results and state snapshots can be bigger.
LINE_LIMIT = 8 * 1024 * 1024
EVENT_QUEUE_SIZE = 5


def default_brain_factory(conn: ModConnection) -> Brain:
    return Brain(conn, anthropic.AsyncAnthropic())


# Tests swap this for a Brain with a fake LLM client.
brain_factory: Callable[[ModConnection], Brain] = default_brain_factory


async def _event_worker(brain: Brain, queue: "asyncio.Queue[Event]") -> None:
    while True:
        event = await queue.get()
        try:
            await brain.on_event(event)
        except anthropic.APIError as e:
            log.error("Claude API error handling %s: %s", event.name, e)
        except Exception:
            log.exception("failed handling event %s", event.name)


async def handle_mod(reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
    peer = writer.get_extra_info("peername")
    log.info("mod connected from %s", peer)
    worker = None
    try:
        hello = parse(await asyncio.wait_for(reader.readline(), HANDSHAKE_TIMEOUT))
        if not isinstance(hello, Hello) or not hmac.compare_digest(hello.token, TOKEN):
            log.warning("rejected handshake from %s", peer)
            return
        log.info("hello from mod %s, world %s", hello.mod_version, hello.world)

        conn = ModConnection(writer)
        await conn.send(HelloAck(agent_version=__version__))
        queue: asyncio.Queue[Event] = asyncio.Queue(maxsize=EVENT_QUEUE_SIZE)
        worker = asyncio.create_task(_event_worker(brain_factory(conn), queue))

        while line := await reader.readline():
            msg = parse(line)
            if msg is None:
                log.warning("unhandled message: %r", line[:200])
            elif conn.dispatch(msg):
                pass
            elif isinstance(msg, Event):
                try:
                    queue.put_nowait(msg)
                except asyncio.QueueFull:
                    log.warning("busy; dropped event %s", msg.name)
    except (asyncio.TimeoutError, ConnectionError) as e:
        log.warning("connection %s ended: %s", peer, e)
    finally:
        if worker:
            worker.cancel()
        writer.close()
        log.info("mod disconnected")


async def main() -> None:
    for stream in (sys.stdout, sys.stderr):
        stream.reconfigure(encoding="utf-8")  # Windows consoles default to a legacy code page
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")
    logging.getLogger("httpx2").setLevel(logging.WARNING)  # the SDK's HTTP client logs every request
    if not TOKEN:
        raise SystemExit("AGENT_TOKEN must be set")
    if not (os.environ.get("ANTHROPIC_API_KEY") or os.environ.get("ANTHROPIC_AUTH_TOKEN")):
        raise SystemExit("No Claude credentials: add ANTHROPIC_API_KEY=... to agent/.env (never commit it)")
    server = await asyncio.start_server(handle_mod, HOST, PORT, limit=LINE_LIMIT)
    log.info("listening on %s:%d", HOST, PORT)
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    asyncio.run(main())
