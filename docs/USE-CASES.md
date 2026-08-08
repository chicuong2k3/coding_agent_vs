# Is this worth it? Feature-by-feature, against a plain terminal

The other guides ([`DEBUGGER.md`](DEBUGGER.md), [`SEMANTIC.md`](SEMANTIC.md), [`TESTING.md`](TESTING.md), [`QOL.md`](QOL.md)) explain what each feature *does*. This one answers a harder question: **for a real project, does it actually save you time compared to just running `claude` in a terminal?**

For some features the answer is "it changes how you work". For others it's "it saves twenty seconds". A few are genuinely just nicer, not faster. This document says which is which, because a list of 58 tools tells you nothing about whether any of them matter on a Tuesday afternoon.

**Jump to:** [How to read this](#how-to-read-this) · [Tier 1: no terminal equivalent](#tier-1--no-terminal-equivalent) · [Tier 2: possible in a terminal, but worse](#tier-2--possible-in-a-terminal-but-worse) · [Tier 3: convenience, not throughput](#tier-3--convenience-not-throughput) · [When it is NOT worth it](#when-this-is-not-worth-it) · [Measure it yourself](#measure-it-yourself)

---

## How to read this

Value here is the product of two things, and people usually only think about the first:

**Value = (can the CLI do this at all?) × (how often do you hit it?)**

A tool that does something impossible but comes up twice a year is worth less day to day than a tool that saves fifteen seconds on every loop. Both are listed below with an honest frequency, not a marketing one.

Every entry follows the same shape: a concrete scenario, what happens in a plain terminal, what happens here, and what the delta really is.

The bottom line up front, if you only read one paragraph: **the real dividing line is that Claude can see your application while it is running.** Everything else is a supporting act.

---

## Tier 1 — no terminal equivalent

These aren't faster versions of something the CLI does. The CLI has no path to them at all.

### Debugger reads — the one that changes how you work

`vs_debug_state` · `vs_evaluate` · `vs_expand` · `vs_get_frame_locals` · `vs_threads` · `vs_exception` · the break-time context push

**Scenario.** An order total comes out wrong, but only for one coupon type. The logic spans a pricing service, a discount rule chain, and a rounding helper.

**In a terminal.** Claude reads the code and forms a hypothesis. It can't see any values, so it asks you to add logging, rebuild, re-run, and paste the output. The hypothesis was wrong, so it narrows and asks for more logging. Rebuild, re-run, paste. Four to six rounds, each one gated on a build.

**Here.** You F5, stop at a breakpoint in the rule chain, and type your question. The stack, the frame, and the locals are already in Claude's context before it answers — the `UserPromptSubmit` hook injects them. It asks `vs_evaluate` for anything else it wants: the coupon object, the intermediate subtotal, the rounding mode. If the bug is on another thread, `vs_get_frame_locals` reads that thread's frame without switching the UI.

**The delta.** Not "Claude is smarter". The rebuild loop disappears. Investigation stops being a series of guesses separated by builds and becomes a conversation with a live process.

**Frequency: high.** State-dependent bugs are the ones that eat afternoons. This is where the extension earns its keep.

---

### `vs_catch_flaky` — catching an intermittent test red-handed

**Scenario.** A test fails maybe one run in three. CI is red often enough to be ignored, which is worse than being red always.

**In a terminal.** Claude can loop `dotnet test` and report "failed on iteration 7". That's the end of what it can tell you. The failing process is gone; all that survives is a stack trace, and for an assertion failure the stack trace usually points at the assert, not the cause.

**Here.** `vs_catch_flaky` runs the test under the debugger with break-on-thrown armed, iterating until the failing run halts **at the throw** — with the exception live in the frame, the locals intact, and the other threads inspectable. When it can't infer the exception type from a bare assertion, it arms the framework assertion base types (`XunitException`, NUnit's, MSTest's) so an assert failure halts too.

**The delta.** "It failed sometimes" becomes "here is the state at the moment it failed". That's the whole difficulty of a flaky test.

**Frequency: low, but each occurrence is expensive.** Flaky tests are rare per-week and costly per-instance.

---

### `vs_run_affected` — the only test tool with a large everyday delta

**Scenario.** You changed `Score.cs`. The suite has 2000 tests and takes eight minutes.

**In a terminal.** `dotnet test` runs everything, or you hand-write a filter and hope you guessed the blast radius right. Eight minutes per iteration means you batch up changes and stop running tests as often — the loop degrades quietly.

**Here.** The changed file goes through a Roslyn caller-graph BFS up to test methods, and only those tests run, in a single engine pass. Each result carries a `callDistance` so you can see *why* a test was considered affected. `listOnly:true` shows the set without running it.

**The delta.** Eight minutes to roughly twenty seconds, with a defensible reason for the selection rather than a guessed filter.

**Frequency: high on a large suite, near zero on a small one.** If your whole suite runs in fifteen seconds, skip this — `dotnet test` is already fine, and this document isn't going to pretend otherwise.

---

### ClrMD diagnostics — for when nothing else can tell you

`vs_wait_chains` · `vs_async_stacks` · `vs_heap_stats` · `vs_threadpool` · `vs_gc_roots` · `vs_heap_diff` (plus `vs_break_all` to get there)

**Scenario A — the hang.** A service stops responding under load. No breakpoint ever hits, because nothing is executing your code any more.

**In a terminal.** Essentially nothing. The manual path is `dotnet-dump` plus WinDbg plus SOS plus knowing `!syncblk` — a skill most teams have one person for, if any.

**Here.** `vs_break_all` pauses the process mid-run, then `vs_wait_chains` returns the monitor ownership graph with the deadlock suspects already identified, and `vs_threads` marks each waiter with the `lockOwnerThreadId` it's blocked behind.

**Scenario B — the leak.** Memory climbs steadily over a couple of hours.

**Here.** `vs_heap_diff` takes a baseline, you exercise the suspect path, and the diff names the types that grew. Then `vs_gc_roots` on one of them gives you the retention path — the specific reason that object is still alive.

**The delta.** Enormous per incident. This is the difference between a triage that takes a day and one that takes ten minutes.

**Frequency: low.** Weeks or months between incidents on most projects. Value it as insurance, not as daily throughput.

**Constraint worth knowing up front:** x64 only (the bundled ClrMD worker can't snapshot an ARM64 process), and the target must be a managed process under the debugger.

---

### `vs_decompile` — replacing a guess with a read

**Scenario.** EF Core's `SaveChanges` behaves in a way the docs don't explain, or a NuGet package does something surprising and ships no source.

**In a terminal.** Claude answers from training memory. Sometimes that's right. Sometimes it describes a different major version's behaviour with complete confidence, which is worse than not knowing.

**Here.** `vs_decompile` returns the actual member body through Visual Studio's own metadata-as-source service (the same ILSpy-backed decompiler behind Go To Definition). For core BCL types that only decompile to a signature stub, it automatically retries via SourceLink and fetches the real .NET source, telling you which you got via `source: decompiled|source` and `bodyAvailable`.

**The delta.** "Claude thinks it works like this" becomes "Claude read the code". On a library-behaviour question that distinction is the whole answer.

**Frequency: medium.** Every time you're fighting a framework rather than your own code.

---

### `vs_set_data_breakpoint` — who wrote to this field?

**Scenario.** A field holds the wrong value by the time you look at it, and the assignment could be in any of a dozen places — or in code you didn't write.

**In a terminal.** No path at all. Not only can the CLI not do this: **Visual Studio's own UI can't set a managed data breakpoint programmatically either.** This ships as a bundled Concord debug-engine component precisely because no automation surface exists.

**Here.** Watch an instance field, continue, and get a structured `[{previous, current, type}]` timeline of every mutation. `stopOnChange:true` halts one statement after each write so you can inspect the locals at the mutation site — and it re-arms, so it catches every write in a loop, not just the first.

**The delta.** Answers "who wrote to this" directly, instead of by bisecting with breakpoints.

**Frequency: low.** A specialist tool. But when the question comes up there's no alternative.

**Constraint:** instance fields of heap objects only — statics, locals, and struct fields are refused with an explanation.

---

### `vs_capture_window` / `vs_capture_screen` — showing Claude the UI

**Scenario.** A WPF window lays out wrong, or a web page renders differently than intended.

**In a terminal.** You screenshot manually, save it somewhere, and type the path into the prompt. It works — it's just friction, every single time.

**Here.** One tool call captures the debuggee window, the IDE, a window by title, or a monitor, optionally cropped to a region. The PNG is staged as a tray chip and the **path** is returned rather than an image blob (which costs roughly 10–20× the tokens).

**The delta.** Around twenty seconds and a broken train of thought, per iteration.

**Frequency: high if you build UI, zero if you don't.**

---

## Tier 2 — possible in a terminal, but worse

The CLI can reach these. It reaches them less accurately or more slowly.

### `getDiagnostics` — errors without a build

**Scenario.** The tightest loop there is: make an edit, find out if it compiles.

**In a terminal.** `dotnet build`, wait 10–30 seconds, parse the output text.

**Here.** The Error List already has the answer, computed incrementally by Roslyn as you type — zero wait. Since 1.16 C#/VB entries are upgraded to Roslyn-precise start/end spans, so Claude gets the exact bad token rather than a point location.

**The delta.** Small per call, but this is the highest-frequency call in the whole system. It compounds more than anything else in this document.

**Frequency: very high.**

**Caveat:** it needs a loaded project. Loose files have no Error List entries.

---

### Semantic navigation — worth it in proportion to repo size

`vs_find_references` · `vs_go_to_definition` · `vs_find_implementations` · `vs_call_hierarchy` · `vs_type_hierarchy` · `vs_search_symbols`

Be honest about this one, because it's the feature most often oversold.

**On a small repo (under ~50 files), grep is fine.** Claude greps, gets a manageable number of hits, reads them, and is correct. The extension wins very little. If that's your project, don't choose the extension for this.

**On a large repo with a common method name, grep degrades badly.** Search `Process` and you get 200 hits, 180 of them irrelevant — and Claude burns context reading all of them. Worse, grep *misses* the two cases that matter most during a refactor: calls dispatched through an interface, and explicit interface implementations, which don't textually contain the name you searched.

**Here.** `vs_find_references` returns the 12 real references from Roslyn's resolved semantic model, including both cases grep misses. `vs_go_to_definition` on a call inside an overload set resolves to the exact overload, not the group.

**The delta.** Fewer tokens spent, and — the part that actually costs you — no missed call site during a rename or a signature change.

**Frequency: scales with codebase size.** Low value on a small project, high on a large one.

**Related:** `vs_rename` performs the rename through Roslyn's `Renamer`, previews by default, and applies as **one undo unit** — a single Ctrl+Z reverts the whole thing. A `sed`-based rename either misses the explicit implementation or renames something it shouldn't.

---

### The rest of the test tools — smaller delta than they look

`vs_list_tests` · `vs_run_test` · `vs_rerun_failed` · `vs_debug_test`

Stated plainly: **`dotnet test` in a terminal already works, and Claude can already run it and read the output.** Listing, running, and re-running tests are not where this extension differentiates itself. What these add:

- `vs_list_tests` discovers via a Roslyn attribute scan, so it lists without needing a build first.
- `vs_run_test` returns real per-test `{outcome, errorMessage, errorStackTrace, durationMs}` rather than text to parse, and self-builds so you never need a manual Ctrl+Shift+B.
- `vs_debug_test` runs a single test under the debugger and halts at the failure — which does have no terminal equivalent, and pairs with break-on-thrown.

Real but modest. The two test tools that genuinely justify themselves are [`vs_run_affected`](#vs_run_affected--the-only-test-tool-with-a-large-everyday-delta) and [`vs_catch_flaky`](#vs_catch_flaky--catching-an-intermittent-test-red-handed), both in Tier 1.

---

### Debugger drive — hands-off stepping

`vs_continue` · `vs_step_over/into/out` · `vs_run_to_line` · `vs_set_breakpoint` · `vs_break_on_thrown` · `vs_attach` / `vs_detach` · `vs_break_all`

**Scenario.** "Step over twice and tell me what changed in the locals."

**In a terminal.** You do the stepping by hand and narrate what you see.

**Here.** Claude drives and reports. Each command awaits the next break properly, so the UI never hangs. `vs_break_on_thrown` stops at the throw site rather than the catch — which is often the entire investigation. `vs_attach` debugs an already-running process without an F5.

**The delta.** Moderate. It removes you from a loop you'd otherwise be manually clicking through, which matters most on long stepping sessions.

**Frequency: medium.** Gated behind the **Allow Claude to drive debugger** toggle, off by default, reset every session — by design, since it's the surface that moves your program.

---

### Tracepoints and profiling

`vs_set_tracepoint` · `vs_perf_counters` · `vs_trace_cpu` · `vs_gc_dump`

**Tracepoints** are printf-debugging without the rebuild: log an expression at a line, keep running, read back a `[{seq, time, values}]` timeline. In a terminal the equivalent is adding a `Console.WriteLine` and rebuilding — which is exactly the loop [debugger reads](#debugger-reads--the-one-that-changes-how-you-work) exist to kill, so this is the same win in a different shape, for the case where you can't afford to stop.

**Profiling** shells out to `dotnet-counters` / `dotnet-trace` / `dotnet-gcdump`, which Claude could run itself in a terminal. What the extension adds is that it targets the debuggee automatically and parses the output into a structured top-methods or top-types table. Convenience over the raw CLI, not new capability.

**Frequency: low for both.**

---

## Tier 3 — convenience, not throughput

Real improvements to the experience. Don't build a productivity case on them.

### The native diff with Accept / Reject

This is the feature people lead with, and it's the weakest productivity argument in the set. The CLI **already** shows a diff in the terminal and **already** asks for permission. What you get here is a proper VS diff window, syntax-highlighted, reviewed with editor tooling instead of terminal text, plus **Reject with feedback** to send a reason back in one step.

There's a known wart, recorded in `ROADMAP` Phase 4: **the CLI's own terminal prompt still asks alongside the diff**, so you currently pass through two gates for one edit.

Better review ergonomics. Not fewer minutes.

### Selection context

Select code, ask "what does this do", skip the copy-paste. Tiny per use — but you do it constantly, and small constant savings are the kind that survive.

### Notifications

Turn-finished and needs-input surface as an in-IDE InfoBar plus a bounded taskbar flash when VS is in the background. Saves you polling a terminal on a second monitor. Small, frequent, easy to underrate on a long task.

### Attachments tray

Paste a screenshot or drop a file and it's staged with a token estimate and an `@` reference inserted into the composer. Formats the model can't read directly (xlsx, mp4, zip) are still mentioned with a needs-tool label rather than rejected. Removes fiddly path-typing.

### The panel itself

Status pill, activity feed, edit and debugger counters, token and cost cards, the usage breakdown line. This is **information, not capability** — useful for knowing what Claude did and what it cost, worth roughly zero minutes of throughput. It's listed here so it isn't mistaken for a feature that speeds you up.

---

## When this is NOT worth it

Straight answer, because a document that only lists upsides isn't useful:

- **You don't use the debugger.** The single biggest win is exposing a live process to Claude. If you never break into running code, most of Tier 1 doesn't apply to you and the terminal is close to as good.
- **Small codebase.** Under a few dozen files, grep genuinely competes with the semantic model, and a fast test suite makes `vs_run_affected` pointless.
- **Not on Windows / not in Visual Studio.** This is an in-proc VS 2026 VSIX. There is no other configuration.
- **ARM64.** Every ClrMD tool refuses (the worker can't snapshot an ARM64 process), as do data breakpoints. Everything else — state, evaluate, threads, breakpoints, stepping, attach, semantic, tests — still works.
- **You mostly write new code rather than debug existing code.** Generation is the CLI's job and the extension doesn't make it better. The extension is about *understanding* code that already runs.
- **Non-.NET work.** Semantic tools are C#/VB only. Debugger and test integration follow Visual Studio's support.

---

## Measure it yourself

Don't take the tiering above on faith — the honest way to settle this is a stopwatch, and it takes about a week of normal work.

**The experiment.** The next time you hit a state-dependent bug (wrong value, works-on-my-machine, only-with-this-input), do it twice:

1. **Terminal first.** Plain `claude`, no extension tools. Count the rounds of `Console.WriteLine` + rebuild + paste. Note the wall-clock time.
2. **Then here.** Break at the relevant line, ask the same question, let Claude use `vs_evaluate` and the locals.

Record two numbers each time: **iterations to root cause** and **minutes elapsed**. Three or four bugs is enough to see the pattern — and if it isn't there for your kind of work, you'll know that just as clearly.

**What to expect if the tiering is right.** The rebuild-loop count should drop sharply on state-dependent bugs and barely move on everything else. That's the shape of the claim being made here: it's not that Claude gets smarter, it's that a specific expensive loop disappears.

---

## Related reading

| Doc | What it covers |
|---|---|
| [`DEBUGGER.md`](DEBUGGER.md) | Every debugger and ClrMD tool in detail |
| [`SEMANTIC.md`](SEMANTIC.md) | The semantic model, and why it beats grep |
| [`TESTING.md`](TESTING.md) | The discover → run → debug → catch loop |
| [`QOL.md`](QOL.md) | Terminal, notifications, attachments |
| [`VISION.md`](VISION.md) | Screen capture |
| [`TESTING-NEW-FEATURES.md`](TESTING-NEW-FEATURES.md) | Verifying each feature yourself |
