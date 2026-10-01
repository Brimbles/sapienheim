"""A tiny local status page for the companion. GET / for the page, GET /api/status for JSON.

Plain asyncio, no web framework. Binds to localhost by default; it shows inventory, chat and spend, so
don't expose it publicly.
"""

from __future__ import annotations

import asyncio
import json
import logging
import os

from companion_agent.status import Status

log = logging.getLogger("companion_agent.dashboard")

HOST = os.environ.get("AGENT_DASHBOARD_HOST", "127.0.0.1")
PORT = int(os.environ.get("AGENT_DASHBOARD_PORT", "7778"))

PAGE = """<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Companion status</title>
<style>
:root{--bg:#f6f4ef;--card:#fff;--ink:#1d1b17;--mute:#6b665c;--line:#e4dfd4;--ok:#2f7d4f;--bad:#b23a2b;--accent:#8a5a1f}
@media (prefers-color-scheme:dark){:root{--bg:#16140f;--card:#211e17;--ink:#ede8dd;--mute:#a39c8e;--line:#363026;--ok:#6cc08f;--bad:#e07a6a;--accent:#d9a45a}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:15px/1.45 system-ui,sans-serif}
main{max-width:980px;margin:0 auto;padding:20px 16px 40px}h1{font-size:22px;margin:0 0 4px}
.sub{color:var(--mute);margin-bottom:18px}.grid{display:grid;gap:14px;grid-template-columns:repeat(auto-fit,minmax(280px,1fr))}
.card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:14px}
.card h2{font-size:13px;letter-spacing:.06em;text-transform:uppercase;color:var(--mute);margin:0 0 10px}
.kv{display:grid;grid-template-columns:auto 1fr;gap:4px 12px}.kv span:nth-child(odd){color:var(--mute)}
.pill{display:inline-block;padding:1px 8px;border-radius:99px;font-size:12px;border:1px solid var(--line)}
.ok{color:var(--ok)}.bad{color:var(--bad)}.big{font-size:28px;font-weight:600;color:var(--accent)}
ul{list-style:none;margin:0;padding:0}li{padding:5px 0;border-bottom:1px solid var(--line)}li:last-child{border:0}
.t{color:var(--mute);font-size:12px;margin-right:6px;font-variant-numeric:tabular-nums}.k{font-size:12px;color:var(--accent);margin-right:6px}
.wide{grid-column:1/-1}table{width:100%;border-collapse:collapse}td{padding:3px 0;border-bottom:1px solid var(--line)}td:last-child{text-align:right}
</style></head><body><main>
<h1 id="title">Companion</h1><div class="sub" id="sub">loading…</div>
<div class="grid">
 <section class="card"><h2>Status</h2><div class="kv" id="status"></div></section>
 <section class="card"><h2>Spend today</h2><div class="big" id="usd">$0</div><table id="spend"></table></section>
 <section class="card"><h2>Inventory</h2><table id="inv"></table></section>
 <section class="card"><h2>Nearby</h2><ul id="nearby"></ul></section>
 <section class="card wide"><h2>Recent activity</h2><ul id="activity"></ul></section>
</div></main>
<script>
const esc=s=>String(s??"").replace(/[&<>]/g,c=>({"&":"&amp;","<":"&lt;",">":"&gt;"}[c]));
const time=t=>new Date(t*1000).toLocaleTimeString([], {hour:"2-digit",minute:"2-digit",second:"2-digit"});
async function refresh(){
 let d; try{d=await (await fetch("api/status")).json()}catch(e){document.getElementById("sub").textContent="agent not reachable";return}
 const s=d.state||{}, me=s.self||{};
 document.getElementById("title").textContent=me.name||"Companion";
 document.getElementById("sub").innerHTML=(d.connected?'<span class="ok">● connected</span>':'<span class="bad">● mod not connected</span>')+
   (d.world?` · world ${esc(d.world)}`:"")+(d.state_age_s!=null?` · state ${d.state_age_s}s old`:"");
 const prog=me.progress?Object.entries(me.progress).map(([k,v])=>`${k} ${v}`).join(", "):"";
 const rows=[["Task",me.task?`<span class="pill">${esc(me.task)}</span> ${esc(prog)}`:"(away or offline)"],
   ["Health",me.hp!=null?`${me.hp} / ${me.max_hp}`:"–"],["Position",me.pos?me.pos.map(Math.round).join(", "):"–"],
   ["Master",esc(me.master)+(me.master_nearby?" (nearby)":"")],["Queue",(me.queue||[]).map(esc).join(" → ")||"–"],
   ["Biome / time",[s.biome,s.time_of_day,s.weather].filter(Boolean).map(esc).join(" · ")||"–"],
   ["Players online",s.players_online??"–"]];
 document.getElementById("status").innerHTML=rows.map(([k,v])=>`<span>${k}</span><span>${v}</span>`).join("");
 document.getElementById("inv").innerHTML=(me.inventory||[]).map(i=>`<tr><td>${esc(i.name)}${i.equipped?' <span class="pill">equipped</span>':""}</td><td>${i.qty}</td></tr>`).join("")||"<tr><td>–</td></tr>";
 document.getElementById("nearby").innerHTML=(s.nearby||[]).slice(0,12).map(n=>`<li>${n.hostile?'<span class="bad">⚔</span> ':""}${esc(n.name)} <span class="t">${n.dist} m · ${n.hp}/${n.max_hp}</span></li>`).join("")||"<li>nothing nearby</li>";
 const sp=d.spend_today; document.getElementById("usd").textContent="$"+sp.usd.toFixed(sp.usd<1?4:2);
 document.getElementById("spend").innerHTML=Object.entries(sp.by_model).map(([m,u])=>`<tr><td>${esc(m)} · ${u.calls} calls</td><td>$${u.usd.toFixed(4)}</td></tr>`).join("");
 document.getElementById("activity").innerHTML=d.activity.map(a=>`<li><span class="t">${time(a.t)}</span><span class="k">${esc(a.kind)}</span>${esc(a.text)}</li>`).join("")||"<li>nothing yet</li>";
}
refresh(); setInterval(refresh,5000);
</script></body></html>"""


async def serve(status: Status, host: str = HOST, port: int = PORT) -> asyncio.base_events.Server:
    async def handle(reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        try:
            request_line = (await asyncio.wait_for(reader.readline(), 5)).decode("latin-1")
            while (await reader.readline()) not in (b"\r\n", b"\n", b""):
                pass  # skip headers
            path = request_line.split(" ")[1] if " " in request_line else "/"
            if path.startswith("/api/status"):
                body, ctype, code = json.dumps(status.snapshot()).encode(), "application/json", "200 OK"
            elif path in ("/", "/index.html"):
                body, ctype, code = PAGE.encode(), "text/html; charset=utf-8", "200 OK"
            else:
                body, ctype, code = b"not found", "text/plain", "404 Not Found"
            writer.write(
                f"HTTP/1.1 {code}\r\nContent-Type: {ctype}\r\nContent-Length: {len(body)}\r\n"
                f"Cache-Control: no-store\r\nConnection: close\r\n\r\n".encode() + body
            )
            await writer.drain()
        except (asyncio.TimeoutError, ConnectionError, IndexError):
            pass
        finally:
            writer.close()

    server = await asyncio.start_server(handle, host, port)
    log.info("dashboard on http://%s:%d", host, server.sockets[0].getsockname()[1])
    return server
