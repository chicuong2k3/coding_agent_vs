# Hướng dẫn test các tính năng mới (1.15.0 → 1.17.0)

Tài liệu này liệt kê từng tính năng vừa thêm và cách test thủ công từng cái một.

**Chuẩn bị chung:**
1. Cài bản `.vsix` mới nhất (Release trên GitHub, hoặc build local: `msbuild src/ClaudeCodeVS/ClaudeCodeVS.csproj /restore /t:Build /p:Configuration=Release`).
2. Mở một solution C# trong VS 2026 — dùng `demo/TestLab` (test tools) và `demo/RefMaze` (semantic tools) là tiện nhất.
3. **View > Other Windows > Claude Code** → **Launch Claude Code**, chờ pill **Connected**.
4. Các tool `vs_*` gọi qua Claude: cứ gõ yêu cầu tự nhiên (ví dụ bên dưới) hoặc bảo Claude "gọi tool X với tham số Y".
5. **Riêng mục profiling (mục 6):** cài 3 CLI một lần trước khi test — **mỗi cái một lệnh riêng** (`dotnet tool install` không nhận nhiều tên gói một lúc):
   ```powershell
   dotnet tool install -g dotnet-counters
   dotnet tool install -g dotnet-trace
   dotnet tool install -g dotnet-gcdump
   ```

---

## 1.16.0

### 1. Roslyn-precise diagnostic ranges (`getDiagnostics`)

**Là gì:** trước đây diagnostic chỉ có 1 điểm (line/column); giờ C#/VB có span đầy đủ start→end + mã lỗi (`CS0246`…), Claude anchor fix chính xác vào token lỗi.

**Test:**
1. Mở file C#, cố ý gây lỗi (ví dụ đổi `int x = 5;` thành `intt x = 5;`), đợi Error List hiện lỗi.
2. Hỏi Claude: *"Có lỗi compile nào không? Dùng getDiagnostics."*
3. **Đạt:** kết quả có `range.start` ≠ `range.end` (span phủ đúng `intt`), có `"code": "CS0246"`, `"source": "roslyn"`.
4. Với file C++ (nếu có): vẫn ra kết quả nhưng là point range, `"source": "Visual Studio"` — đúng thiết kế.

### 2. `vs_run_affected` — chạy test bị ảnh hưởng bởi thay đổi

**Là gì:** đưa file vừa sửa → Roslyn lần ngược caller graph tìm các test gọi (gián tiếp) vào code đó → chạy đúng các test ấy trong MỘT lượt Test Explorer.

**Test (dùng `demo/TestLab`):**
1. Mở solution TestLab.
2. Bảo Claude: *"Tôi vừa sửa file Score.cs (file mà các test gọi vào), chạy các test bị ảnh hưởng — dùng vs_run_affected."*
3. **Đạt:** kết quả có `affected.tests` = danh sách FQN kèm `callDistance` (số hop từ chỗ sửa tới test), và `run.tests` = outcome từng test.
4. Thử `listOnly=true`: chỉ ra danh sách, không chạy.
5. Đưa một file không test nào gọi tới: phải trả `count: 0` + note, không chạy gì.

---

## 1.17.0

### 3. Transitive callees (`vs_call_hierarchy`)

**Là gì:** trước chỉ ra callee trực tiếp (depth 1); giờ ra cả CÂY callee đệ quy (có chặn cycle + budget, callee ngoài solution là lá).

**Test (dùng `demo/RefMaze` hoặc bất kỳ chain A→B→C):**
1. Hỏi Claude: *"Hàm X gọi những gì, và các hàm đó gọi tiếp những gì? Dùng vs_call_hierarchy direction callees."*
2. **Đạt:** node callee có field `callees` lồng bên trong (cây, không phải danh sách phẳng); hàm đệ quy có `recursionElided: true`; kết quả có `maxDepth`.

### 4. `vs_rename` — rename ngữ nghĩa toàn solution

**Là gì:** rename qua Roslyn Renamer (bắt được interface/override/generic/alias mà grep-rename bỏ sót). Mặc định PREVIEW; `apply=true` mới commit — là MỘT undo unit trong VS (Ctrl+Z hoàn tác toàn bộ).

