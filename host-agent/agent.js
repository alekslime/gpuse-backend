// ============================================================================
//  GPUse HOST AGENT  (Node.js — no npm packages, only built-in Node)
//  ----------------------------------------------------------------------------
//  Run this file on the HOST computer (the one sharing its GPU):
//
//      node agent.js
//
//  What it does, in a loop, forever:
//    1. Register this computer with the server (first run only)
//    2. Say "I am alive" every 5 seconds            (heartbeat)
//    3. Ask the server "any job for me?"             (polling)
//    4. If yes: run the job FOR REAL on this computer
//    5. Upload the result file back to the server
//
//  How to read this file: 7 numbered steps, top to bottom.
// ============================================================================

import { execFile, spawn } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

// ============================================================================
//  STEP 1 — SETTINGS  (edit these two lines)
// ============================================================================

// Where is the server? During local testing this is Aleks's PC.
// Later this becomes the public tunnel/internet address, e.g.
//   https://something.trycloudflare.com
const SERVER_URL = process.env.GPUSE_SERVER || "http://127.0.0.1:5000";

// A friendly name for this computer, shown on the website, e.g. "@aleks-pc"
const HOST_NAME = process.env.GPUSE_NAME || "host-pc";

// ============================================================================
//  STEP 2 — SMALL TOOLS
// ============================================================================

// Folder of this agent.js file (runners/ and work/ live next to it)
const AGENT_FOLDER = path.dirname(fileURLToPath(import.meta.url));
const WORK_FOLDER = path.join(AGENT_FOLDER, "work");
const TOKEN_FILE = path.join(AGENT_FOLDER, "host-token.json");

fs.mkdirSync(WORK_FOLDER, { recursive: true });

// Wait for N milliseconds. Used to pace the loop ("sleep 2 seconds").
function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

// Run a command and wait for it to finish. Returns { ok, stdout, stderr }.
// We use execFile (a list of words), never a shell string — safer and clearer.
function runCommand(command, args, timeoutMs) {
  return new Promise((resolve) => {
    execFile(
      command,
      args,
      { timeout: timeoutMs, maxBuffer: 10 * 1024 * 1024 },
      (error, stdout, stderr) => {
        if (error) {
          resolve({ ok: false, stdout: String(stdout), stderr: String(stderr || error.message) });
        } else {
          resolve({ ok: true, stdout: String(stdout), stderr: String(stderr) });
        }
      }
    );
  });
}

// Send JSON to the server and read JSON back.
// token = our host secret (after Step 4) or "" before we have one.
async function callServer(urlPath, method, body, token) {
  const response = await fetch(SERVER_URL + urlPath, {
    method: method,
    headers: {
      "Content-Type": "application/json",
      ...(token ? { Authorization: "Bearer " + token } : {}),
    },
    body: body ? JSON.stringify(body) : undefined,
  });

  const text = await response.text();
  let data = null;
  try { data = JSON.parse(text); } catch { data = null; }

  if (!response.ok) {
    throw new Error("Server said " + response.status + ": " + text);
  }
  return data;
}

// ============================================================================
//  STEP 3 — FIND OUT WHAT GPU THIS COMPUTER HAS
//  We ask NVIDIA's own tool: nvidia-smi
//  If there is no NVIDIA GPU, we honestly report "CPU only".
// ============================================================================

async function detectGpu() {
  const result = await runCommand(
    "nvidia-smi",
    ["--query-gpu=name,memory.total", "--format=csv,noheader"],
    10_000
  );

  if (result.ok && result.stdout.trim() !== "") {
    // Example answer: "NVIDIA GeForce RTX 4090, 24564 MiB"
    return result.stdout.trim().split("\n")[0].trim();
  }
  return "CPU only (no NVIDIA GPU found)";
}

// ============================================================================
//  STEP 4 — REGISTER THIS COMPUTER (first run only)
//  The server gives us: our host id + a secret token.
//  We save the token to host-token.json so next run we are the same host.
// ============================================================================

async function registerIfNeeded(gpuName) {
  if (fs.existsSync(TOKEN_FILE)) {
    const saved = JSON.parse(fs.readFileSync(TOKEN_FILE, "utf8"));
    console.log("Already registered as host #" + saved.hostId);
    return saved;
  }

  console.log("Registering this computer with the server...");
  const answer = await callServer("/api/host/register", "POST", {
    name: HOST_NAME,
    gpu: gpuName,
  }, "");

  const saved = { hostId: answer.hostId, token: answer.token };
  fs.writeFileSync(TOKEN_FILE, JSON.stringify(saved, null, 2));
  console.log("Registered as host #" + saved.hostId + " (token saved)");
  return saved;
}

