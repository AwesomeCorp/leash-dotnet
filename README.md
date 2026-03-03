# Leash

A local service that monitors every tool call [Claude Code](https://docs.anthropic.com/en/docs/claude-code) and [GitHub Copilot](https://docs.github.com/en/copilot) make, scores them for safety using an LLM, and gives you a real-time dashboard to see exactly what's happening. Keep Claude and Copilot on a leash. Optionally, it can auto-approve or deny requests based on the safety score.

## Why

Claude Code and Copilot ask for permission before running commands, reading files, or making edits. When you're deep in a task, clicking "allow" dozens of times breaks your flow. But blindly auto-approving everything is risky.

Leash sits in the middle: it intercepts every permission request, runs it through a safety analysis, and either logs it silently (observe mode) or makes the approve/deny decision for you (enforce mode). Either way, you get full visibility into what your AI coding assistant is doing.

## How It Works

```
Claude Code / Copilot  -->  curl hook  -->  Leash  -->  LLM safety analysis  -->  decision
```

Hooks are lightweight `curl` commands injected into the AI tool's settings file (`~/.claude/settings.json` for Claude, `.github/hooks/hooks.json` for Copilot). When a tool call is triggered, the hook sends the request to the local service. The service scores it 0-100, categorizes it (safe/cautious/risky/dangerous), and either:

- **Observe mode** (default): Logs everything, returns no opinion. Claude asks you as normal.
- **Approve-only mode**: Auto-approves safe requests, falls through to you on anything uncertain. Never denies.
- **Enforce mode**: Returns approve/deny based on the safety score vs threshold.

Zero external dependencies beyond .NET and `curl`. No Python, no npm, no Docker.

## One-Line Install

**Windows (PowerShell):**
```powershell
git clone https://github.com/awesomecorp/leash.git && cd leash && dotnet run --project src\Leash.Api
```

**macOS / Linux:**
```bash
git clone https://github.com/awesomecorp/leash.git && cd leash && dotnet run --project src/Leash.Api
```

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download) and `curl`. That's it.

## Quick Start

On startup the service:

1. Installs hooks into Claude's `~/.claude/settings.json` automatically (Copilot hooks via dashboard)
2. Starts at **http://localhost:5050** and opens the dashboard
3. Prints an in-place status line showing live event counts and latency
4. Optionally shows system tray notifications for uncertain decisions (enable with `tray.enabled` in config)
5. On `Ctrl+C`, removes all installed hooks cleanly

Use `--no-hooks` to skip hook installation, or `--enforce` to start in enforcement mode.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Claude CLI](https://docs.anthropic.com/en/docs/claude-code) and/or [GitHub Copilot CLI](https://docs.github.com/en/copilot) installed and authenticated
- `curl` (ships with Windows 10+, macOS, and Linux)

## Dashboard

The web UI at `http://localhost:5050` has 8 pages:

| Page | What it shows |
|------|---------------|
| **Dashboard** | Live stats, safety score charts, permission profiles, quick actions, hook controls |
| **Live Logs** | Every hook event with multi-select filter chips, LLM reasoning, response JSON, export to CSV/JSON |
| **Sessions** | Per-session timelines with event breakdowns and filter chips |
| **Transcripts** | Browse Claude and Copilot session transcripts grouped by project folder, with SSE live streaming and markdown rendering |
| **Prompt Editor** | Edit the LLM prompt templates that drive safety analysis |
| **Configuration** | Hook handler management: matchers, modes, thresholds, prompt templates |
| **Claude Settings** | Direct JSON editor for `~/.claude/settings.json` |
| **Copilot Settings** | Direct JSON editor for `~/.copilot/hooks/hooks.json` |

## Configuration

Config lives at `~/.leash/config.json` (auto-created on first run).

```json
{
  "llm": {
    "provider": "claude-cli",
    "model": "sonnet",
    "timeout": 30000,
    "persistentProcess": true
  },
  "server": { "port": 5050, "host": "localhost" },
  "enforcementMode": "observe",
  "hookHandlers": {
    "PermissionRequest": {
      "enabled": true,
      "handlers": [{
        "name": "bash-analyzer",
        "matcher": "Bash",
        "mode": "llm-analysis",
        "promptTemplate": "bash-prompt.txt",
        "threshold": 95,
        "autoApprove": true
      }]
    }
  },
  "tray": { "enabled": false }
}
```

**Key settings:**

| Setting | What it does |
|---------|-------------|
| `enforcementMode` | `"observe"` (log only), `"approve-only"` (auto-approve safe, never deny), `"enforce"` (auto-approve/deny) |
| `hookHandlers` | Which tools get analyzed, with what prompt, at what threshold |
| `profiles.activeProfile` | `permissive` / `moderate` / `strict` / `lockdown` |
| `llm.persistentProcess` | Keep a single Claude subprocess alive for faster responses |
| `tray.enabled` | Show system tray notifications for uncertain decisions |

## CLI Flags

```bash
dotnet run --project src/Leash.Api             # Start with hooks, observe mode
dotnet run --project src/Leash.Api -- --enforce # Start with enforcement enabled
dotnet run --project src/Leash.Api -- --no-hooks # Start without installing hooks
```

## How Hooks Work

For Claude, the service writes `curl` commands into `~/.claude/settings.json` tagged with a `# leash` marker. For Copilot, equivalent hooks go into `.github/hooks/hooks.json`.

```json
{
  "hooks": {
    "PreToolUse": [{
      "matcher": "Bash",
      "hooks": [{
        "type": "command",
        "command": "curl -sS -X POST \"http://localhost:5050/api/hooks/claude?event=PreToolUse\" -H \"Content-Type: application/json\" -d @- # leash"
      }]
    }]
  }
}
```

On shutdown, only hooks with the `# leash` marker are removed. Your own hooks are never touched.

## Security

- Localhost-only binding with CORS restrictions
- Security headers (CSP, X-Frame-Options, no-cache)
- Rate limiting (600 req/min per IP)
- Optional API key authentication
- Input sanitization and path traversal protection
- Any hook error returns `{}` (no opinion) so Claude falls through to normal behavior

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Backend | C# .NET 10, ASP.NET Core |
| Hook Transport | `curl` |
| Frontend | Vanilla HTML/CSS/JS (no CDN dependencies) |
| LLM | Claude CLI, Copilot CLI, Anthropic API, or generic REST |
| Storage | JSON files + MemoryCache |
| Tests | xUnit + Moq (154 tests) |

## Development

```bash
dotnet build    # Build
dotnet test     # Run 154 tests
```

### Building & Running by OS

**Windows** (PowerShell or cmd):
```powershell
dotnet build
dotnet run --project src\Leash.Api
```
Windows gets native system tray notifications via `NotifyIcon`. `curl` ships with Windows 10+.

**macOS**:
```bash
dotnet build
dotnet run --project src/Leash.Api
```
Uses `osascript` for notification dialogs. Requires [.NET 10 SDK for macOS](https://dotnet.microsoft.com/download) and `curl` (pre-installed).

**Linux**:
```bash
dotnet build
dotnet run --project src/Leash.Api
```
Uses `notify-send` (libnotify) for notifications and `zenity` for interactive dialogs. Install with `sudo apt install libnotify-bin zenity` (Debian/Ubuntu) or equivalent.

## Releases

CI builds self-contained binaries for Windows x64, Linux x64, macOS x64, and macOS ARM64. See the [Releases](https://github.com/awesomecorp/leash/releases) page.
