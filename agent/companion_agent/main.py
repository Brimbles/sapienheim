"""Agent entry point: accepts the mod's TCP connection and speaks newline-delimited JSON.

M0 skeleton: performs the hello handshake and logs everything else. The LLM brain arrives in M3.
"""

import asyncio
import hmac
import json
import logging
import os

from companion_agent import __version__

log = logging.getLogger("companion_agent")

HOST = os.environ.get("AGENT_HOST", "127.0.0.1")
PORT = int(os.environ.get("AGENT_PORT", "7777"))
TOKEN = os.environ.get("AGENT_TOKEN", "")


async def send(writer: asyncio.StreamWriter, msg: dict) -> None:
    writer.write(json.dumps(msg, separators=(",", ":")).encode() + b"\n")
    await writer.drain()


async def handle_mod(reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
    peer = writer.get_extra_info("peername")
    log.info("mod connected from %s", peer)
    try:
        hello = json.loads(await reader.readline() or b"null")
        if not isinstance(hello, dict) or hello.get("type") != "hello" or not hmac.compare_digest(
            str(hello.get("token", "")), TOKEN
        ):
            log.warning("rejected handshake from %s", peer)
            return
        log.info("hello from mod %s, world %s", hello.get("mod_version"), hello.get("world"))
        await send(writer, {"type": "hello_ack", "agent_version": __version__})

        while line := await reader.readline():
            try:
                msg = json.loads(line)
            except json.JSONDecodeError:
                log.warning("bad json: %r", line[:200])
                continue
            log.info("<- %s", msg)
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
