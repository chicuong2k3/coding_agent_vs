using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.Protocol;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCodeVs.Tools;

/// <summary>
/// Profiling tools (ROADMAP "CPU / memory profiling"): NOT the debugger subsystem — shell out to the
/// .NET diagnostics CLIs against the debuggee (or any) PID and return parsed top-N results.
/// vs_perf_counters = dotnet-counters (CPU%, GC, alloc rate, threadpool), vs_trace_cpu = dotnet-trace
/// (top hot methods), vs_gc_dump = dotnet-gcdump (top types by retained size). Each fails with an
/// install hint when its CLI is missing (`dotnet tool install -g dotnet-counters` etc.). Read-only
/// observation of a process — ungated (same class as the ClrMD reads).
/// </summary>
internal static class PerfSupport
{
    /// <summary>Default PID = the first debugged process (EnvDTE, UI thread). Null if no session.</summary>
    public static async Task<int?> ResolvePidAsync(JToken? args, CancellationToken ct)
    {
        if ((int?)args?["pid"] is int p) return p;
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        int? found = null;
        try
        {
            var dbg = (ServiceProvider.GlobalProvider.GetService(typeof(SDTE)) as DTE)?.Debugger;
            if (dbg?.DebuggedProcesses != null)
                foreach (EnvDTE.Process dp in dbg.DebuggedProcesses) { found = dp.ProcessID; break; }
        }
        catch { }
        await TaskScheduler.Default;
        return found;
    }

    public static JObject NoPid() => new JObject
    {
        ["error"] = "no process to profile - start/attach a debug session, or pass 'pid' explicitly (see vs_list_processes)",
    };

    /// <summary>Run a diagnostics CLI to completion (bounded). Returns (exitCode, stdout+stderr) or an install hint.</summary>
    public static async Task<(int? exit, string output, JObject? error)> RunCliAsync(
        string exe, string arguments, int timeoutSeconds, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        System.Diagnostics.Process proc;
        try { proc = System.Diagnostics.Process.Start(psi)!; }
        catch (Exception)
        {
            return (null, "", new JObject
            {
                ["error"] = $"'{exe}' is not installed or not on PATH",
                ["hint"] = $"Install it once with: dotnet tool install -g {exe}",
            });
        }

        // Cancellation/timeout must KILL the child, not just abandon the wait — an orphaned
        // dotnet-trace keeps profiling (and holding its session) long after the request died.
        using (proc)
        using (ct.Register(() => { try { proc.Kill(); } catch { } }))
        {
            var sb = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            bool exited;
            try { exited = await Task.Run(() => proc.WaitForExit(timeoutSeconds * 1000), CancellationToken.None); }
            catch (Exception e)
            {
                try { proc.Kill(); } catch { }
                return (null, sb.ToString(), new JObject { ["error"] = $"'{exe}' wait failed: {e.Message}" });
            }
            if (ct.IsCancellationRequested)
                return (null, sb.ToString(), new JObject { ["error"] = $"'{exe}' was cancelled" });
            if (!exited)
            {
                try { proc.Kill(); } catch { }
                return (null, sb.ToString(), new JObject { ["error"] = $"'{exe}' did not finish within {timeoutSeconds}s" });
            }
            return (proc.ExitCode, sb.ToString(), null);
        }
    }

    /// <summary>Bounded raw-text payload (the reports are tables; parsing them further loses information).</summary>
    public static string Cap(string text, int maxChars = 8000)
        => text.Length <= maxChars ? text : text.Substring(0, maxChars) + "\n… (truncated)";

    public static string TempFile(string ext)
        => Path.Combine(Path.GetTempPath(), $"claude-vs-perf-{Guid.NewGuid():N}{ext}");
}

/// <summary>vs_perf_counters - live runtime counters (CPU %, GC, allocation rate, threadpool) over a sampling window.</summary>
internal sealed class VsPerfCountersTool : IIdeTool
{
    public string Name => "vs_perf_counters";
    public string Description =>
        "Sample .NET runtime counters from a live process for a few seconds via dotnet-counters and return "
        + "the summary: CPU %, working set, GC heap size + collection counts, allocation rate, threadpool "
        + "queue/threads, exception rate. Defaults to the current debuggee; pass pid for any process. "
        + "The 'why is it slow / is it allocating / is the threadpool starving' first look.";

    public JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject
        {
            ["pid"] = new JObject { ["type"] = "integer", ["description"] = "Process id (default: the current debuggee)." },
            ["durationSeconds"] = new JObject { ["type"] = "integer", ["description"] = "Sampling window, 3-60 (default 10)." },
        },
    };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        int? pid = await PerfSupport.ResolvePidAsync(args, ct);
        if (pid is null) return PerfSupport.NoPid();
        int dur = Math.Max(3, Math.Min(60, (int?)args["durationSeconds"] ?? 10));

        string csv = PerfSupport.TempFile(".csv");
        try
        {
            var (exit, output, error) = await PerfSupport.RunCliAsync(
                "dotnet-counters",
                $"collect -p {pid} -o \"{csv}\" --format csv --duration 00:00:{dur:00}",
                dur + 30, ct);
            if (error != null) return error;
            if (!File.Exists(csv))
                return new JObject { ["error"] = "dotnet-counters produced no output", ["cliOutput"] = PerfSupport.Cap(output, 2000) };

            // CSV rows: Timestamp,Provider,Counter Name,Counter Type,Mean/Increment. Summarize per counter:
            // last value for gauges (Metric), sum for rates (Rate), so the model sees one line per counter.
            var last = new Dictionary<string, (string type, double value, int n)>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadLines(csv).Skip(1))
            {
                var parts = line.Split(',');
                if (parts.Length < 5) continue;
                string name = parts[2].Trim('"');
                string type = parts[3].Trim('"');
                if (!double.TryParse(parts[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)) continue;
                last.TryGetValue(name, out var cur);
                last[name] = type.Contains("Rate")
                    ? (type, cur.value + v, cur.n + 1)          // rate/increment: total over the window
                    : (type, v, cur.n + 1);                     // gauge: last observed value
            }

            var counters = new JObject();
            foreach (var kv in last.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                counters[kv.Key] = Math.Round(kv.Value.value, 2);

            Ui.BridgeStatus.RecordDebugInspect();
            Log.Info($"vs_perf_counters(pid={pid}, {dur}s) -> {counters.Count} counter(s)");
            return new JObject
            {
                ["ok"] = true, ["pid"] = pid, ["durationSeconds"] = dur,
                ["note"] = "Gauges = last observed value; rates = total over the window.",
                ["counters"] = counters,
            };
        }
        finally { try { File.Delete(csv); } catch { } }
    }
}