**Test (dùng `demo/RefMaze` — có interface + 3 implementation):**
1. Bảo Claude: *"Preview rename method Process trong IHandler thành Handle — dùng vs_rename."*
2. **Đạt (preview):** `applied: false`, danh sách `files` gồm CẢ file interface lẫn các file implementation (điều grep không làm được), mỗi file có `edits` + `sample` dòng.
3. Bảo tiếp: *"Apply đi."* → `applied: true`; mở các file xem tên đã đổi ở mọi nơi.
4. Nhấn **Ctrl+Z một lần** trong VS: toàn bộ rename hoàn tác (đây là điểm phải kiểm tra kỹ).
5. Thử newName không hợp lệ (`123abc`): phải bị từ chối.

### 5. Tracepoints — probe log-and-continue (`vs_set_tracepoint` / `vs_get_tracepoint` / `vs_remove_tracepoint`)

**Là gì:** đặt "điểm ghi log" tại file:line KHÔNG sửa code: mỗi lần chạy qua, evaluate các expression, ghi lại, rồi app tự chạy tiếp (gần như không dừng). Gated sau toggle **Allow Claude to drive debugger**.

**Test (dùng `demo/CheckoutBuggy` hoặc app nào có vòng lặp):**
1. Bật toggle **Allow Claude to drive debugger** trên panel.
2. F5 chạy app dưới debugger.
3. Bảo Claude: *"Đặt tracepoint tại CheckoutBuggy/Program.cs dòng N ghi lại giá trị `item.Price` và `total` — dùng vs_set_tracepoint."*
4. Cho app chạy qua dòng đó vài lần (thao tác trong app).
5. Bảo: *"Đọc tracepoint đi"* (`vs_get_tracepoint`). **Đạt:** `hits` = `[{seq, time, values{...}}]` nhiều bản ghi, app KHÔNG bị dừng ở breakpoint (chỉ khựng rất nhẹ).
6. `vs_remove_tracepoint` → breakpoint biến mất khỏi cửa sổ Breakpoints của VS.
7. Kiểm tra `maxHits`: đặt `maxHits: 3`, chạy qua 5 lần → chỉ ghi 3, `exhausted: true`, app không còn khựng.
8. Tắt toggle drive → cả 3 tool phải từ chối.

### 6. Profiling — `vs_perf_counters` / `vs_trace_cpu` / `vs_gc_dump`

**Là gì:** shell-out sang dotnet diagnostics CLIs đo process đang debug (hoặc PID bất kỳ): counters (CPU %, GC, alloc rate, threadpool), top hot methods (CPU sampling), top types chiếm heap.

**Chuẩn bị:** cài CLIs một lần — chạy **từng lệnh riêng**, không gộp nhiều tên gói vào một lệnh:
```powershell
dotnet tool install -g dotnet-counters
dotnet tool install -g dotnet-trace
dotnet tool install -g dotnet-gcdump
```

**Test:**
1. F5 một app .NET (app nào bận CPU càng rõ — `demo/SignalScan` hợp).
2. *"Đo counters của app đang debug 10 giây — vs_perf_counters."* **Đạt:** một object `counters` có `cpu-usage`, `working-set`, `alloc-rate`, `gc-heap-size`…
3. *"CPU đang tốn ở đâu? Sample 10 giây — vs_trace_cpu."* **Đạt:** `topMethods` là bảng top-N hàm nóng, kèm `traceFile` (mở được trong VS/PerfView).
4. *"Cái gì đang chiếm memory? — vs_gc_dump."* **Đạt:** `topTypes` là bảng type theo size, kèm `dumpFile`.
5. Gỡ CLI đi (hoặc test trên máy chưa cài): tool phải trả lỗi kèm hint `dotnet tool install -g …` — không crash.

### 7. Capture region crop (`vs_capture_window` / `vs_capture_screen`)

**Là gì:** thêm tham số `region: {x, y, width, height}` để crop ảnh chụp — màn hình dày đặc thì crop vùng cần nhìn, ảnh nét hơn và tốn ít token hơn.

**Test:**
1. Bật toggle **Allow screen capture**.
2. *"Chụp cửa sổ VS, crop vùng 800x600 ở góc trên trái — vs_capture_window target ide, region {x:0, y:0, width:800, height:600}."*
3. **Đạt:** kết quả `width: 800, height: 600`; mở PNG theo `path` thấy đúng vùng crop; chip attachment hiện trên panel.
4. Crop vượt biên (width khổng lồ): tự clamp về mép ảnh, không lỗi.

