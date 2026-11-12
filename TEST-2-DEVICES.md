# Testing GPUse between two devices — checklist

Work down this list. Don't skip ahead: each step proves one thing.

## Step 0 — Same machine (proves server + agent logic)
- [ ] Terminal 1: `cd server && dotnet run` → open http://localhost:5000
- [ ] Terminal 2: `cd host-agent && node agent.js` → it registers, prints GPU name
- [ ] Browser: sign up → host shows Online on Browse
- [ ] Send a `python` job with prompt: `print("hello from the host")`
- [ ] My Jobs: queued → running → done; download result, it says hello
- [ ] Agent terminal printed the job + "done in Xs, result uploaded"

## Step 1 — Two devices, same WiFi (proves agent really runs elsewhere)
- [ ] On this PC, find LAN IP: `ip addr` (looks like 192.168.x.x)
- [ ] Copy `host-agent/` to device #2 (USB/git clone), needs Node 18+ (+ Python3)
- [ ] On device #2: `GPUSE_SERVER=http://192.168.x.x:5000 GPUSE_NAME=@device2 node agent.js`
      (Linux/macOS syntax; Windows: set the vars in the terminal first)
- [ ] Host @device2 appears Online in Browse on this PC
- [ ] Send it a python job from this PC; result was computed on device #2

## Step 2 — Internet (proves it works anywhere)
- [ ] Install cloudflared on this PC: https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/downloads/
- [ ] Run server, then: `cloudflared tunnel --url http://localhost:5000`
      → it prints a public https://…trycloudflare.com address
- [ ] Device #2 on a DIFFERENT network (phone hotspot is perfect):
      `GPUSE_SERVER=https://…trycloudflare.com node agent.js`
- [ ] Full job round-trip over the internet, nobody touched device #2

## Step 3 — Real AI image (proves the GPU bit)
- [ ] On device #2: `pip install torch diffusers transformers accelerate`
      (CUDA torch build if it has an NVIDIA GPU)
- [ ] First image job downloads the model (~2-4 GB, slow once), then returns a PNG

## Failure drills (know these answers)
- [ ] Wrong password → "Wrong password." (not a crash)
- [ ] Stop the agent → host shows Offline after ~30s, jobs stay `queued`
- [ ] Send job type the host can't do → `failed` with a readable reason
