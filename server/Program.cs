// ============================================================================
//  GPUse SERVER  (C# / ASP.NET Core)
//  ----------------------------------------------------------------------------
//  What this file does, in plain words:
//    - Keeps 3 lists in a database: users, hosts (GPUs), jobs
//    - Renters:  sign up -> log in -> see GPUs -> send a job -> download result
//    - Hosts:    (the other computer) registers -> asks "any job for me?"
//                -> runs it -> uploads the result file back here
//
//  How to read this file: it goes top to bottom in 6 numbered parts.
//    PART 1  Start the web server
//    PART 2  Make the database (if it does not exist yet)
//    PART 3  Small helper tools used everywhere below
//    PART 4  Renter endpoints  (/api/...)
//    PART 5  Host endpoints    (/api/host/...)
//    PART 6  The "shapes" of data we receive (records at the bottom)
// ============================================================================

using Microsoft.Data.Sqlite;
using System.Security.Cryptography;

// ============================================================================
//  PART 1 — START THE WEB SERVER
// ============================================================================

var builder = WebApplication.CreateBuilder(args);

// Quiet the log: by default ASP.NET prints every single request, and our
// agent asks for a job every 3 seconds - the terminal becomes unreadable.
// Warnings and errors still show. (Needs a server restart to take effect.)
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// The server listens on port 5000 on this computer.
// Later, a Cloudflare Tunnel will make this reachable from the internet.
builder.WebHost.UseUrls("http://0.0.0.0:5000");

var app = builder.Build();

// Files in wwwroot/ are the website itself (our copy of the GPUse pages).
// Example: wwwroot/browse.html is shown when you open /browse.html
// UseDefaultFiles = opening just "/" shows index.html (found 2026-11-12:
// without this line the bare tunnel link gave a 404).
app.UseDefaultFiles();
app.UseStaticFiles();

// Where we store things on disk:
string dataFolder   = Path.Combine(Directory.GetCurrentDirectory(), "data");
string resultsFolder = Path.Combine(dataFolder, "results");
Directory.CreateDirectory(resultsFolder);

string databaseFile = Path.Combine(dataFolder, "gpuse.db");
string connectionString = $"Data Source={databaseFile}";

// ============================================================================
//  PART 2 — MAKE THE DATABASE (runs once when the server starts)
//  ----------------------------------------------------------------------------
//  SQLite = the whole database is ONE file (data/gpuse.db).
//  We have 3 tables. Think of each table like a spreadsheet:
//
//    users : who can log in
//    hosts : computers offering their GPU ("host token" proves who they are)
//    jobs  : one row per job, status goes  queued -> running -> done/failed
// ============================================================================

