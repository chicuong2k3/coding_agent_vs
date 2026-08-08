# Full Feature Test Checklist (through 1.18.0)

One feature at a time — what to do, a sample prompt to give Claude, and the **Pass** criteria. New items (1.16→1.18) are marked 🆕.

**Common setup:**
1. Install the latest `.vsix` ([Releases](https://github.com/chicuong2k3/coding_agent_vs/releases)) or build locally: `msbuild src/ClaudeCodeVS/ClaudeCodeVS.csproj /restore /t:Build /p:Configuration=Release`.
2. Open the demo solution each section calls for: `demo/TestLab` (tests), `demo/RefMaze` (semantic), `demo/CheckoutBuggy`/`ComboScore` (basic debugging), `demo/LockJam` (deadlock), `demo/WebQuote` (attach/web), `demo/AsyncTrace` (async), `demo/SignalScan` (CPU).
3. **View > Other Windows > Claude Code** → **Launch Claude Code** → the pill turns green **Connected**.
4. Invoke `vs_*` tools through Claude with natural requests, or say "call tool X with parameters Y" directly.
5. Profiling (section H) needs three CLIs installed once — **one `dotnet tool install` per package**:
   ```powershell
   dotnet tool install -g dotnet-counters
   dotnet tool install -g dotnet-trace
   dotnet tool install -g dotnet-gcdump
   ```

**Recording results:** this file is the script, not the scratchpad — copy the template in [Appendix 1](#appendix-1--result-sheet-template) into a scratch file and fill it in as you go. If something fails on a prerequisite rather than on the feature itself, check [Appendix 2](#appendix-2--prerequisite-failures-not-feature-bugs) before filing a bug.

---

## A. Core bridge + diff (the main experience)

### A1. Launch + auto-connect (docked terminal)
Click **Launch Claude Code**. **Pass:** `claude` opens in a docked VS Terminal tab (not an external window), the panel pill turns green **Connected** with NO `/ide` needed.

### A2. External console
Click **External console**. **Pass:** `claude` opens in its own cmd window, still auto-connects; the window survives closing VS.

### A3. Diff Accept
Tell Claude: *"Add a comment at the top of Program.cs."* **Pass:** the diff opens NATIVELY in VS; clicking **Accept** writes the file; the panel feed records ✓.

### A4. Diff Reject
Same as A3 but click **Reject**. **Pass:** the file is UNCHANGED and Claude knows it was rejected.

### A5. Reject with feedback
Click **Reject with feedback**, type a reason (e.g. "use a different variable name"). **Pass:** Claude reads the reason and redoes the edit accordingly.

### A6. Auto-accept (run wild)
Enable the **Auto-accept** toggle. Ask Claude for an edit. **Pass:** the edit applies directly, NO diff opens; disabling the toggle brings the diff back. Restart VS → the toggle resets to OFF.

### A7. Gate skipped for Claude's own scratch files (1.14.4)
Ask Claude to write to its memory / create a temp file (e.g. "save this to your memory"). **Pass:** NO diff opens (the feed shows a skip line); but every file inside the workspace still opens a diff normally.

### A8. Selection context
Select a few lines of code, then ask: *"What does the code I have selected do?"* **Pass:** Claude answers about exactly that code without you pasting anything.

### A9. getDiagnostics — 🆕 precise spans (1.16)
Introduce a C# compile error (e.g. `intt x = 5;`), wait for the Error List. Ask: *"Any compile errors?"* **Pass:** the result has `range.start` ≠ `range.end` covering exactly the bad token, `"code": "CS0246"`, `"source": "roslyn"`. A C++ file (if any) still returns entries but with point ranges — by design.

### A10. Panel
Watch the panel while doing the items above. **Pass:** the status pill matches the state; the feed shows curated lines; the Edits ✓/✗ and Debugger inspected/driven counters increment correctly; the token card shows Latest/Session; the **≈ Show est. cost** button reveals cost; the "Awaiting your review" strip appears while a diff is pending.

### A11. 🆕 Usage breakdown line (1.18)
Run one turn that uses many tools (have Claude read several files, call vs_debug_state) and one turn with a subagent (*"use an agent to find X for me"*). **Pass:** a dim line appears below Session: `Subagents (exact): N calls · ↑… ↓…   Top tools (est.): Read ≈40k · …`; it stays hidden when neither was used.

### A12. Notifications
Put VS in the background (switch to another app), give Claude a long task. **Pass:** when the turn finishes → a "Claude finished" InfoBar + taskbar flash; when Claude needs input (a permission prompt in the terminal) → a "needs your input" notification. Turning the **Notify** toggle off silences both.

### A13. Attachments tray
Take a screenshot (Win+Shift+S) → click **Paste** on the panel; also drag-drop a file from Explorer. **Pass:** a chip appears in the tray with a token estimate; an `@` reference lands in the CLI's input box (inserted, NOT submitted); Claude can actually see the image; clicking a chip re-mentions it; ✕ removes it. xlsx/zip files attach too (🧰 label). Staged copies live in a gitignored `.claude/attachments/`.

### A14. Reconnect hardening
With a diff pending, kill the `claude` terminal entirely. **Pass:** the orphaned diff auto-rejects and closes. Relaunch `claude` → it reconnects to the same bridge (no VS restart needed).

---

## B. Debugger — READ (vs-debug, no toggle needed)

Fixture: `demo/CheckoutBuggy` or `demo/ComboScore`, F5 and stop at a breakpoint.

### B1. Push context at break
While paused at a breakpoint, type any prompt to Claude. **Pass:** Claude already knows where you're stopped (file/line/stack/locals) without you pasting anything — the context is injected at prompt submit.

### B2. vs_debug_state — 🆕 per-frame file/line (1.18)
*"What's the current debug state?"* **Pass:** mode/stoppedAt/callStack/locals; **every your-code frame carries `file` + `line`** (framework frames staying name-only is correct).

### B3. vs_evaluate / vs_expand / vs_get_frame_locals
*"What's the value of X? Expand object Y. Locals of frame 2?"* **Pass:** correct values; expand walks into child properties; a different `threadId` reads another thread's locals.

### B4. vs_threads — 🆕 (file:line) suffix (1.18)
*"List the threads."* **Pass:** each thread has a stack; frames carry ` (path\file.cs:42)` where symbols exist; a lock-waiting thread is flagged with `lockOwnerThreadId` (clearest with LockJam).

### B5. vs_exception
Break at a throw (or with `$exception` in a catch). *"What's the current exception?"* **Pass:** type + message + stack of the exception.

### B6. vs_list_breakpoints / vs_list_processes
**Pass:** the breakpoint list matches VS's Breakpoints window; list processes returns attachable processes.

### B7. The ClrMD suite (needs a .NET app running under the debugger)
- `vs_wait_chains` (LockJam): monitor ownership chains + deadlock suspects.
- `vs_async_stacks` (AsyncTrace): logical async stacks.
- `vs_heap_stats`: heap composition + GC health.
- `vs_threadpool`: thread counts + backlog + starvation warning.
- `vs_gc_roots <type>`: retention path (why an object is alive).
- `vs_heap_diff`: baseline → act → diff shows the growing types (leak finder).
**Pass:** each tool returns structured JSON without errors; large results carry `truncated:true`.

---

## C. Debugger — DRIVE (enable "Allow Claude to drive debugger")

### C1. Gate
Toggle OFF → call any drive tool. **Pass:** a clear refusal with a hint to enable the toggle. Restart VS → the toggle resets to OFF.

### C2. continue / step over / into / out / run_to_line
*"Step over twice and tell me what changed in the locals."* **Pass:** each command returns the NEW break position (it awaits the next break; the UI never hangs).

### C3. vs_break_all (LockJam)
The app is hung in a deadlock (no breakpoint will ever hit). *"The app is hung — pause it and look."* **Pass:** the app stops mid-run; vs_threads/vs_wait_chains then walk the cycle.

### C4. set/remove breakpoint
By file:line AND by function name; try `condition` and `hitCount` too. **Pass:** the breakpoint appears in the Breakpoints window and hits under the right condition.

### C5. vs_break_on_thrown (WebQuote)
*"Break right where FooException is thrown."* **Pass:** stops at the THROW site (first-chance), not at the catch.

### C6. freeze_thread / set_next_statement
**Pass:** a frozen thread stops advancing; set-next moves the execution pointer (yellow arrow) to the given line.

### C7. start/stop_debugging + attach/detach (WebQuote)
*"Attach to the running WebQuote process."* **Pass:** attach works without F5; after detach the app keeps running; start = F5 to first break; stop = Shift+F5.

### C8. 🆕 Tracepoints (1.17)
An app with a loop running under the debugger. *"Set a tracepoint at file:line logging `item.Price` and `total`, maxHits 5."* **Pass:** the app does NOT halt (barely hiccups); `vs_get_tracepoint` returns a `[{seq,time,values}]` timeline; after 5 hits → `exhausted:true` and the hiccups stop; `vs_remove_tracepoint` deletes the breakpoint from VS.

---

## D. Data breakpoints (Concord; x64; drive toggle on)

### D1. vs_set_data_breakpoint
While at a break, watch an INSTANCE field: *"Watch owner.field and report every change."* **Pass:** on continue, every mutation lands in the timeline; `vs_get_data_changes` returns `[{previous,current,type}]`; `vs_remove_data_breakpoint` disarms. Static/local/struct fields → a refusal with an explanation (by design).

### D2. stopOnChange — recurring, not one-shot
Same watch with `stopOnChange:true` on a field written in a LOOP (several iterations). **Pass:** VS halts on the FIRST change one statement after the write; `vs_continue` → it halts AGAIN on the next write, and again — it re-arms every time like a normal breakpoint, it does not fire once and go quiet. `vs_get_data_changes` shows `broke:true` and a `breakCount` that matches the number of halts. Add a `condition` (e.g. `current > 100`) → only the matching writes halt, but EVERY write still appears in the timeline.

### D3. Multi-watch fan-out on the same address
With one watch already armed on `owner.field`, arm a SECOND one on the **same** field (a different `requestId`, e.g. one plain + one with a `condition`). **Pass:** BOTH requests keep receiving changes — the engine binds a single data breakpoint per address and fans out to every watcher, so the second `vs_set_data_breakpoint` must not shadow/silence the first. Check by calling `vs_get_data_changes` on each `requestId`: both timelines advance. Removing one → the other keeps firing; removing the last one disarms the engine binding.

### D4. Multi-watch on different fields
Watch two DIFFERENT instance fields at once. **Pass:** each `requestId` shows only its own field's mutations, no cross-talk.

---

## E. Semantic (vs-semantic; needs a loaded C#/VB solution; fixture `demo/RefMaze`)

### E1. vs_search_symbols
*"Find symbols named Process."* **Pass:** a candidate list where each entry has a `symbolId` (like `M:Ns.Type.Method(...)`).

### E2. vs_find_references
*"Who uses IHandler.Process?"* **Pass:** includes interface-dispatched calls AND the explicit implementation (the cases grep misses), each as file:line:column + snippet.

### E3. vs_go_to_definition
Ask by `file`+`line` inside an overload set. **Pass:** resolves to EXACTLY the one overload being called, not the whole group.

### E4. vs_find_implementations
*"Which classes implement IHandler?"* **Pass:** all 3 RefMaze implementations, including the explicit one.

### E5. vs_call_hierarchy — 🆕 transitive callees (1.17)
Callers: *"Who (transitively) calls X?"* → a multi-level caller tree, recursion marked `recursionElided`. Callees: *"What does X call, and what do those call?"* → a **nested callee tree** (no longer flat depth-1), with `maxDepth`.

### E6. vs_type_hierarchy
**Pass:** base chain + interfaces, and derived types.

### E7. vs_get_selection
Select an identifier → *"What do I have selected? Navigate from it."* **Pass:** returns the selected text + the `symbolId` at that position.

### E8. vs_decompile
*"Decompile System.Linq.Enumerable.Where."* **Pass:** returns just that member's C# body (not a giant whole file); a forwarded BCL type (String…) auto-retries via SourceLink to REAL source, with `source: decompiled|source` and `bodyAvailable` set correctly.

### E9. 🆕 vs_rename (1.17)
*"Preview renaming IHandler.Process → Handle."* **Pass (preview):** `applied:false`, the files list includes the interface AND all 3 implementations, each with `edits`+`sample`. *"Apply it"* → `applied:true`, the name changes everywhere. **ONE Ctrl+Z undoes the entire rename** (test this carefully). An invalid `newName` (`123abc`) → refused.

---

## F. Test integration (vs-debug; fixture `demo/TestLab`)

### F1. vs_list_tests
*"What tests does the solution have?"* **Pass:** real FQNs (Roslyn attribute scan), no build needed first.

### F2. vs_run_test
*"Run test X."* **Pass:** builds automatically; per-test `{outcome, errorMessage, errorStackTrace, durationMs}` — pass/fail distinguished for REAL (not Success=run-completed). `collectCoverage:true` → a .coverage file under attachments.

### F3. 🆕 vs_run_test profile:true (1.18, EXPERIMENTAL)
**Pass:** no more `Status=Cancelled` + the old note; per-test results come back (possibly with a .diagsession). **If still Cancelled** → the GUID/property doesn't match this VS build; report it so we can iterate (try another `profilerToolId`). Failing this item blocks nothing else.

### F4. 🆕 vs_run_affected (1.16)
*"I just edited Score.cs — run the affected tests."* **Pass:** `affected.tests` = FQNs with `callDistance`; `run.tests` = outcomes; `listOnly:true` only lists; a file no test reaches → `count:0` + note.

### F5. vs_rerun_failed
Run the suite with failures → *"Re-run only the failed tests."* **Pass:** only the previously failing tests run again.

### F6. vs_debug_test
*"Debug test X and stop where it fails."* **Pass:** the test runs under the debugger and halts at the throw/assert (pairs with break-on-thrown).

### F7. vs_hunt_flaky — 🆕 idle-wait (1.18)
On a ~1-in-3 intermittent test: *"Hunt test X for 12 runs and measure the rate — measureRate:true."* **Pass:** returns a `huntId` within ≤40s; `vs_hunt_result` polling reaches a full **12/12 executed** (pre-1.18 often fell short), rate ≈1/3; `vs_hunt_cancel` stops it.

### F8. vs_catch_flaky
*"Catch flaky test X red-handed."* (needs the drive toggle). **Pass:** loops under the debugger until the failing iteration HALTS AT THE THROW, the exception live in the frame for inspection.

---

## G. Screen capture (enable "Allow screen capture")

### G1. Gate
Toggle OFF → call a capture tool. **Pass:** refusal with a hint.

### G2. vs_capture_window
`target:debuggee` (a UI app under debug), `target:ide`, `target:window title:"Edge"`. **Pass:** the PNG is staged as a tray chip + a `path` is returned (Claude Reads that path and truly sees the image); a minimized window → an error asking to restore; an unmatched title → an error with `visibleWindows`.

### G3. vs_capture_screen
`monitor:0` / `all:true`. **Pass:** exactly the requested monitor(s).

### G4. 🆕 Region crop (1.17)
`region {x:0,y:0,width:800,height:600}` on both tools. **Pass:** the result reports `width:800,height:600` and the PNG shows exactly that region; an out-of-bounds crop clamps without erroring.

---

## H. Profiling (🆕 1.17; needs the 3 CLIs from Setup; fixture `demo/SignalScan` under F5)

### H1. vs_perf_counters
*"Sample counters of the debugged app for 10 seconds."* **Pass:** a `counters` object with `cpu-usage`, `working-set`, `alloc-rate`, `gc-heap-size`…

### H2. vs_trace_cpu
*"Where is the CPU going? Sample 10 seconds."* **Pass:** `topMethods` = a top-N hot-method table + a `traceFile` (opens in PerfView/VS).

### H3. vs_gc_dump
*"What's filling memory?"* **Pass:** `topTypes` by size + a `dumpFile`.

### H4. Missing CLI
On a machine without the tools (or temporarily uninstall one) → **Pass:** an error with the `dotnet tool install -g …` hint, no crash.

---

## I. Infrastructure (no UI of its own)

### I1. Multi-agent AgentProfile (1.15)
Nothing new to click — **Pass** = everything in A→H behaves exactly as before (Claude Code behavior unchanged after the parameterization). Details: `docs/MULTI-AGENT.md`.

### I2. CI/CD (1.15)
**Pass:** the Actions tab — every push/PR has a green **Build** run with a `ClaudeCodeVS-vsix` artifact; every `v*` tag has a Release with `ClaudeCodeVS.vsix` attached (v1.15.0→v1.18.0 all do).

---

## Known notes (not bugs)

- **Four IDE-protocol tools have no checklist item because the CLI never calls them.** `getOpenEditors`, `getWorkspaceFolders`, `checkDocumentDirty` and `saveDocument` are implemented and correct, but the current CLI exposes only `getDiagnostics` + `executeCode` to the model and drives the rest internally — so there is no way to trigger them by asking Claude. They stay dormant until the CLI surfaces them; to exercise them meanwhile, drive them from the `spike/` harness (`dotnet run --project spike`), which speaks the raw protocol. "Claude can't list my open editors" is expected, not a regression.
- The CLI's terminal prompt still asks alongside the diff (a redundant second gate) — an architectural limitation recorded in ROADMAP Phase 4.
- `vs_rename` is C#/VB only and the symbol must have source in the solution.
- A tracepoint hit while a `vs_continue` is awaiting the next break may resolve that wait — a known limit of the simulated log-and-continue.
- `vs_trace_cpu`/`vs_gc_dump` keep their trace/dump files in `%TEMP%` — delete manually if needed.
- An `at_mentioned` reference sent mid-turn or with the agents view focused may be silently dropped by the CLI — click the chip to re-mention.
- F3 (profile:true) is experimental — report a failure, it blocks nothing else.
- Everything 1.16→1.18 is build-verified only; this checklist IS the live-verification pass.

---

## Appendix 1 — result sheet template

Copy into a scratch file (NOT into this doc) and fill in as you go. `✅` pass · `❌` fail · `⚠️` pass with a caveat · `⏭️` skipped (say why).

```
Tester:        Date:        VSIX version:        VS build:        claude --version:

A. Core bridge + diff
[ ] A1  launch + auto-connect          [ ] A8   selection context
[ ] A2  external console               [ ] A9   getDiagnostics precise spans
[ ] A3  diff accept                    [ ] A10  panel
[ ] A4  diff reject                    [ ] A11  usage breakdown line
[ ] A5  reject with feedback           [ ] A12  notifications
[ ] A6  auto-accept                    [ ] A13  attachments tray
[ ] A7  scratch-file gate skip         [ ] A14  reconnect hardening

B. Debugger READ
[ ] B1 push context   [ ] B2 debug_state   [ ] B3 evaluate/expand/locals
[ ] B4 threads        [ ] B5 exception     [ ] B6 breakpoints/processes
[ ] B7 ClrMD: wait_chains __ async_stacks __ heap_stats __ threadpool __ gc_roots __ heap_diff __

C. Debugger DRIVE
[ ] C1 gate   [ ] C2 step family   [ ] C3 break_all   [ ] C4 set/remove bp
[ ] C5 break_on_thrown   [ ] C6 freeze/set_next   [ ] C7 start/stop/attach/detach   [ ] C8 tracepoints

D. Data breakpoints
[ ] D1 set/get/remove   [ ] D2 stopOnChange recurring   [ ] D3 fan-out same address   [ ] D4 different fields

E. Semantic
[ ] E1 search   [ ] E2 find_refs   [ ] E3 go_to_def   [ ] E4 find_impls   [ ] E5 call_hierarchy
[ ] E6 type_hierarchy   [ ] E7 get_selection   [ ] E8 decompile   [ ] E9 rename (+ single Ctrl+Z)

F. Tests
[ ] F1 list   [ ] F2 run (+coverage)   [ ] F3 profile (experimental)   [ ] F4 run_affected
[ ] F5 rerun_failed   [ ] F6 debug_test   [ ] F7 hunt (12/12)   [ ] F8 catch_flaky

G. Capture           H. Profiling                    I. Infrastructure
[ ] G1 gate          [ ] H1 perf_counters            [ ] I1 multi-agent profile
[ ] G2 window        [ ] H2 trace_cpu                [ ] I2 CI/CD
[ ] G3 screen        [ ] H3 gc_dump
[ ] G4 region crop   [ ] H4 missing-CLI hint

Failures (one line each): id — what happened — exact error text — repro steps
```

---

## Appendix 2 — prerequisite failures (not feature bugs)

When an item fails, check here first: these symptoms mean the environment isn't set up, not that the feature is broken. File a bug only after ruling these out.

### B7 — the ClrMD suite (`vs_wait_chains`, `vs_async_stacks`, `vs_heap_stats`, `vs_threadpool`, `vs_gc_roots`, `vs_heap_diff`)

These six shell out to `ClrMdWorker.exe`, bundled in the .vsix under `ClrMdWorker\` (ClrMD cannot load in-proc in devenv). Each failure mode returns a distinct `error` string:

| `error` contains | Cause | Fix |
|---|---|---|
| `this tool is x64-only for now` | You're on ARM64 Windows. The worker can't snapshot an ARM64 process. | Not fixable here — every non-ClrMD debugger tool still works. Mark B7 `⏭️ skipped (ARM64)`. |
| `ClrMD worker not found at <path>` | The `ClrMdWorker\` folder didn't ship in the .vsix, or the extension is running from a stale install dir. | Confirm `ClrMdWorker.exe` exists at the printed path. If missing, the packaging step dropped it — that IS a bug, report it with the path. |
| `ClrMD worker timed out (Ns)` | The snapshot hung — usually a very large heap or the target being mid-GC. | Retry once. Persistent timeouts on a small demo app = a real bug. |
| `ClrMD worker produced no output (exit N)` | The worker crashed on launch — nearly always a missing `.NET Framework 4.8` runtime or a corrupted `ClrMdWorker.exe.config`. | Run it by hand: `<installdir>\ClrMdWorker\ClrMdWorker.exe heapstats <pid>`. The real exception prints to the console. |
| `unparseable worker output` | The worker wrote something before its JSON (a first-chance trace, a loader warning). | Report with the `raw` field — that field exists for exactly this. |

Two more preconditions the tools can't detect for you:
- **Nothing is being debugged.** All six need a live .NET process under the VS debugger. F5 the fixture first (`demo/LockJam` for wait chains, `demo/AsyncTrace` for async stacks) — no debuggee means no PID to snapshot.
- **The debuggee is .NET Framework-only or native.** ClrMD reads managed heaps; a pure-native target returns empty structures, not an error.

To iterate without VS in the loop, drive the worker directly against any PID — that's what it's for:
```powershell
& "<installdir>\ClrMdWorker\ClrMdWorker.exe" waitchains <pid>
```

### D — data breakpoints
Needs **x64** (Concord data breakpoints don't arm on ARM64), the **drive toggle ON**, and an **instance field of a heap object** already in scope at the current break. A refusal naming statics/locals/struct fields is by design (D1), not a failure.

### E — semantic tools
`{"available":false}` means no C#/VB project is loaded — open the actual `.sln` (`demo/RefMaze`), not a folder. Roslyn also needs the solution to have finished loading; retry once if you asked immediately after opening.

### F — test tools
The engine is VS's own Test Explorer, reached through internal types by reflection. `vs_list_tests` returning `[]` on a solution that clearly has tests means the Roslyn attribute scan found no `[Fact]/[Test]/[TestMethod]` — check you opened `demo/TestLab`'s solution. A run failing to build is a build error, not a tool bug; the tools self-build via `SolutionBuild.Build(true)`.

### H — profiling
`vs_perf_counters` / `vs_trace_cpu` / `vs_gc_dump` need `dotnet-counters` / `dotnet-trace` / `dotnet-gcdump` on `PATH` (Setup step 5). A missing one returns the install hint — that's item H4 passing, not a failure.
