$ErrorActionPreference = "Stop"

$LocalBaseUrl = if ($env:LOCAL_OPENAI_BASE_URL) { $env:LOCAL_OPENAI_BASE_URL } else { "http://host.docker.internal:8080/v1" }
$LocalModel = if ($env:LOCAL_LLM_MODEL) { $env:LOCAL_LLM_MODEL } else { "local-coder" }
$HostRepo = if ($env:LOCALFASTAGENT_HOST_REPO) { $env:LOCALFASTAGENT_HOST_REPO } else { "C:\src\target-repo" }

$dockerArgs = @(
  "run", "--rm", "-i",
  "--isolation=hyperv",
  "--name", "aiagent-localfastagent",
  "-e", "LOCAL_OPENAI_BASE_URL=$LocalBaseUrl",
  "-e", "LOCAL_OPENAI_API_KEY=$env:LOCAL_OPENAI_API_KEY",
  "-e", "LOCAL_LLM_MODEL=$LocalModel",
  "-e", "WORKSPACE_ROOT=C:\workspace",
  "-v", "${HostRepo}:C:\workspace:ro",
  "localfastagent:0.1"
)

& docker @dockerArgs