/// <summary>vs_trace_cpu - sample the process's CPU for a window and return the hottest methods (dotnet-trace).</summary>
internal sealed class VsTraceCpuTool : IIdeTool
{
    public string Name => "vs_trace_cpu";
    public string Description =>
        "CPU-sample a live process for a few seconds via dotnet-trace and return the TOP HOT METHODS "
        + "(inclusive/exclusive %) - 'where is the CPU time actually going'. Defaults to the current "
        + "debuggee; pass pid for any process. Sampling adds low overhead; the app keeps running.";

    public JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject
        {
            ["pid"] = new JObject { ["type"] = "integer", ["description"] = "Process id (default: the current debuggee)." },
            ["durationSeconds"] = new JObject { ["type"] = "integer", ["description"] = "Sampling window, 3-60 (default 10)." },
            ["topN"] = new JObject { ["type"] = "integer", ["description"] = "How many hottest methods to report (default 15)." },
        },
    };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        int? pid = await PerfSupport.ResolvePidAsync(args, ct);
        if (pid is null) return PerfSupport.NoPid();
        int dur = Math.Max(3, Math.Min(60, (int?)args["durationSeconds"] ?? 10));
        int topN = Math.Max(5, Math.Min(50, (int?)args["topN"] ?? 15));

        string trace = PerfSupport.TempFile(".nettrace");
        try
        {
            var (exit, output, error) = await PerfSupport.RunCliAsync(
                "dotnet-trace",
                $"collect -p {pid} -o \"{trace}\" --profile cpu-sampling --duration 00:00:{dur:00}",
                dur + 60, ct);
            if (error != null) return error;
            if (!File.Exists(trace))
                return new JObject { ["error"] = "dotnet-trace produced no trace file", ["cliOutput"] = PerfSupport.Cap(output, 2000) };

            var (rExit, report, rError) = await PerfSupport.RunCliAsync(
                "dotnet-trace", $"report \"{trace}\" topN -n {topN}", 120, ct);
            if (rError != null) return rError;

            Ui.BridgeStatus.RecordDebugInspect();
            Log.Info($"vs_trace_cpu(pid={pid}, {dur}s) -> report {report.Length} chars");
            return new JObject
            {
                ["ok"] = true, ["pid"] = pid, ["durationSeconds"] = dur,
                ["topMethods"] = PerfSupport.Cap(report),
                ["traceFile"] = trace, // kept on disk so the user can open it in VS/PerfView
            };
        }
        catch { try { File.Delete(trace); } catch { } throw; }
    }
}

/// <summary>vs_gc_dump - capture a GC heap snapshot and return the top types by size (dotnet-gcdump).</summary>
internal sealed class VsGcDumpTool : IIdeTool
{
    public string Name => "vs_gc_dump";
    public string Description =>
        "Capture a GC heap snapshot of a live process via dotnet-gcdump and return the TOP TYPES by "
        + "retained size - 'what is filling memory'. Defaults to the current debuggee; pass pid for any "
        + "process. Pair with vs_heap_diff (before/after) to find leaks; this one is the single-shot view.";

    public JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject
        {
            ["pid"] = new JObject { ["type"] = "integer", ["description"] = "Process id (default: the current debuggee)." },
        },
    };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        int? pid = await PerfSupport.ResolvePidAsync(args, ct);
        if (pid is null) return PerfSupport.NoPid();

        string dump = PerfSupport.TempFile(".gcdump");
        var (exit, output, error) = await PerfSupport.RunCliAsync(
            "dotnet-gcdump", $"collect -p {pid} -o \"{dump}\"", 120, ct);
        if (error != null) return error;
        if (!File.Exists(dump))
            return new JObject { ["error"] = "dotnet-gcdump produced no dump", ["cliOutput"] = PerfSupport.Cap(output, 2000) };

        var (rExit, report, rError) = await PerfSupport.RunCliAsync("dotnet-gcdump", $"report \"{dump}\"", 120, ct);
        if (rError != null) return rError;

        Ui.BridgeStatus.RecordDebugInspect();
        Log.Info($"vs_gc_dump(pid={pid}) -> report {report.Length} chars");
        return new JObject
        {
            ["ok"] = true, ["pid"] = pid,
            ["topTypes"] = PerfSupport.Cap(report),
            ["dumpFile"] = dump, // kept on disk so the user can open it in VS (File > Open) or PerfView
        };
    }
}
