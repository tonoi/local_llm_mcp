$ErrorActionPreference = "Stop"

$JetsonBaseUrl = if ($env:JETSON_OPENAI_BASE_URL) { $env:JETSON_OPENAI_BASE_URL } else { "http://192.168.10.60:8080/v1" }
$LocalModel = if ($env:LOCAL_LLM_MODEL) { $env:LOCAL_LLM_MODEL } else { "local-coder" }
$HostRepo = if ($env:LOCALFASTAGENT_HOST_REPO) { $env:LOCALFASTAGENT_HOST_REPO } else { "C:\src\target-repo" }

$dockerArgs = @(
  "run", "--rm", "-i",
  "--isolation=hyperv",
  "--name", "codex-localfastagent",
  "-e", "JETSON_OPENAI_BASE_URL=$JetsonBaseUrl",
  "-e", "JETSON_OPENAI_API_KEY=$env:JETSON_OPENAI_API_KEY",
  "-e", "LOCAL_LLM_MODEL=$LocalModel",
  "-e", "WORKSPACE_ROOT=C:\workspace",
  "-v", "${HostRepo}:C:\workspace:ro",
  "localfastagent:0.1"
)

& docker @dockerArgs