// ============================================================================
//  STEP 5 — RUN ONE JOB FOR REAL
//  A job has:  id, type, prompt
//    type "python" -> prompt IS python code. We save it, run it, and the
//                     result is a .txt file containing what it printed.
//    type "image"  -> prompt is words. We call runners/image_ai.py which
//                     makes a real PNG (see that file for how).
//  Returns: { ok:true, file, seconds }  or  { ok:false, error }
// ============================================================================

async function runJob(job) {
  const startedAt = Date.now();

  // Each job gets its own clean work folder: work/job-<id>/
  const jobFolder = path.join(WORK_FOLDER, "job-" + job.id);
  fs.mkdirSync(jobFolder, { recursive: true });

  try {
    let resultFile = null;

    if (job.type === "python") {
      // Save the renter's code to a file, then run it with python3.
      const codeFile = path.join(jobFolder, "job.py");
      fs.writeFileSync(codeFile, job.prompt);

      const run = await runCommand("python3", [codeFile], 30_000); // 30 sec max

      // Whatever it printed (good or bad) becomes the result text.
      resultFile = path.join(jobFolder, "result.txt");
      fs.writeFileSync(
        resultFile,
        run.stdout + (run.stderr ? "\n--- errors ---\n" + run.stderr : "")
      );
      if (!run.ok && run.stdout === "" ) {
        return { ok: false, error: "Python job failed: " + run.stderr.slice(0, 500) };
      }
    }
    else if (job.type === "image") {
      // Hand the words to our Python AI runner script.
      resultFile = path.join(jobFolder, "result.png");
      const script = path.join(AGENT_FOLDER, "runners", "image_ai.py");

      const run = await runCommand("python3", [script, job.prompt, resultFile], 600_000);
      if (!run.ok || !fs.existsSync(resultFile)) {
        return { ok: false, error: "Image generation failed: " + run.stderr.slice(0, 500) };
      }
    }
    else {
      // audio / 3d / video runners get added here later, one at a time.
      return { ok: false, error: "This host cannot do job type '" + job.type + "' yet." };
    }

    const seconds = Math.round((Date.now() - startedAt) / 1000);
    return { ok: true, file: resultFile, seconds: seconds };
  } catch (err) {
    return { ok: false, error: String(err.message || err) };
  }
}

// ============================================================================
//  STEP 6 — UPLOAD THE RESULT FILE TO THE SERVER
//  We send it as "form-data": the file + how many seconds it took.
// ============================================================================

async function uploadResult(jobId, filePath, seconds, token) {
  const fileBytes = fs.readFileSync(filePath);
  const fileName = path.basename(filePath);

  const form = new FormData();
  form.append("seconds", String(seconds));
  form.append("file", new Blob([fileBytes]), fileName);

  const response = await fetch(SERVER_URL + "/api/host/jobs/" + jobId + "/result", {
    method: "POST",
    headers: { Authorization: "Bearer " + token },
    body: form,
  });

  if (!response.ok) {
    throw new Error("Upload failed: " + response.status + " " + (await response.text()));
  }
}

async function reportFailure(jobId, errorText, token) {
  await callServer("/api/host/jobs/" + jobId + "/fail", "POST", { error: errorText }, token);
}

// ============================================================================
//  STEP 7 — THE MAIN LOOP (this runs forever until Ctrl+C)
// ============================================================================

async function main() {
  console.log("GPUse host agent starting...");
  console.log("Server: " + SERVER_URL);

  const gpuName = await detectGpu();
  console.log("GPU:    " + gpuName);

  const host = await registerIfNeeded(gpuName);

  console.log("Waiting for jobs. Press Ctrl+C to stop.\n");

  // One loop turn = heartbeat + ask for a job + maybe run it.
  while (true) {
    try {
      // "I am alive" — the website shows us Online if this is recent.
      await callServer("/api/host/heartbeat", "POST", null, host.token);

      // "Any job for me?"
      const answer = await callServer("/api/host/jobs/next", "GET", null, host.token);

      if (answer.job) {
        const job = answer.job;
        console.log("Job #" + job.id + " (" + job.type + "): " + job.prompt.slice(0, 80));

        const result = await runJob(job);

        if (result.ok) {
          await uploadResult(job.id, result.file, result.seconds, host.token);
          console.log("Job #" + job.id + " done in " + result.seconds + "s, result uploaded.\n");
        } else {
          await reportFailure(job.id, result.error, host.token);
          console.log("Job #" + job.id + " FAILED: " + result.error + "\n");
        }
      }
    } catch (err) {
      // Server offline? Just wait and try again. Never crash the agent.
      console.log("Waiting for server... (" + err.message + ")");
    }

    await sleep(3000); // ask again in 3 seconds
  }
}

main();