---

## 1.18.0

### 8. Per-frame source trong call stack (`vs_debug_state` / `vs_threads`)

**Là gì:** mỗi frame trong call stack giờ kèm file + line (trước chỉ có tên hàm; chỉ dòng đang dừng có vị trí).

**Test:**
1. F5 một app, dừng ở breakpoint sâu vài tầng gọi (ví dụ `demo/ComboScore`).
2. Hỏi Claude: *"Call stack hiện tại? — vs_debug_state."*
3. **Đạt:** các frame code-của-bạn có `file` + `line`; frame framework (không symbol) chỉ có tên — đúng thiết kế.
4. `vs_threads`: frame dạng chuỗi có hậu tố ` (đường\dẫn\file.cs:42)`.

### 9. Hunt idle-wait (`vs_hunt_flaky` với `measureRate`)

**Là gì:** giữa các lượt hunt, chờ engine thật sự idle (qua `IOperationState`) thay vì delay cứng — `measureRate` không còn đếm thiếu vì engine churn.

**Test (dùng `demo/TestLab`, test intermittent ~1-in-3):**
1. *"Hunt test X 12 lượt, đo tỉ lệ fail — vs_hunt_flaky measureRate:true."*
2. **Đạt:** `executed` đạt đủ 12 (trước đây hay hụt do inconclusive), tỉ lệ fail ~1/3. So sánh cảm quan với trước là đủ.

### 10. `vs_run_test profile:true` (experimental)

**Là gì:** chạy test dưới profiler với GUID tool CPU Usage của Diagnostics Hub (dò bằng reflection, override được bằng `profilerToolId`).

**Test:**
1. *"Chạy test X dưới profiler — vs_run_test profile:true."*
2. **Đạt:** run KHÔNG còn trả `Status=Cancelled` + note cũ; có kết quả per-test (và có thể một file .diagsession trong attachments).
3. **Nếu vẫn Cancelled:** GUID/property không khớp bản VS — đây là feature experimental; báo lại kết quả để gỡ tiếp (thử `profilerToolId` khác).

### 11. Panel usage breakdown

**Là gì:** dòng mới trong stats card: chia token subagent (CHÍNH XÁC, từ usage records sidechain) + top tool ngốn context (ƯỚC LƯỢNG, size tool_result / 4).

**Test:**
1. Chạy một phiên có dùng tool nhiều (bảo Claude đọc vài file, gọi vs_debug_state…) và một tác vụ có subagent (bảo Claude "dùng agent tìm X").
2. Sau mỗi lượt trả lời, nhìn stats card trên panel.
3. **Đạt:** xuất hiện dòng mờ dạng `Subagents (exact): 5 calls · ↑ 12k ↓ 3k      Top tools (est.): Read ≈40k · vs_debug_state ≈12k`. Không dùng subagent/tool thì dòng ẩn.

---

## 1.15.0 (nền tảng — không có UI mới để test riêng)

- **Multi-agent `AgentProfile`**: toàn bộ điểm phụ thuộc Claude (lockfile dir, binary, env vars, auth header, đường config, cờ hooks/MCP) gom về `src/ClaudeCodeVS.Protocol/AgentProfile.cs`. Hành vi Claude Code giữ nguyên — test = mọi thứ cũ vẫn chạy. Chi tiết: `docs/MULTI-AGENT.md`.
- **CI/CD**: mỗi push/PR build `.vsix` (artifact `ClaudeCodeVS-vsix` trong tab Actions); push tag `v*` → GitHub Release tự đính `.vsix`. Test = xem tab Actions xanh + Release có file.

## Ghi chú đã biết (chưa phải bug)

- `vs_rename` chỉ C#/VB, symbol phải có source trong solution (không rename được symbol metadata).
- Tracepoint hit khi model đang `vs_continue` chờ break: lần break thoáng qua của tracepoint có thể resolve lượt chờ đó — hạn chế đã biết của mô phỏng log-and-continue.
- `vs_trace_cpu`/`vs_gc_dump` giữ lại file trace/dump trong `%TEMP%` để mở bằng VS/PerfView — xoá tay nếu cần.
- Toàn bộ tính năng 1.16/1.17 mới build-verified, chưa live-verified trong VS — tài liệu này chính là checklist để verify.
