# Checklist test TOÀN BỘ tính năng (đến 1.18.0)

Từng tính năng một — làm gì, prompt mẫu gõ cho Claude, và tiêu chí **Đạt**. Tính năng mới (1.16→1.18) đánh dấu 🆕.

**Chuẩn bị chung:**
1. Cài `.vsix` mới nhất ([Releases](https://github.com/chicuong2k3/coding_agent_vs/releases)) hoặc build local: `msbuild src/ClaudeCodeVS/ClaudeCodeVS.csproj /restore /t:Build /p:Configuration=Release`.
2. Mở solution demo phù hợp từng mục (ghi trong từng mục): `demo/TestLab` (test), `demo/RefMaze` (semantic), `demo/CheckoutBuggy`/`ComboScore` (debug cơ bản), `demo/LockJam` (deadlock), `demo/WebQuote` (attach/web), `demo/AsyncTrace` (async), `demo/SignalScan` (CPU).
3. **View > Other Windows > Claude Code** → **Launch Claude Code** → pill xanh **Connected**.
4. Tool `vs_*` gọi qua Claude bằng yêu cầu tự nhiên, hoặc nói thẳng "gọi tool X với tham số Y".
5. Test profiling (mục H) cần cài 3 CLI một lần — **mỗi cái một lệnh riêng**:
   ```powershell
   dotnet tool install -g dotnet-counters
   dotnet tool install -g dotnet-trace
   dotnet tool install -g dotnet-gcdump
   ```

---

## A. Lõi bridge + diff (trải nghiệm chính)

### A1. Launch + auto-connect (terminal docked)
Bấm **Launch Claude Code**. **Đạt:** `claude` mở trong tab Terminal docked của VS (không phải cửa sổ ngoài), pill panel chuyển xanh **Connected** mà KHÔNG cần gõ `/ide`.

### A2. External console
Bấm **External console**. **Đạt:** `claude` mở trong cửa sổ cmd riêng, vẫn tự connect; đóng VS thì cửa sổ này sống tiếp.

### A3. Diff Accept
Bảo Claude: *"Thêm comment vào đầu file Program.cs."* **Đạt:** diff mở NATIVE trong VS, bấm **Accept** → file được ghi, feed panel ghi nhận ✓.

### A4. Diff Reject
Như A3 nhưng bấm **Reject**. **Đạt:** file KHÔNG đổi, Claude nhận biết bị từ chối.

### A5. Reject với lý do
Bấm **Reject with feedback**, gõ lý do (vd "đặt tên biến khác"). **Đạt:** Claude đọc được lý do và sửa lại theo.

### A6. Auto-accept (run wild)
Bật toggle **Auto-accept**. Bảo Claude sửa file. **Đạt:** edit áp thẳng, KHÔNG mở diff; tắt toggle thì diff quay lại. Restart VS → toggle tự về OFF.

### A7. Bỏ qua gate cho file scratch của Claude (1.14.4)
Bảo Claude ghi memory / tạo file tạm (vd "lưu điều này vào memory của bạn"). **Đạt:** KHÔNG mở diff (feed có dòng skip); nhưng mọi file trong workspace vẫn mở diff bình thường.

### A8. Selection context
Bôi đen vài dòng code rồi hỏi: *"Đoạn tôi đang chọn làm gì?"* **Đạt:** Claude trả lời đúng đoạn đó mà bạn không cần dán code.

### A9. getDiagnostics — 🆕 span chính xác (1.16)
Gây lỗi compile C# (vd `intt x = 5;`), đợi Error List hiện. Hỏi: *"Có lỗi compile nào không?"* **Đạt:** kết quả có `range.start` ≠ `range.end` phủ đúng token lỗi, `"code": "CS0246"`, `"source": "roslyn"`. File C++ (nếu có): vẫn ra nhưng point-range — đúng thiết kế.

### A10. Panel
Nhìn panel trong khi làm các mục trên. **Đạt:** pill status đúng trạng thái; feed có dòng curated; counters Edits ✓/✗ và Debugger inspected/driven tăng đúng; card token có Latest/Session; nút **≈ Show est. cost** hiện cost; strip "Awaiting your review" hiện khi diff đang chờ.

### A11. 🆕 Dòng usage breakdown (1.18)
Chạy một lượt dùng nhiều tool (bảo Claude đọc vài file, gọi vs_debug_state) và một lượt có subagent (*"dùng agent tìm giúp tôi X"*). **Đạt:** dưới dòng Session xuất hiện dòng mờ `Subagents (exact): N calls · ↑… ↓…   Top tools (est.): Read ≈40k · …`; không dùng thì ẩn.

### A12. Notifications
Để VS xuống background (mở app khác), bảo Claude làm việc gì đó dài. **Đạt:** khi xong lượt → InfoBar "Claude finished" + taskbar nháy; khi Claude cần input (permission prompt trong terminal) → notification "needs your input". Tắt toggle **Notify** → im lặng.

### A13. Attachments tray
Chụp màn hình (Win+Shift+S) → bấm **Paste** trên panel; kéo-thả 1 file từ Explorer. **Đạt:** chip hiện trên tray kèm ước lượng token; chip `@` tự chèn vào ô nhập của CLI (chỉ chèn, không gửi); Claude đọc được ảnh thật; click chip = re-mention; ✕ = gỡ. File xlsx/zip vẫn attach được (nhãn 🧰). Bản staged nằm trong `.claude/attachments/` có gitignore.

### A14. Reconnect hardening
Đang có diff chờ → đóng hẳn terminal `claude`. **Đạt:** diff mồ côi tự reject + đóng. Mở lại `claude` → tự reconnect với đúng bridge (không cần restart VS).

---

## B. Debugger — ĐỌC (vs-debug, không cần bật toggle)

Fixture: `demo/CheckoutBuggy` hoặc `demo/ComboScore`, F5 và dừng ở breakpoint.

### B1. Push context khi break
Đang dừng ở breakpoint, gõ prompt bất kỳ cho Claude. **Đạt:** Claude tự biết đang dừng ở đâu (file/line/stack/locals) mà bạn không dán gì — context được bơm lúc submit.

### B2. vs_debug_state — 🆕 per-frame file/line (1.18)
*"Trạng thái debug hiện tại?"* **Đạt:** mode/stoppedAt/callStack/locals; **từng frame code-của-bạn có `file` + `line`** (frame framework name-only là đúng).

### B3. vs_evaluate / vs_expand / vs_get_frame_locals
*"Giá trị của biến X? Mở rộng object Y. Locals của frame 2?"* **Đạt:** giá trị đúng; expand ra property con; `threadId` khác đọc được locals thread khác.

### B4. vs_threads — 🆕 hậu tố (file:line) (1.18)
*"Liệt kê threads."* **Đạt:** mỗi thread có stack; frame có ` (path\file.cs:42)` khi có symbol; thread chờ lock có cờ + `lockOwnerThreadId` (thấy rõ với LockJam).

### B5. vs_exception
Break tại một throw (hoặc `$exception` trong catch). *"Exception hiện tại là gì?"* **Đạt:** type + message + stack của exception.

### B6. vs_list_breakpoints / vs_list_processes
**Đạt:** danh sách breakpoint khớp cửa sổ Breakpoints; list processes ra các process attach được.

### B7. Bộ ClrMD (cần app .NET đang chạy dưới debugger)
- `vs_wait_chains` (LockJam): chuỗi sở hữu monitor + deadlock suspects.
- `vs_async_stacks` (AsyncTrace): stack async logical.
- `vs_heap_stats`: thành phần heap + GC health.
- `vs_threadpool`: số thread + backlog + cảnh báo starvation.
- `vs_gc_roots <type>`: đường retention (vì sao object còn sống).
- `vs_heap_diff`: baseline → thao tác → diff ra type tăng (tìm leak).
**Đạt:** mỗi tool trả JSON cấu trúc, không lỗi; kết quả lớn có `truncated:true`.

---

## C. Debugger — LÁI (bật toggle "Allow Claude to drive debugger")

### C1. Gate
Toggle OFF → gọi tool drive bất kỳ. **Đạt:** từ chối rõ ràng kèm hint bật toggle. Restart VS → toggle tự OFF.

### C2. continue / step over / into / out / run_to_line
*"Step over 2 lần rồi cho biết locals thay đổi gì."* **Đạt:** mỗi lệnh trả về vị trí break MỚI (chờ được break kế tiếp, không treo UI).

### C3. vs_break_all (LockJam)
App đang treo deadlock (không breakpoint nào hit). *"App treo rồi, pause nó xem sao."* **Đạt:** app dừng giữa chừng, sau đó vs_threads/vs_wait_chains lần ra cycle.

### C4. set/remove breakpoint
Theo file:line VÀ theo tên hàm; thử kèm `condition`, `hitCount`. **Đạt:** breakpoint hiện trong cửa sổ Breakpoints, hit đúng điều kiện.

### C5. vs_break_on_thrown (WebQuote)
*"Break ngay tại chỗ ném FooException."* **Đạt:** dừng ở THROW site (first-chance), không phải ở catch.

### C6. freeze_thread / set_next_statement
**Đạt:** thread đóng băng không chạy tiếp; set-next dời con trỏ thực thi (vàng) sang dòng chỉ định.

### C7. start/stop_debugging + attach/detach (WebQuote)
*"Attach vào process WebQuote đang chạy."* **Đạt:** attach không cần F5; detach xong app vẫn chạy; start = F5 tới break đầu; stop = Shift+F5.

### C8. 🆕 Tracepoints (1.17)
App chạy dưới debugger có vòng lặp. *"Đặt tracepoint tại file:line ghi `item.Price` và `total`, maxHits 5."* **Đạt:** app KHÔNG dừng hẳn (chỉ khựng nhẹ); `vs_get_tracepoint` ra timeline `[{seq,time,values}]`; đủ 5 hit → `exhausted:true`, hết khựng; `vs_remove_tracepoint` xoá breakpoint khỏi VS.

---

## D. Data breakpoints (Concord; x64; bật toggle drive)

### D1. vs_set_data_breakpoint
Đang break, watch một INSTANCE field: *"Watch owner.field, báo mỗi lần nó đổi."* **Đạt:** chạy tiếp → mỗi mutation ghi vào timeline; `stopOnChange:true` → VS dừng ngay sau statement ghi; `vs_get_data_changes` ra `[{previous,current,type}]`; `vs_remove_data_breakpoint` disarm. Static/local/struct field → từ chối có giải thích (đúng thiết kế).

---

## E. Semantic (vs-semantic; cần solution C#/VB load xong; fixture `demo/RefMaze`)

### E1. vs_search_symbols
*"Tìm symbol tên Process."* **Đạt:** danh sách candidates, mỗi cái có `symbolId` (dạng `M:Ns.Type.Method(...)`).

### E2. vs_find_references
*"Ai dùng IHandler.Process?"* **Đạt:** ra CẢ các call qua interface-dispatch + explicit implementation (grep miss các case này), kèm file:line:column + snippet.

### E3. vs_go_to_definition
Hỏi theo `file`+`line` giữa một overload set. **Đạt:** ra ĐÚNG MỘT overload đang được gọi, không phải cả cụm.

### E4. vs_find_implementations
*"Những class nào implement IHandler?"* **Đạt:** đủ 3 impl của RefMaze, gồm cả explicit implementation.

### E5. vs_call_hierarchy — 🆕 callees transitive (1.17)
Callers: *"Ai gọi (gián tiếp) tới X?"* → cây callers nhiều tầng, đệ quy có `recursionElided`. Callees: *"X gọi những gì, và tiếp nữa?"* → **cây callee lồng nhau** (không còn phẳng depth-1), có `maxDepth`.

### E6. vs_type_hierarchy
**Đạt:** base chain + interfaces, và derived types.

### E7. vs_get_selection
Bôi đen một identifier → *"Tôi đang chọn gì? Navigate từ nó."* **Đạt:** trả text chọn + `symbolId` tại vị trí đó.

### E8. vs_decompile
*"Decompile System.Linq.Enumerable.Where."* **Đạt:** ra body C# của member (không phải cả file khổng lồ); type BCL stub (String…) → tự retry SourceLink ra source THẬT, field `source: decompiled|source`, `bodyAvailable` đúng.

### E9. 🆕 vs_rename (1.17)
*"Preview rename IHandler.Process → Handle."* **Đạt (preview):** `applied:false`, danh sách files gồm CẢ interface + 3 impl, mỗi file có `edits`+`sample`. *"Apply đi"* → `applied:true`, tên đổi mọi nơi. **Ctrl+Z MỘT lần → hoàn tác toàn bộ** (điểm phải test kỹ). `newName` bậy (`123abc`) → từ chối.

---

## F. Test integration (vs-debug; fixture `demo/TestLab`)

### F1. vs_list_tests
*"Solution có những test nào?"* **Đạt:** danh sách FQN thật (Roslyn scan attribute), không cần build trước.

### F2. vs_run_test
*"Chạy test X."* **Đạt:** tự build; per-test `{outcome, errorMessage, errorStackTrace, durationMs}` — pass/fail phân biệt THẬT (không phải Success=run-completed). `collectCoverage:true` → có file .coverage trong attachments.

### F3. 🆕 vs_run_test profile:true (1.18, EXPERIMENTAL)
**Đạt:** không còn `Status=Cancelled` + note cũ; có kết quả per-test (có thể kèm .diagsession). **Nếu vẫn Cancelled** → GUID/property không khớp bản VS này; báo lại để dò tiếp (thử `profilerToolId` khác). Fail mục này không chặn các mục khác.

### F4. 🆕 vs_run_affected (1.16)
*"Tôi vừa sửa Score.cs, chạy các test bị ảnh hưởng."* **Đạt:** `affected.tests` = FQN kèm `callDistance`; `run.tests` = outcome; `listOnly:true` chỉ liệt kê; file không test nào gọi tới → `count:0` + note.

### F5. vs_rerun_failed
Chạy cả bộ có fail → *"Chạy lại chỉ những test fail."* **Đạt:** chỉ các test fail lượt trước chạy lại.

### F6. vs_debug_test
*"Debug test X, dừng ở chỗ fail."* **Đạt:** test chạy dưới debugger, dừng tại điểm ném/assert (phối hợp break-on-thrown).

### F7. vs_hunt_flaky — 🆕 idle-wait (1.18)
Test intermittent ~1-in-3: *"Hunt test X 12 lượt, đo tỉ lệ — measureRate:true."* **Đạt:** trả `huntId` trong ≤40s; `vs_hunt_result` poll tới đủ **12/12 executed** (trước 1.18 hay hụt), tỉ lệ ~1/3; `vs_hunt_cancel` dừng được.

### F8. vs_catch_flaky
*"Bắt tận tay test flaky X."* (cần toggle drive). **Đạt:** loop dưới debugger tới lượt fail thì DỪNG NGAY TẠI THROW, exception sống trong frame để mổ xẻ.

---

## G. Screen capture (bật toggle "Allow screen capture")

### G1. Gate
Toggle OFF → gọi capture. **Đạt:** từ chối kèm hint.

### G2. vs_capture_window
`target:debuggee` (app có UI đang debug), `target:ide`, `target:window title:"Edge"`. **Đạt:** PNG staged thành chip trên tray + trả `path` (Claude Read path đó thấy ảnh THẬT); window minimized → lỗi bảo restore; title không khớp → lỗi kèm `visibleWindows`.

### G3. vs_capture_screen
`monitor:0` / `all:true`. **Đạt:** đúng màn hình yêu cầu.

### G4. 🆕 Region crop (1.17)
`region {x:0,y:0,width:800,height:600}` trên cả hai tool. **Đạt:** kết quả `width:800,height:600`, PNG đúng vùng; crop vượt biên tự clamp không lỗi.

---

## H. Profiling (🆕 1.17; cần 3 CLI ở mục Chuẩn bị; fixture `demo/SignalScan` chạy F5)

### H1. vs_perf_counters
*"Đo counters app đang debug 10 giây."* **Đạt:** object `counters` có `cpu-usage`, `working-set`, `alloc-rate`, `gc-heap-size`…

### H2. vs_trace_cpu
*"CPU tốn ở đâu? Sample 10 giây."* **Đạt:** `topMethods` = bảng top-N hàm nóng + `traceFile` (mở được PerfView/VS).

### H3. vs_gc_dump
*"Cái gì chiếm memory?"* **Đạt:** `topTypes` theo size + `dumpFile`.

### H4. Thiếu CLI
Máy chưa cài (hoặc gỡ tạm) → **Đạt:** lỗi kèm hint `dotnet tool install -g …`, không crash.

---

## I. Hạ tầng (không có UI riêng)

### I1. Multi-agent AgentProfile (1.15)
Không có gì mới để bấm — **Đạt** = toàn bộ A→H chạy như cũ (hành vi Claude Code giữ nguyên sau khi tham số hoá). Chi tiết: `docs/MULTI-AGENT.md`.

### I2. CI/CD (1.15)
**Đạt:** tab Actions — mỗi push/PR có workflow **Build** xanh kèm artifact `ClaudeCodeVS-vsix`; mỗi tag `v*` có Release kèm `ClaudeCodeVS.vsix` (v1.15.0→v1.18.0 đều đã có).

---

## Ghi chú đã biết (không phải bug)

- Prompt terminal của CLI vẫn hỏi song song với diff (redundant second gate) — giới hạn kiến trúc đã ghi trong ROADMAP Phase 4.
- `vs_rename` chỉ C#/VB, symbol phải có source trong solution.
- Tracepoint hit trong lúc đang `vs_continue` chờ break có thể resolve lượt chờ đó — giới hạn của mô phỏng log-and-continue.
- `vs_trace_cpu`/`vs_gc_dump` giữ file trace/dump trong `%TEMP%` — xoá tay nếu cần.
- Reference `at_mentioned` gửi giữa lượt hoặc khi agents view đang focus có thể bị CLI drop — click lại chip để re-mention.
- F3 (profile:true) là experimental — fail thì báo, không chặn merge/test khác.
- Các mục 1.16→1.18 mới build-verified; checklist này chính là bước live-verify.
