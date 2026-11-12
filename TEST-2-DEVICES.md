# Testing GPUse between two devices — checklist

Work down this list. Don't skip ahead: each step proves one thing.

## Step 0 — Same machine (proves server + agent logic) — DONE 2026-11-12 (job #1)
- [x] Terminal 1: `cd server && dotnet run` → open http://localhost:5000
- [x] Terminal 2: `cd host-agent && node agent.js` → it registers, prints GPU name
- [x] Browser: sign up → host shows Online on Browse
- [x] Send a `python` job with prompt: `print("hello from the host")`
- [x] My Jobs: queued → running → done; download result, it says hello
- [x] Agent terminal printed the job + "done in Xs, result uploaded"

## Step 1 — Two devices, same WiFi (proves agent really runs elsewhere)
- [ ] On this PC, find LAN IP: `ip addr` (looks like 192.168.x.x)
- [ ] Copy `host-agent/` to device #2 (USB/git clone), needs Node 18+ (+ Python3)
- [ ] On device #2: `GPUSE_SERVER=http://192.168.x.x:5000 GPUSE_NAME=@device2 node agent.js`
      (Linux/macOS syntax; Windows: set the vars in the terminal first)
- [ ] Host @device2 appears Online in Browse on this PC
- [ ] Send it a python job from this PC; result was computed on device #2

## Step 2 — Internet (proves it works anywhere) — TUNNEL DONE 2026-11-12 (job #5, via trycloudflare; second physical device still pending)
- [x] Install cloudflared on this PC (pacman cloudflared)
- [x] Run server, then: `cloudflared tunnel --url http://localhost:5000`
      → it prints a public https://…trycloudflare.com address
- [ ] Device #2 on a DIFFERENT network (phone hotspot is perfect):
      `GPUSE_SERVER=https://…trycloudflare.com node agent.js`
- [x] Full job round-trip over the internet (agent claimed + returned job #5 through the tunnel from this PC; nobody touched a second device yet)

## Step 2b — Real second device as RENTER — DONE 2026-11-12
This is the reverse split: server + host agent stayed on this PC
(Quadro M3000M), the second device (Ubuntu laptop, different network)
only used a browser through the tunnel link. No agent install needed there.
- [x] Renter opened the tunnel link (bare `/` gave 404 → fixed same day
      with `UseDefaultFiles()` in Program.cs; `/index.html` worked before the fix)
- [x] Job #6 `print("hello from the host computer")` → result `6.txt`
      downloaded by the renter on the second device, contents confirmed
- [x] Job #7 failed honestly: pasted code had extra indentation →
      `IndentationError` shown to the renter (failure path works remotely too)
- [x] Job #8 heavy test: 8,000,000 SHA-256 hashes in 5.9s on this host,
      result downloaded and confirmed on the second device
- Still open (different test): second device as HOST on another network
  (Step 2 above) — the renter-side proof is done, host-side is not.

## Step 3 — Real AI image (proves the GPU bit)
- [ ] On device #2: `pip install torch diffusers transformers accelerate`
      (CUDA torch build if it has an NVIDIA GPU)
- [ ] First image job downloads the model (~2-4 GB, slow once), then returns a PNG

## Failure drills (know these answers)
- [ ] Wrong password → "Wrong password." (not a crash)
- [ ] Stop the agent → host shows Offline after ~30s, jobs stay `queued`
- [x] Send job type the host can't do → `failed` with a readable reason (proven 2026-11-12: job #3 bad python → NameError shown; jobs #2/#4 image without torch → install instructions shown)
