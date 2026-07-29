# Windows Computer Use project instructions

## Product contract

- Ship the repository as a Codex marketplace containing the `windows-computer-use` plugin.
- Keep the three layers distinct: Codex skill, local stdio MCP server, and native Windows broker.
- Keep `full-control` as the default. Do not add plugin-owned confirmation lists or app allowlists. Host and Windows authorization boundaries still apply.
- Prefer app APIs or browser DOM when the calling agent has them, then UIA3, Windows OCR, and physical-pixel SendInput fallback.
- Re-observe after state-changing actions. Re-resolve stale UIA elements from stable selectors before falling back to coordinates.
- Preserve compatibility with the global lock used by `desktop-control-for-windows`.

## Change gates

- Read the root and plugin manifests before changing layout or tool names.
- Keep English `README.md` and Chinese `README.zh-CN.md` aligned.
- Run `plugins/windows-computer-use/scripts/test.ps1` before claiming completion.
- A release is not complete unless the MCP stdio handshake, UIA Unicode entry, semantic invoke, condition wait, capture, OCR, and session cleanup pass end to end on real Windows UI.
- Do not commit `bin`, `obj`, `dist`, screenshots, audit logs, or user-specific paths.
- Keep stdout of the MCP and broker protocols machine-readable; send diagnostics only to stderr.
