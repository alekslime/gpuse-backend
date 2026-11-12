// ============================================================================
//  GPUse WEBSITE -> SERVER TALK  (plain JavaScript, no libraries)
//  ----------------------------------------------------------------------------
//  The server (C#) and this website can only talk via "fetch" calls:
//  fetch = "go to this URL, bring back the answer".
//
//  Remember 3 ideas and this whole file makes sense:
//    1. After login we keep a TOKEN (like a wristband) in the browser.
//    2. Every call sends that token:  Authorization: Bearer <token>
//    3. Answers come back as JSON (text that looks like a JS object).
// ============================================================================

// The server is the same computer/website origin, so "" means "this address".
const API = "";

// --- The wristband: save / read / delete the login token ----------------------
function saveToken(token) { localStorage.setItem("gpuse_token", token); }
function getToken()       { return localStorage.getItem("gpuse_token"); }
function logout()         { localStorage.removeItem("gpuse_token"); }

// --- One helper used by every call below --------------------------------------
// Example:  api("POST", "/api/login", { email, password })
async function api(method, url, body) {
  const response = await fetch(API + url, {
    method: method,
    headers: {
      "Content-Type": "application/json",
      ...(getToken() ? { Authorization: "Bearer " + getToken() } : {}),
    },
    body: body ? JSON.stringify(body) : undefined,
  });

  const data = await response.json().catch(() => ({}));

  if (!response.ok) {
    throw new Error(data.error || ("Server error " + response.status));
  }
  return data;
}

// --- Account --------------------------------------------------------------------
async function signup(handle, email, password) {
  const data = await api("POST", "/api/signup", { handle, email, password });
  saveToken(data.token);
}

async function login(email, password) {
  const data = await api("POST", "/api/login", { email, password });
  saveToken(data.token);
}

// --- GPUs and jobs ---------------------------------------------------------------
function getGpus()       { return api("GET", "/api/gpus"); }
function getMyJobs()     { return api("GET", "/api/jobs/mine"); }
function createJob(hostId, type, prompt) {
  return api("POST", "/api/jobs", { hostId, type, prompt });
}
function resultUrl(jobId) { return API + "/api/jobs/" + jobId + "/result"; }

// ============================================================================
//  PAGE WIRING — small blocks, each only runs if its page elements exist.
//  (So this one file is safe to load on every page.)
// ============================================================================

// --- Login / Signup pages: when the form is sent, call the server instead -----
const authForm = document.querySelector("form.auth-panel, form.panel");

if (authForm && document.getElementById("email")) {
  authForm.addEventListener("submit", async (event) => {
    event.preventDefault(); // stop the normal page reload

    const email = document.getElementById("email").value;
    const password = document.getElementById("password").value;
    const handleField = document.getElementById("handle"); // only on signup
    const confirmField = document.getElementById("confirm"); // only on signup

    if (confirmField && password !== confirmField.value) {
      alert("Passwords do not match.");
      return;
    }

    try {
      if (handleField) {
        await signup(handleField.value, email, password);
      } else {
        await login(email, password);
      }
      window.location.href = "browse.html"; // success -> go rent a GPU
    } catch (err) {
      alert(err.message); // e.g. "Wrong password."
    }
  });
}

// --- Browse page: show REAL hosts, and send a real job --------------------------
// This block only runs on browse.html (it looks for id="gpu-cards").
const gpuCards = document.getElementById("gpu-cards");

if (gpuCards) {
  loadGpus();
  setInterval(loadGpus, 5000); // refresh Online/Offline every 5 seconds
}

