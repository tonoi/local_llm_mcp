# LocalFastAgent

LocalFastAgent is a thin C#/.NET MCP stdio server intended to run in a Hyper-V isolated Windows container and delegate first-pass log, code-map, and diff analysis to an embedded, local, edge, or dedicated OpenAI-compatible API.

## Tools

- `local_summarize_logs`: summarizes build, test, and runtime logs without returning the full log.
- `local_code_map`: narrows broad repository exploration to likely files and symbols.
- `local_review_diff`: performs a first-pass review focused on practical risks.

LocalFastAgent is read-only by design. It validates workspace paths, avoids known secret files, masks likely secrets, writes MCP JSON-RPC messages only to stdout, and writes operational logs to stderr.

## Environment variables

| Variable | Default | Purpose |
| --- | --- | --- |
| `WORKSPACE_ROOT` | current directory | Read-only repository root inside the container. |
| `LOCAL_OPENAI_BASE_URL` | `http://host.docker.internal:8080/v1` | OpenAI-compatible API base URL. |
| `LOCAL_OPENAI_API_KEY` | empty | Optional Bearer token. |
| `LOCAL_LLM_MODEL` | `local-coder` | Chat completions model name. |
| `LOCAL_REQUEST_TIMEOUT_SECONDS` | `120` | Request timeout. |
| `MAX_LOG_BYTES` | `512000` | Maximum log bytes read. |
| `MAX_DIFF_BYTES` | `512000` | Maximum diff bytes read. |
| `MAX_FILE_BYTES` | `256000` | Maximum single file size for code-map snippets. |
| `MAX_PROMPT_CHARS` | `60000` | Maximum prompt content sent to the local LLM. |
| `MAX_CODE_MAP_FILES` | `200` | Maximum files considered by `local_code_map`. |

## Build on Windows

```powershell
cd C:\Tools\LocalFastAgent\src\LocalFastAgent

dotnet publish .\LocalFastAgent.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  /p:PublishSingleFile=true `
  /p:PublishTrimmed=false `
  -o C:\Tools\LocalFastAgent\bin
```

## Docker image

```powershell
cd C:\Tools\LocalFastAgent

docker build `
  -t localfastagent:0.1 `
  -f Dockerfile .
```

## AI agent MCP configuration

Add the server to your MCP client configuration file. The same tools can be used from general AI agents, Vibe Coding workflows, Deep Research workflows, and other MCP-capable clients:

```toml
[mcp_servers.localfastagent]
command = "powershell.exe"
args = [
  "-NoProfile",
  "-ExecutionPolicy", "Bypass",
  "-File", "C:\\Tools\\LocalFastAgent\\docker-run-localfastagent.ps1"
]
enabled = true
required = false
startup_timeout_sec = 30
tool_timeout_sec = 180
default_tools_approval_mode = "prompt"
enabled_tools = ["local_summarize_logs", "local_code_map", "local_review_diff"]
```

Keep `LOCAL_OPENAI_API_KEY` in a user environment variable or Windows Credential Manager; do not store it in `config.toml`.
