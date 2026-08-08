// Regression test for the agent stubs' bridge discovery + heartbeat + diff gate.
// Simulates the real contract: a lockfile named <port>.lock (filename IS the port, no port field
// in the JSON) under %USERPROFILE%\.claude\ide, and a bridge answering /agent-heartbeat and
// /permission with the auth token. Run from an EMPTY scratch dir with USERPROFILE faked so the
// real ~\.claude\ide is never touched:
//   $env:USERPROFILE = "$env:TEMP\stub-test-home"; mkdir $env:USERPROFILE -Force
//   node test-stub.mjs "file:///<repo>/docs/agents/opencode-vs-diff-gate.js"
// (Tests the .js stub; the .ts stub mirrors the same findBridge logic - keep them in sync.)
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { homedir } from "node:os";
import http from "node:http";
import assert from "node:assert";

const TOKEN = "test-token-123";
const hits = { heartbeat: 0, permission: 0 };

// 1) Tiny bridge on an OS-assigned port.
const server = http.createServer((req, res) => {
  if (req.headers["x-claude-code-ide-authorization"] !== TOKEN) {
    res.statusCode = 401; res.end(); return;
  }
  let body = "";
  req.on("data", (c) => (body += c));
  req.on("end", () => {
    if (req.url === "/agent-heartbeat") {
      hits.heartbeat++;
      assert.strictEqual(JSON.parse(body).agent, "OpenCode");
      res.end();
    } else if (req.url === "/permission") {
      hits.permission++;
      res.setHeader("content-type", "application/json");
      res.end(JSON.stringify({ allow: false, reason: "rejected in VS diff (test)" }));
    } else if (req.url === "/debug-context") {
      res.setHeader("content-type", "application/json");
      res.end(JSON.stringify({ mode: "design" }));
    } else { res.statusCode = 404; res.end(); }
  });
});
await new Promise((r) => server.listen(0, "127.0.0.1", r));
const port = server.address().port;

// 2) Real-contract lockfile: filename == port, JSON has ideName/authToken but NO port field.
const ideDir = join(homedir(), ".claude", "ide");
mkdirSync(ideDir, { recursive: true });
writeFileSync(join(ideDir, `${port}.lock`), JSON.stringify({
  pid: process.pid, ideName: "Visual Studio", transport: "ws",
  runningInWindows: true, authToken: TOKEN, workspaceFolders: [process.cwd()],
}));
// Decoys the filter must skip: another IDE's lockfile, and a dead-port VS lockfile.
writeFileSync(join(ideDir, "1.lock"), JSON.stringify({ ideName: "JetBrains", authToken: "x" }));
writeFileSync(join(ideDir, "2.lock"), JSON.stringify({ ideName: "Visual Studio", authToken: "x", workspaceFolders: ["Z:/nowhere"] }));

// 3) Load the REAL stub and run its factory.
const { VsDiffGate } = await import(process.argv[2]);
const hooks = await VsDiffGate();

// 4) Heartbeat must have fired once at load.
await new Promise((r) => setTimeout(r, 300));
assert.strictEqual(hits.heartbeat, 1, "expected one heartbeat at plugin load");

// 5) The diff gate must consult /permission and veto on deny (throw).
writeFileSync(join(process.cwd(), "gate-me.txt"), "old\n");
let vetoed = null;
try {
  await hooks["tool.execute.before"](
    { tool: "edit" },
    { args: { filePath: join(process.cwd(), "gate-me.txt"), oldString: "old", newString: "new" } },
  );
} catch (e) { vetoed = e.message; }
assert.strictEqual(hits.permission, 1, "expected one /permission POST");
assert.match(vetoed ?? "", /rejected in VS diff/, "deny must veto via throw");

console.log(`PASS: bridge discovered on :${port}, heartbeat=${hits.heartbeat}, permission gate vetoed OK`);
process.exit(0);
