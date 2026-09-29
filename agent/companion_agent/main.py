"""Agent entry point: accepts the mod's TCP connection and speaks newline-delimited JSON.

M2: performs the hello handshake and echoes player chat back as `say`. The LLM brain arrives in M3.
"""

import asyncio
import hmac
import logging
import os

from companion_agent import __version__
from companion_agent.protocol import CommandResult, Event, Hello, HelloAck, State, command, encode, parse

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


async def send(writer: asyncio.StreamWriter, msg) -> None:
    writer.write(encode(msg))
    await writer.drain()


async def on_event(writer: asyncio.StreamWriter, event: Event) -> None:
    if event.name == "player_chat":
        player = event.data.get("player", "someone")
        text = str(event.data.get("text", "")).strip()
        reply = f"You said: {text}" if text else f"Yes, {player}?"
        log.info("chat from %s (%s): %s", player, event.data.get("via"), text)
        await send(writer, command("say", text=reply))
    else:
        log.info("event %s: %s", event.name, event.data)


async def handle_mod(reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
    peer = writer.get_extra_info("peername")
    log.info("mod connected from %s", peer)
    try:
        hello = parse(await asyncio.wait_for(reader.readline(), HANDSHAKE_TIMEOUT))
        if not isinstance(hello, Hello) or not hmac.compare_digest(hello.token, TOKEN):
            log.warning("rejected handshake from %s", peer)
            return
        log.info("hello from mod %s, world %s", hello.mod_version, hello.world)
        await send(writer, HelloAck(agent_version=__version__))

        while line := await reader.readline():
            msg = parse(line)
            if isinstance(msg, Event):
                await on_event(writer, msg)
            elif isinstance(msg, CommandResult):
                if not msg.ok:
                    log.warning("command %s failed: %s", msg.cmd_id, msg.error)
            elif isinstance(msg, State):
                log.info("state: %s", msg.model_dump())
            else:
                log.warning("unhandled message: %r", line[:200])
    except (asyncio.TimeoutError, ConnectionError) as e:
        log.warning("connection %s ended: %s", peer, e)
    finally:
        writer.close()
        log.info("mod disconnected")


async def main() -> None:
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")
    if not TOKEN:
        raise SystemExit("AGENT_TOKEN must be set")
    server = await asyncio.start_server(handle_mod, HOST, PORT)
    log.info("listening on %s:%d", HOST, PORT)
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    asyncio.run(main())
