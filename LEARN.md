# LEARN.md — how GPUse backend works, in plain words

Memorise this file and you can explain the whole system. Every idea here
maps to one numbered part in the code.

## The 30-second version

> GPUse has 3 actors. The **server** (C#) is the middleman with a notebook
> (SQLite database). **Renters** use the website to write jobs in the
> notebook. **Host agents** (Node.js, on other people's PCs) keep asking
> the server "any job for me?", run it for real, and bring the result file
> back. Statuses in the notebook: `queued → running → done/failed`.

Why a middleman server at all? Because two home computers can't easily
talk directly (routers/NAT block incoming connections). Both sides only
make *outgoing* calls to the server, so it works over the internet with
zero router setup. The tunnel (Cloudflare) just gives the server a
public address.

## The flows

### 1. Host comes online (`host-agent/agent.js` Steps 3-4, 7)
Agent asks `nvidia-smi` what GPU it has → `POST /api/host/register`
(name + GPU) → server stores a row in `hosts` + invents a secret token →
agent saves token in `host-token.json` (so a restart = same host).
Then forever: every 3s heartbeat (`last_seen = now`), and the website
calls a host "online" if `last_seen` is < 30s old.

### 2. Renter account (`server/Program.cs` Part 4)
Signup: password is never stored — we store salt + PBKDF2 hash
(Part 3). Login checks the hash and returns a random token. The website
saves the token and sends it on every call:
`Authorization: Bearer <token>`. Server looks the token up in `users` —
that's `GetUserId()`. No token / wrong token = 401.

### 3. A job's life (the core — memorise this)
1. Renter: `POST /api/jobs {hostId, type, prompt}` → server inserts a
   `jobs` row with `status='queued'` and a fixed price (`PriceFor()`).
2. Agent loop asks `GET /api/host/jobs/next` → server finds the OLDEST
   queued job *for that host*, flips it to `running` (claiming it, so
   no two hosts take it), returns `{id, type, prompt}`.
3. Agent runs it for real (Step 5): `python` = save prompt as `job.py`,
   run `python3`, capture printout. `image` = call `runners/image_ai.py`
   which loads Stable Diffusion and saves a PNG.
4. Agent uploads: `POST /api/host/jobs/<id>/result` (form-data: file +
   seconds) → server saves file as `data/results/<id>.png|txt`, marks
   job `done`. On error: `POST .../fail {error}` → `failed` + reason.
5. Renter: `GET /api/jobs/mine` shows the table; `GET /api/jobs/<id>/result`
   downloads the file (server checks the job belongs to that user).

### 4. Why polling, not pushing?
The server never calls the agent (it can't — host is behind a router).
So the agent *asks* every 3 seconds. Simple, works everywhere, easy to
explain. The fancy upgrade would be WebSockets/SignalR — same idea,
server holding a phone line open. Polling first, always.

## The database (one file: `server/data/gpuse.db`)

- `users(id, handle, email UNIQUE, password_hash, token)`
- `hosts(id, name, gpu, token, last_seen)`
- `jobs(id, user_id, host_id, type, prompt, status, cost, seconds,
  result_file, error, created_at)`

Everything is plain SQL strings in the code — search `SELECT` /
`INSERT` / `UPDATE` in `Program.cs` and you've found every query.

## File map

| File | Memorise as |
|---|---|
| `server/Program.cs` | "the middleman" — Parts 1-6: start, DB, helpers, renter API, host API, data shapes |
| `host-agent/agent.js` | "the worker" — Steps 1-7: settings, tools, GPU detect, register, run job, upload, main loop |
| `host-agent/runners/image_ai.py` | "the artist" — argv in (prompt, outfile), Stable Diffusion, PNG out |
| `server/wwwroot/` | "the shop window" — Aleks's frontend copy + `js/api.js` |

## Honest limits (say these before anyone asks)

- `python` jobs run real code on the host — only safe because both test
  devices are Aleks's. Real product = sandboxing (containers), later.
- Prices are a fixed table, payments are fake (no real money yet).
- One token per user (new login logs out the old device) — simple on
  purpose. Password hashing (PBKDF2) is real, though.
- Polling every 3s wastes a little traffic — fine at this scale.

## Questions you should be able to answer

1. Why can't renter and host talk directly? (NAT/router — both call out to the server.)
2. Where is state stored? (SQLite file, 3 tables; result files on disk.)
3. How do we know a host is online? (heartbeat + last_seen < 30s.)
4. How does a job not get run twice? (server flips queued→running when claiming.)
5. How are passwords protected? (salt + PBKDF2 hash, never plain text.)
6. How does the host prove it's a real registered host? (secret Bearer token from registration.)
7. What changes to add video jobs? (one runner script + one else-if in agent Step 5. Nothing else.)
