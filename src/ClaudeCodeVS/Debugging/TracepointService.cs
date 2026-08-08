using System;
using System.Collections.Generic;
using System.Linq;
using ClaudeCodeVs.Protocol;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Debugging;

/// <summary>
/// Simulated tracepoints (ROADMAP "native tracepoints"): log-and-continue probes the model places
/// WITHOUT editing the file. EnvDTE has no "when hit: log + continue" action, so we simulate it:
/// a normal breakpoint tagged as ours + a <see cref="_events"/>.OnEnterBreakMode handler that — when
/// the LAST-HIT breakpoint carries our tag — evaluates the requested expressions, records the values,
/// and sets ExecutionAction=Go so the debuggee barely pauses. Everything is UI-thread EnvDTE
/// (convention #1: the DebuggerEvents COM event already arrives there).
///
/// COM-event pitfall: the DebuggerEvents object MUST be held in a field — VS hands out a fresh
/// wrapper per access, and a collected wrapper silently drops the subscription.
/// </summary>
internal static class TracepointService
{
    private const string TagPrefix = "claude-tracepoint:";
    private const int MaxRecordedHits = 200; // bounded log per tracepoint

    private sealed class Tracepoint
    {
        public string Id = "";
        public string File = "";
        public int Line;
        public string[] Expressions = Array.Empty<string>();
        public int MaxHits;
        public int HitCount;
        public bool Disabled;
        public readonly JArray Hits = new();
    }

    private static readonly object _gate = new();
    private static readonly Dictionary<string, Tracepoint> _tps = new(StringComparer.Ordinal);
    private static DebuggerEvents? _events; // MUST stay referenced (see class doc)

    /// <summary>Arm a tracepoint. UI thread (EnvDTE).</summary>
    public static JObject Set(string file, int line, string[] expressions, int maxHits)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var dte = ServiceProvider.GlobalProvider.GetService(typeof(SDTE)) as DTE;
        var dbg = dte?.Debugger;
        if (dte == null || dbg == null) return new JObject { ["error"] = "DTE debugger unavailable" };

        var tp = new Tracepoint
        {
            Id = "tp_" + Guid.NewGuid().ToString("N").Substring(0, 8),
            File = file,
            Line = line,
            Expressions = expressions,
            MaxHits = Math.Max(1, Math.Min(1000, maxHits)),
        };

        Breakpoints added;
        try { added = dbg.Breakpoints.Add(File: file, Line: line); }
        catch (Exception e) { return new JObject { ["error"] = $"could not set breakpoint at {file}:{line}: {e.Message}" }; }
        foreach (Breakpoint bp in added)
            try { bp.Tag = TagPrefix + tp.Id; } catch { }

        EnsureSubscribed(dte);
        lock (_gate) _tps[tp.Id] = tp;

        Log.Info($"tracepoint {tp.Id} armed at {file}:{line} ({expressions.Length} expr, maxHits {tp.MaxHits})");
        return new JObject
        {
            ["ok"] = true,
            ["tracepointId"] = tp.Id,
            ["file"] = file, ["line"] = line,
            ["expressions"] = new JArray(expressions),
            ["maxHits"] = tp.MaxHits,
            ["note"] = "Runs as log-and-continue: each hit records the expressions and execution resumes. "
                     + "Poll vs_get_tracepoint for the value timeline; vs_remove_tracepoint disarms.",
        };
    }

    /// <summary>The recorded hit timeline. Off-thread safe (pure registry read).</summary>
    public static JObject Get(string id)
    {
        lock (_gate)
        {
            if (!_tps.TryGetValue(id, out var tp)) return new JObject { ["error"] = "unknown tracepointId: " + id };
            return new JObject
            {
                ["tracepointId"] = id,
                ["file"] = tp.File, ["line"] = tp.Line,
                ["hitCount"] = tp.HitCount,
                ["maxHits"] = tp.MaxHits,
                ["exhausted"] = tp.Disabled,
                ["hits"] = new JArray(tp.Hits), // copy: the handler appends concurrently
            };
        }
    }

    /// <summary>Disarm + delete the tagged breakpoints. UI thread (EnvDTE).</summary>
    public static JObject Remove(string id)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Tracepoint? tp;
        lock (_gate) { _tps.TryGetValue(id, out tp); _tps.Remove(id); }
        if (tp == null) return new JObject { ["error"] = "unknown tracepointId: " + id };

        var dbg = (ServiceProvider.GlobalProvider.GetService(typeof(SDTE)) as DTE)?.Debugger;
        int deleted = 0;
        try
        {
            if (dbg != null)
                foreach (Breakpoint bp in dbg.Breakpoints)
                    try { if ((bp.Tag ?? "") == TagPrefix + id) { bp.Delete(); deleted++; } } catch { }
        }
        catch { }
        Log.Info($"tracepoint {id} removed ({deleted} breakpoint(s) deleted, {tp.HitCount} hit(s) recorded)");
        return new JObject { ["ok"] = true, ["tracepointId"] = id, ["hitCount"] = tp.HitCount, ["hits"] = new JArray(tp.Hits) };
    }

    private static void EnsureSubscribed(DTE dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_events != null) return;
        _events = dte.Events.DebuggerEvents;
        _events.OnEnterBreakMode += OnEnterBreakMode;
    }

    private static void OnEnterBreakMode(dbgEventReason reason, ref dbgExecutionAction execAction)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (reason != dbgEventReason.dbgEventReasonBreakpoint) return;

        try
        {
            var dbg = (ServiceProvider.GlobalProvider.GetService(typeof(SDTE)) as DTE)?.Debugger;
            var hit = dbg?.BreakpointLastHit;
            string? tag = null;
            try { tag = hit?.Tag; } catch { }
            if (dbg == null || hit == null || tag == null || !tag.StartsWith(TagPrefix, StringComparison.Ordinal)) return;

            string id = tag.Substring(TagPrefix.Length);
            Tracepoint? tp;
            lock (_gate) _tps.TryGetValue(id, out tp);
            if (tp == null || tp.Disabled) { execAction = dbgExecutionAction.dbgExecutionActionGo; return; }

            // Evaluate the expressions in the halted frame; never let one bad expression kill the hit.
            var values = new JObject();
            foreach (var expr in tp.Expressions)
            {
                try
                {
                    var e = dbg.GetExpression(expr, false, 1500);
                    values[expr] = e.IsValidValue ? e.Value : $"<invalid: {e.Value}>";
                }
                catch (Exception ex) { values[expr] = $"<error: {ex.Message}>"; }
            }

            lock (_gate)
            {
                tp.HitCount++;
                if (tp.Hits.Count < MaxRecordedHits)
                    tp.Hits.Add(new JObject
                    {
                        ["seq"] = tp.HitCount,
                        ["time"] = DateTime.Now.ToString("HH:mm:ss.fff"),
                        ["values"] = values,
                    });
                if (tp.HitCount >= tp.MaxHits)
                {
                    tp.Disabled = true;
                    try { hit.Enabled = false; } catch { } // stop pausing the app once the budget is spent
                }
            }

            execAction = dbgExecutionAction.dbgExecutionActionGo; // the "continue" half of log-and-continue
        }
        catch (Exception e)
        {
            Log.Warn($"tracepoint handler failed: {e.Message}");
        }
    }
}