void MakeDatabase()
{
    using var db = new SqliteConnection(connectionString);
    db.Open();

    string sql = @"
        CREATE TABLE IF NOT EXISTS users (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            handle        TEXT NOT NULL,
            email         TEXT NOT NULL UNIQUE,
            password_hash TEXT NOT NULL,
            token         TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS hosts (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            name       TEXT NOT NULL,
            gpu        TEXT NOT NULL,
            token      TEXT NOT NULL,
            last_seen  TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS jobs (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            user_id       INTEGER NOT NULL,
            host_id       INTEGER NOT NULL,
            type          TEXT NOT NULL,
            prompt        TEXT NOT NULL,
            status        TEXT NOT NULL,
            cost          REAL NOT NULL,
            seconds       INTEGER,
            result_file   TEXT,
            error         TEXT,
            created_at    TEXT NOT NULL
        );
    ";

    using var cmd = new SqliteCommand(sql, db);
    cmd.ExecuteNonQuery();
}

MakeDatabase();

// ============================================================================
//  PART 3 — SMALL HELPER TOOLS
// ============================================================================

// --- Open a connection to the database --------------------------------------
// Every endpoint below starts with this. "using" = close it when done.
SqliteConnection OpenDb()
{
    var db = new SqliteConnection(connectionString);
    db.Open();
    return db;
}

// --- Turn a password into a safe stored string -------------------------------
// We NEVER store the real password. We store:  salt + "." + hash
// Hash = PBKDF2, a standard slow guessing-resistant method built into .NET.
string HashPassword(string password)
{
    byte[] salt = RandomNumberGenerator.GetBytes(16);
    byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
        password, salt, 100_000, HashAlgorithmName.SHA256, 32);

    return Convert.ToBase64String(salt) + "." + Convert.ToBase64String(hash);
}

// --- Check a typed password against the stored string ------------------------
bool PasswordIsCorrect(string password, string stored)
{
    string[] parts = stored.Split('.');
    byte[] salt = Convert.FromBase64String(parts[0]);
    byte[] expectedHash = Convert.FromBase64String(parts[1]);

    byte[] actualHash = Rfc2898DeriveBytes.Pbkdf2(
        password, salt, 100_000, HashAlgorithmName.SHA256, 32);

    return actualHash.SequenceEqual(expectedHash);
}

// --- Make a new random token (used for login sessions AND host computers) ----
string NewToken()
{
    return Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
}

// --- Who is calling this renter endpoint? ------------------------------------
// The website sends:   Authorization: Bearer <token>
// If the token matches a user, return that user's id. Otherwise return null.
int? GetUserId(HttpRequest request)
{
    string header = request.Headers.Authorization.ToString();
    if (!header.StartsWith("Bearer ")) return null;

    string token = header.Substring("Bearer ".Length);

    using var db = OpenDb();
    using var cmd = new SqliteCommand(
        "SELECT id FROM users WHERE token = $token", db);
    cmd.Parameters.AddWithValue("$token", token);

    object? result = cmd.ExecuteScalar();
    if (result == null) return null;
    return Convert.ToInt32(result);
}

// --- Which host computer is calling? (same idea, but for hosts) --------------
int? GetHostId(HttpRequest request)
{
    string header = request.Headers.Authorization.ToString();
    if (!header.StartsWith("Bearer ")) return null;

    string token = header.Substring("Bearer ".Length);

    using var db = OpenDb();
    using var cmd = new SqliteCommand(
        "SELECT id FROM hosts WHERE token = $token", db);
    cmd.Parameters.AddWithValue("$token", token);

    object? result = cmd.ExecuteScalar();
    if (result == null) return null;
    return Convert.ToInt32(result);
}

// --- Price list: what one job costs, by type ---------------------------------
// Simple and fixed for now. Real pricing (per host) comes later.
double PriceFor(string type)
{
    if (type == "image")  return 0.01;
    if (type == "audio")  return 0.02;
    if (type == "3d")     return 0.04;
    if (type == "video")  return 0.06;
    return 0.01; // "python" and anything else
}

// ============================================================================
//  PART 4 — RENTER ENDPOINTS
//  An "endpoint" = one URL the website/app can call.
//  Results.Ok(...)     = answer 200 + some JSON data
//  Results.Unauthorized() = answer 401 "log in first"
//  Results.BadRequest(...) = answer 400 + a human-readable reason
// ============================================================================

// --- Sign up: make a new account, get a login token back ---------------------
app.MapPost("/api/signup", (SignupBody body) =>
{
    if (body.Handle == "" || body.Email == "" || body.Password == "")
        return Results.BadRequest(new { error = "Fill in every field." });

    using var db = OpenDb();
    string token = NewToken();

    try
    {
        using var cmd = new SqliteCommand(
            "INSERT INTO users (handle, email, password_hash, token) " +
            "VALUES ($handle, $email, $hash, $token)", db);

        cmd.Parameters.AddWithValue("$handle", body.Handle);
        cmd.Parameters.AddWithValue("$email", body.Email);
        cmd.Parameters.AddWithValue("$hash", HashPassword(body.Password));
        cmd.Parameters.AddWithValue("$token", token);
        cmd.ExecuteNonQuery();
    }
    catch (SqliteException)
    {
        // The email column is UNIQUE, so a duplicate email lands here.
        return Results.BadRequest(new { error = "That email is already used." });
    }

    return Results.Ok(new { token });
});

// --- Log in: check email + password, return the SAME token idea --------------
app.MapPost("/api/login", (LoginBody body) =>
{
    using var db = OpenDb();

    using var cmd = new SqliteCommand(
        "SELECT id, password_hash FROM users WHERE email = $email", db);
    cmd.Parameters.AddWithValue("$email", body.Email);

    using var reader = cmd.ExecuteReader();
    if (!reader.Read())
        return Results.BadRequest(new { error = "No account with that email." });

    string storedHash = reader.GetString(1);
    if (!PasswordIsCorrect(body.Password, storedHash))
        return Results.BadRequest(new { error = "Wrong password." });

    // Login OK -> give this user a fresh token (old device gets logged out).
    reader.Close();
    string token = NewToken();

    using var update = new SqliteCommand(
        "UPDATE users SET token = $token WHERE email = $email", db);
    update.Parameters.AddWithValue("$token", token);
    update.Parameters.AddWithValue("$email", body.Email);
    update.ExecuteNonQuery();

    return Results.Ok(new { token });
});

// --- Browse GPUs: list every host computer we know ----------------------------
// A host is "online" if we heard from it in the last 30 seconds.
app.MapGet("/api/gpus", () =>
{
    using var db = OpenDb();
    using var cmd = new SqliteCommand(
        "SELECT id, name, gpu, last_seen FROM hosts ORDER BY id", db);

    using var reader = cmd.ExecuteReader();
    var gpus = new List<object>();

    while (reader.Read())
    {
        DateTime lastSeen = DateTime.Parse(reader.GetString(3));
        bool online = (DateTime.UtcNow - lastSeen).TotalSeconds < 30;

        gpus.Add(new
        {
            id = reader.GetInt32(0),
            name = reader.GetString(1),
            gpu = reader.GetString(2),
            online = online
        });
    }

    return Results.Ok(gpus);
});

// --- Create a job: renter picks a host + type + prompt ------------------------
app.MapPost("/api/jobs", (CreateJobBody body, HttpRequest request) =>
{
    int? userId = GetUserId(request);
    if (userId == null) return Results.Unauthorized();

    using var db = OpenDb();

    using var cmd = new SqliteCommand(
        "INSERT INTO jobs (user_id, host_id, type, prompt, status, cost, created_at) " +
        "VALUES ($user, $host, $type, $prompt, 'queued', $cost, $now); " +
        "SELECT last_insert_rowid();", db);

    cmd.Parameters.AddWithValue("$user", userId.Value);
    cmd.Parameters.AddWithValue("$host", body.HostId);
    cmd.Parameters.AddWithValue("$type", body.Type);
    cmd.Parameters.AddWithValue("$prompt", body.Prompt);
    cmd.Parameters.AddWithValue("$cost", PriceFor(body.Type));
    cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));

    long jobId = (long)cmd.ExecuteScalar()!;
    return Results.Ok(new { jobId });
});

