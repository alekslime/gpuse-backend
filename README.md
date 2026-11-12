# GPUse Backend — the real working system

Peer-to-peer GPU marketplace, working end-to-end (Aleks's own extra project,
separate from the team frontend repo — nothing here touches their work).

**Renter** (any browser) → signs up, picks a real host computer, sends a job →
**Server** (C#/.NET, this folder's `server/`) queues it →
**Host agent** (Node.js, `host-agent/`) running on *another computer*
automatically picks it up, runs it for real, uploads the result →
renter downloads it from My Jobs.

## Pieces

| Folder | What | Language |
|---|---|---|
| `server/` | Web server + API + SQLite database + serves the website copy | C# / ASP.NET Core (`Program.cs`, one file) |
| `host-agent/` | Runs on the host PC: registers GPU, polls for jobs, runs them, uploads results | Node.js (`agent.js`, one file, no npm packages) |
| `host-agent/runners/` | Real work scripts the agent calls | Python (`image_ai.py`, more later) |
| `server/wwwroot/` | Copy of Aleks's frontend pages + `js/api.js` talking to the server | HTML/CSS/JS |

## Run it (dev, same machine first)

Terminal 1 — server:
```bash
cd server
dotnet run
# -> http://localhost:5000
```

Terminal 2 — host agent (pretend second computer):
```bash
cd host-agent
node agent.js
```

Browser: open http://localhost:5000, sign up, send a `python` job
(e.g. prompt = `print("hello from the other computer")`), watch it go
queued → running → done, download the result `.txt`.

## Two real devices + internet

See `TEST-2-DEVICES.md`. Short version: expose the server with a
Cloudflare Tunnel, point the agent on device #2 at that public URL
(`GPUSE_SERVER=https://… node agent.js`). The agent only makes
*outgoing* connections — no router/port-forwarding setup on the host.

## Job types

- `python` — prompt is Python code, result is what it printed (`.txt`)
- `image` — prompt is words, real Stable Diffusion PNG (host needs
  `pip install torch diffusers transformers accelerate`)
- `audio` / `3d` / `video` — runners added one at a time, same pipeline

## Learn it

`LEARN.md` explains every flow in plain words — that's the file to
memorise from. Code is written long-but-simple on purpose: one file per
job, numbered parts, no clever tricks.