async function loadGpus() {
  try {
    const gpus = await getGpus();

    // Fill the HOST dropdown in the "Send a job" panel.
    const hostSelect = document.getElementById("job-host");
    const oldChoice = hostSelect.value;
    hostSelect.innerHTML = "";

    // One card per host. We build HTML text the same shape as the old cards.
    gpuCards.innerHTML = "";

    for (const gpu of gpus) {
      const option = document.createElement("option");
      option.value = gpu.id;
      option.textContent = gpu.name + " — " + gpu.gpu + (gpu.online ? "" : " (offline)");
      hostSelect.appendChild(option);

      const card = document.createElement("article");
      card.className = "card-host";
      card.innerHTML =
        '<span class="badge' + (gpu.online ? "" : " busy") + '">' +
          (gpu.online ? "Available" : "Offline") + "</span>" +
        "<h3>" + gpu.name + "</h3>" +
        '<p class="gpu">' + gpu.gpu + "</p>" +
        '<p class="price">' + (gpu.online ? "Online now" : "Offline") + "</p>";
      gpuCards.appendChild(card);
    }

    if (oldChoice) hostSelect.value = oldChoice;
    document.getElementById("gpu-count").textContent =
      "Showing " + gpus.length + " real host(s) from the server.";
  } catch (err) {
    gpuCards.innerHTML = '<p class="note">Could not load GPUs: ' + err.message + "</p>";
  }
}

// The "Run job" button in the Send-a-job panel.
const jobSendButton = document.getElementById("job-send");

if (jobSendButton) {
  jobSendButton.addEventListener("click", async () => {
    const msg = document.getElementById("job-msg");
    try {
      const hostId = Number(document.getElementById("job-host").value);
      const type = document.getElementById("job-type").value;
      const prompt = document.getElementById("job-prompt").value;

      const answer = await createJob(hostId, type, prompt);
      msg.textContent = "Job #" + answer.jobId + " sent! Watch it in My jobs.";
    } catch (err) {
      // 401 here means "not logged in" - the server has no idea who we are.
      msg.textContent = "Could not send job: " + err.message + " (are you logged in?)";
    }
  });
}

// --- My Jobs page: show REAL jobs, refresh automatically -------------------------
// This block only runs on my-jobs.html (it looks for id="jobs-table").
const jobsTable = document.getElementById("jobs-table");

if (jobsTable) {
  loadMyJobs();
  setInterval(loadMyJobs, 3000); // status moves queued -> running -> done live
}

async function loadMyJobs() {
  try {
    const jobs = await getMyJobs();

    // Keep the first row (the column headings), delete everything after it.
    while (jobsTable.rows.length > 1) jobsTable.deleteRow(1);

    // Show the "No jobs yet" card ONLY at zero jobs.
    // (We set display directly: .panel's CSS would ignore the hidden attribute.)
    document.getElementById("jobs-empty").style.display =
      jobs.length === 0 ? "" : "none";

    for (const job of jobs) {
      const row = jobsTable.insertRow();
      row.insertCell().textContent = "#" + job.id;
      row.insertCell().textContent = job.type + " · " + job.prompt.slice(0, 60);
      row.insertCell().textContent = job.hostName + " · " + job.gpu;
      row.insertCell().textContent = job.seconds == null ? "—" : job.seconds + " sec";
      row.insertCell().textContent = "$" + job.cost.toFixed(2);

      const statusCell = row.insertCell();
      statusCell.className = "st";
      statusCell.textContent = job.status;

      // Finished job? Add a Download button that fetches WITH the token.
      // (A plain <a href> cannot send the token, so we fetch the file as a
      // "blob" and then pretend-click a download link for it.)
      if (job.status === "done") {
        const button = document.createElement("button");
        button.className = "btn";
        button.textContent = "Download result";
        button.addEventListener("click", async () => {
          const response = await fetch(resultUrl(job.id), {
            headers: { Authorization: "Bearer " + getToken() },
          });
          const blob = await response.blob();
          const link = document.createElement("a");
          link.href = URL.createObjectURL(blob);
          link.download = "gpuse-job-" + job.id + (job.type === "image" ? ".png" : ".txt");
          link.click();
          URL.revokeObjectURL(link.href);
        });
        statusCell.appendChild(document.createTextNode(" "));
        statusCell.appendChild(button);
      }

      if (job.status === "failed" && job.error) {
        statusCell.textContent = "failed: " + job.error.slice(0, 80);
      }
    }
  } catch (err) {
    // Not logged in (or server down): leave the sample rows, say nothing loud.
    console.log("My jobs not loaded:", err.message);
  }
}