// --- My jobs: everything this renter has ever sent ----------------------------
app.MapGet("/api/jobs/mine", (HttpRequest request) =>
{
    int? userId = GetUserId(request);
    if (userId == null) return Results.Unauthorized();

    using var db = OpenDb();
    using var cmd = new SqliteCommand(
        "SELECT j.id, j.type, j.prompt, h.name, h.gpu, j.status, j.cost, j.seconds, j.error " +
        "FROM jobs j JOIN hosts h ON h.id = j.host_id " +
        "WHERE j.user_id = $user ORDER BY j.id DESC", db);
    cmd.Parameters.AddWithValue("$user", userId.Value);

    using var reader = cmd.ExecuteReader();
    var jobs = new List<object>();

    while (reader.Read())
    {
        jobs.Add(new
        {
            id = reader.GetInt32(0),
            type = reader.GetString(1),
            prompt = reader.GetString(2),
            hostName = reader.GetString(3),
            gpu = reader.GetString(4),
            status = reader.GetString(5),
            cost = reader.GetDouble(6),
            seconds = reader.IsDBNull(7) ? (int?)null : reader.GetInt32(7),
            error = reader.IsDBNull(8) ? null : reader.GetString(8)
        });
    }

    return Results.Ok(jobs);
});

// --- Download the result file of a finished job -------------------------------
app.MapGet("/api/jobs/{jobId:int}/result", (int jobId, HttpRequest request) =>
{
    int? userId = GetUserId(request);
    if (userId == null) return Results.Unauthorized();

    using var db = OpenDb();
    using var cmd = new SqliteCommand(
        "SELECT result_file FROM jobs WHERE id = $id AND user_id = $user", db);
    cmd.Parameters.AddWithValue("$id", jobId);
    cmd.Parameters.AddWithValue("$user", userId.Value);

    object? fileName = cmd.ExecuteScalar();
    if (fileName == null) return Results.NotFound();

    string fullPath = Path.Combine(resultsFolder, fileName.ToString()!);
    if (!File.Exists(fullPath)) return Results.NotFound();

    // Send the file back. The browser will download or display it.
    return Results.File(File.ReadAllBytes(fullPath), "application/octet-stream",
                        Path.GetFileName(fullPath));
});

// ============================================================================
//  PART 5 — HOST ENDPOINTS  (called by agent.js on the other computer)
// ============================================================================

// --- Register: a new host computer introduces itself --------------------------
// Called ONCE by the agent. Answer: its id + secret token (agent saves it).
app.MapPost("/api/host/register", (RegisterHostBody body) =>
{
    using var db = OpenDb();
    string token = NewToken();

    using var cmd = new SqliteCommand(
        "INSERT INTO hosts (name, gpu, token, last_seen) " +
        "VALUES ($name, $gpu, $token, $now); SELECT last_insert_rowid();", db);

    cmd.Parameters.AddWithValue("$name", body.Name);
    cmd.Parameters.AddWithValue("$gpu", body.Gpu);
    cmd.Parameters.AddWithValue("$token", token);
    cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));

    long hostId = (long)cmd.ExecuteScalar()!;
    return Results.Ok(new { hostId, token });
});

