using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Newtonsoft.Json;

namespace ClaudeCodeVs.Protocol;

/// <summary>
/// The lockfile is how the CLI discovers that "an IDE is here". It lives at
/// <c>~/.claude/ide/&lt;port&gt;.lock</c> - and the filename IS the port the WS server listens on.
/// See build-plan.md §3. Ported verbatim from the Phase 0 spike with two net48 fixes
/// (<c>Environment.ProcessId</c> is .NET 5+).
/// </summary>
public sealed class Lockfile
{
    public int Port { get; }
    public string AuthToken { get; }

    /// <summary>
    /// Every path this lockfile was written to - one per DISTINCT agent IdeDir. Agents that share a
    /// discovery directory (Claude Code and opencode both read <c>~/.claude/ide</c>) share one file, so
    /// this is usually a single entry.
    /// </summary>
    public IReadOnlyList<string> Paths { get; }

    /// <summary>The primary lockfile path (the first agent's). Kept for logging and single-agent callers.</summary>
    public string Path => Paths[0];

    private readonly LockfileDoc _doc;

    private Lockfile(int port, string authToken, IReadOnlyList<string> paths, LockfileDoc doc)
    {
        Port = port;
        AuthToken = authToken;
        Paths = paths;
        _doc = doc;
    }

    /// <summary>
    /// Pick a free loopback port, then write a lockfile for it into every agent's discovery directory.
    /// We bind a throwaway TcpListener to port 0 (OS assigns a free port), read the assignment, and
    /// release it. There's a tiny TOCTOU window before the WS server grabs the port - acceptable in
    /// practice.
    ///
    /// ONE port, ONE auth token, N directories: the bridge serves every agent simultaneously
    /// (docs/MULTI-AGENT.md), so whichever agent the user launches finds a live bridge without the
    /// server being torn down and rebuilt. Agents with no lockfile discovery
    /// (<see cref="AgentProfile.SupportsIdeSocket"/> false) contribute no directory.
    /// </summary>
    public static Lockfile CreateForFreePort(IReadOnlyList<string> workspaceFolders, IReadOnlyList<AgentProfile>? agents = null)
    {
        agents = Normalize(agents);
        var ideDirs = DistinctIdeDirs(agents);
        if (ideDirs.Count == 0)
            throw new InvalidOperationException("no agent profile declares a lockfile directory");

        int port = PickFreePort();
        var token = Guid.NewGuid().ToString();

        var self = Process.GetCurrentProcess();
        var doc = new LockfileDoc
        {
            Pid = self.Id,
            PidStartTime = SafeStartTime(self),
            WorkspaceFolders = workspaceFolders.ToArray(),
            IdeName = agents[0].IdeName,
            Transport = "ws",
            // CLI uses this to pick `tasklist.exe` (Windows) vs `ps` for PID-liveness checks.
            RunningInWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            AuthToken = token,
        };

        var json = JsonConvert.SerializeObject(doc, Formatting.Indented);
        var paths = new List<string>();
        foreach (var dir in ideDirs)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var path = System.IO.Path.Combine(dir, $"{port}.lock");
                File.WriteAllText(path, json);
                paths.Add(path);
                Log.Info($"wrote lockfile {path} (pid={doc.Pid}, token=<redacted>)");
            }
            catch (Exception e)
            {
                // One unwritable agent directory must not sink the bridge; the others still discover it.
                Log.Warn($"could not write lockfile in {dir}: {e.Message}");
            }
        }
        if (paths.Count == 0)
            throw new IOException($"could not write a lockfile in any of: {string.Join("; ", ideDirs)}");

        return new Lockfile(port, token, paths, doc);
    }

    /// <summary>
    /// Rewrite every lockfile with new workspace folders (same port + token). The bridge starts at
    /// shell-init before any solution is open, so workspaceFolders is initially empty; call this when
    /// a solution/folder opens so the CLI's /ide matches it against the current working directory.
    /// </summary>
    public void UpdateWorkspaceFolders(IReadOnlyList<string> folders)
    {
        _doc.WorkspaceFolders = folders.ToArray();
        var json = JsonConvert.SerializeObject(_doc, Formatting.Indented);
        foreach (var path in Paths)
        {
            try
            {
                File.WriteAllText(path, json);
            }
            catch (Exception e)
            {
                Log.Warn($"could not update lockfile workspaceFolders in {path}: {e.Message}");
            }
        }
        Log.Info($"updated lockfile workspaceFolders: {string.Join("; ", _doc.WorkspaceFolders)}");
    }

    public void Delete()
    {
        foreach (var path in Paths)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    Log.Info($"deleted lockfile {path}");
                }
            }
            catch (Exception e)
            {
                Log.Warn($"could not delete lockfile {path}: {e.Message}");
            }
        }
    }

    /// <summary>
    /// On startup, remove lockfiles whose owning process is dead, across every agent's discovery
    /// directory. A stale lockfile pointing at a dead WS server blocks reconnection (issue #5043). We
    /// ONLY delete dead-PID files - never another live IDE's (e.g. a running VS Code) lockfile.
    /// </summary>
    public static void ReapStale(IReadOnlyList<AgentProfile>? agents = null)
    {
        foreach (var ideDir in DistinctIdeDirs(Normalize(agents)))
        {
            if (!Directory.Exists(ideDir)) continue;

            foreach (var file in Directory.EnumerateFiles(ideDir, "*.lock"))
            {
                try
                {
                    var doc = JsonConvert.DeserializeObject<LockfileDoc>(File.ReadAllText(file));
                    if (doc is null) continue;

                    if (!IsOwnerAlive(doc.Pid, doc.PidStartTime))
                    {
                        File.Delete(file);
                        Log.Info($"reaped stale lockfile {System.IO.Path.GetFileName(file)} (dead/recycled pid {doc.Pid})");
                    }
                }
                catch (Exception e)
                {
                    Log.Warn($"skipping unreadable lockfile {System.IO.Path.GetFileName(file)}: {e.Message}");
                }
            }
        }
    }

    private static IReadOnlyList<AgentProfile> Normalize(IReadOnlyList<AgentProfile>? agents) =>
        agents is { Count: > 0 } ? agents : new[] { AgentProfile.ClaudeCode };

    /// <summary>
    /// The discovery directories to write/reap, de-duplicated case-insensitively - Claude Code and
    /// opencode both point at <c>~/.claude/ide</c>, and writing that file twice would be pointless.
    /// </summary>
    private static IReadOnlyList<string> DistinctIdeDirs(IReadOnlyList<AgentProfile> agents)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new List<string>();
        foreach (var a in agents)
        {
            if (a.IdeDir is not { Length: > 0 } dir) continue;
            if (seen.Add(dir)) dirs.Add(dir);
        }
        return dirs;
    }

    /// <summary>
    /// True if the lockfile's owning VS process is still alive. Beyond a bare PID check, when the
    /// lockfile records a start time we verify the live PID's start time matches - so a recycled PID
    /// (Windows reuses the PIDs of dead processes) is correctly treated as dead rather than a false
    /// "alive". If the start time can't be read (e.g. access denied -> not our same-user devenv), we
    /// keep the lockfile rather than risk reaping a live instance; the hooks' live-port probe skips it.
    /// </summary>
    private static bool IsOwnerAlive(int pid, long startTimeFileTimeUtc)
    {
        if (pid <= 0) return false;
        try
        {
            using var proc = Process.GetProcessById(pid); // throws ArgumentException if no such process
            if (startTimeFileTimeUtc == 0) return true;   // legacy lockfile without identity -> PID-only
            try { return proc.StartTime.ToFileTimeUtc() == startTimeFileTimeUtc; }
            catch { return true; }                        // can't introspect -> don't risk a false reap
        }
        catch (ArgumentException)
        {
            return false; // no process with this PID -> definitely dead
        }
    }

    /// <summary>Owning process start time as a UTC file-time, or 0 if unavailable.</summary>
    private static long SafeStartTime(Process proc)
    {
        try { return proc.StartTime.ToFileTimeUtc(); }
        catch { return 0; }
    }

    private static int PickFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try
        {
            return ((IPEndPoint)l.LocalEndpoint).Port;
        }
        finally
        {
            l.Stop();
        }
    }

    /// <summary>Exact on-disk schema. Property names are wire-critical - do not rename casually.</summary>
    private sealed class LockfileDoc
    {
        [JsonProperty("pid")] public int Pid { get; set; }
        // Extension-only (the CLI ignores unknown fields): the owning process's start time, paired with
        // Pid to defeat PID reuse when reaping stale lockfiles.
        [JsonProperty("pidStartTime")] public long PidStartTime { get; set; }
        [JsonProperty("workspaceFolders")] public string[] WorkspaceFolders { get; set; } = Array.Empty<string>();
        [JsonProperty("ideName")] public string IdeName { get; set; } = "Visual Studio";
        [JsonProperty("transport")] public string Transport { get; set; } = "ws";
        [JsonProperty("runningInWindows")] public bool RunningInWindows { get; set; }
        [JsonProperty("authToken")] public string AuthToken { get; set; } = "";
    }
}
