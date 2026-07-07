# Local LLM Sub-Agent Policy

The connected AI agent is the primary implementation agent.

Use the local MCP tools to reduce token usage before reading large files, logs, or diffs.

## Rules

- Use `local_summarize_logs` before analyzing logs larger than 300 lines.
- Use `local_code_map` before broad repository exploration.
- Use `local_review_diff` before final review of large diffs.
- Treat Local LLM output as a draft, not as ground truth.
- The connected AI agent must make final design decisions.
- The connected AI agent must make final code edits.
- Do not paste full source files into the main AI agent context unless necessary.
- Do not expose secrets, tokens, credentials, or `.env` contents.
- Prefer concise summaries with file paths, symbol names, and reasons.