// --- Heartbeat: host says "I am still alive" every few seconds ----------------
app.MapPost("/api/host/heartbeat", (HttpRequest request) =>
{
    int? hostId = GetHostId(request);
    if (hostId == null) return Results.Unauthorized();

    using var db = OpenDb();
    using var cmd = new SqliteCommand(
        "UPDATE hosts SET last_seen = $now WHERE id = $id", db);
    cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
    cmd.Parameters.AddWithValue("$id", hostId.Value);
    cmd.ExecuteNonQuery();

    return Results.Ok(new { ok = true });
});

// --- "Any job for me?" ---------------------------------------------------------
// The agent asks this again and again (polling). If a queued job is waiting
// for this host, we mark it 'running' and hand it over. Otherwise: null.
app.MapGet("/api/host/jobs/next", (HttpRequest request) =>
{
    int? hostId = GetHostId(request);
    if (hostId == null) return Results.Unauthorized();

    using var db = OpenDb();

    // Step 1: find the oldest waiting job for this host.
    using var find = new SqliteCommand(
        "SELECT id, type, prompt FROM jobs " +
        "WHERE host_id = $host AND status = 'queued' ORDER BY id LIMIT 1", db);
    find.Parameters.AddWithValue("$host", hostId.Value);

    using var reader = find.ExecuteReader();
    if (!reader.Read()) return Results.Ok(new { job = (object?)null });

    int jobId = reader.GetInt32(0);
    string type = reader.GetString(1);
    string prompt = reader.GetString(2);
    reader.Close();

    // Step 2: claim it, so nobody else takes the same job.
    using var claim = new SqliteCommand(
        "UPDATE jobs SET status = 'running' WHERE id = $id", db);
    claim.Parameters.AddWithValue("$id", jobId);
    claim.ExecuteNonQuery();

    return Results.Ok(new { job = new { id = jobId, type, prompt } });
});

// --- Upload a finished result file ---------------------------------------------
// The agent sends the file as form-data. We save it and mark the job done.
app.MapPost("/api/host/jobs/{jobId:int}/result", async (int jobId, HttpRequest request) =>
{
    int? hostId = GetHostId(request);
    if (hostId == null) return Results.Unauthorized();

    var form = await request.ReadFormAsync();
    var file = form.Files["file"];
    if (file == null) return Results.BadRequest(new { error = "No file uploaded." });

    int seconds = 0;
    int.TryParse(form["seconds"].ToString(), out seconds);

    // Save as e.g. data/results/12.png  or  12.txt (keep its extension)
    string extension = Path.GetExtension(file.FileName);
    string savedName = jobId + extension;
    string fullPath = Path.Combine(resultsFolder, savedName);

    using (var stream = File.Create(fullPath))
    {
        await file.CopyToAsync(stream);
    }

    using var db = OpenDb();
    using var cmd = new SqliteCommand(
        "UPDATE jobs SET status = 'done', result_file = $file, seconds = $sec " +
        "WHERE id = $id AND host_id = $host", db);
    cmd.Parameters.AddWithValue("$file", savedName);
    cmd.Parameters.AddWithValue("$sec", seconds);
    cmd.Parameters.AddWithValue("$id", jobId);
    cmd.Parameters.AddWithValue("$host", hostId.Value);
    cmd.ExecuteNonQuery();

    return Results.Ok(new { ok = true });
});

// --- Report a failed job ---------------------------------------------------------
app.MapPost("/api/host/jobs/{jobId:int}/fail", (int jobId, FailBody body, HttpRequest request) =>
{
    int? hostId = GetHostId(request);
    if (hostId == null) return Results.Unauthorized();

    using var db = OpenDb();
    using var cmd = new SqliteCommand(
        "UPDATE jobs SET status = 'failed', error = $error " +
        "WHERE id = $id AND host_id = $host", db);
    cmd.Parameters.AddWithValue("$error", body.Error);
    cmd.Parameters.AddWithValue("$id", jobId);
    cmd.Parameters.AddWithValue("$host", hostId.Value);
    cmd.ExecuteNonQuery();

    return Results.Ok(new { ok = true });
});

// Start listening. (Everything above was just "setting up".)
app.Run();

// ============================================================================
//  PART 6 — SHAPES OF DATA WE RECEIVE
//  A "record" = a named box with fields. ASP.NET fills it from the JSON body
//  automatically. Example JSON for SignupBody:
//    { "handle": "aleks", "email": "a@b.com", "password": "secret" }
// ============================================================================

record SignupBody(string Handle, string Email, string Password);
record LoginBody(string Email, string Password);
record CreateJobBody(int HostId, string Type, string Prompt);
record RegisterHostBody(string Name, string Gpu);
record FailBody(string Error);
