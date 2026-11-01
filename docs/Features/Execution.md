# Native execution and sign-in

The core consumes Grok Build `streaming-json`. Native `text` events carry deltas, `tool_call`/`tool_call_update` carry native tool activity, and `end` carries session identity, request identity, stop reason and usage. Unknown event types remain observable as raw JSON.

Success requires a complete `end` and native exit zero. Provider errors, malformed events, missing terminal results and structured output failures are surfaced. Native cancellation is cancellation. The final event is held until exit validation finishes.

Each operation owns a CLI process. Stdout and stderr are drained concurrently into a channel with backpressure. Cancelling execution or stopping enumeration terminates the owned tree and awaits process exit and pipe completion. The configurable cleanup timeout is a lifecycle deadline, not a prompt/output size limit. Prompt files are removed when the operation completes or fails.

`BeginLoginAsync` invokes `grok login --device-auth` and waits for the native HTTPS URL and user code. It keeps the native process alive for browser approval. It never constructs OAuth requests or handles refresh tokens. A help/version-only native exit is not authentication success.

`WaitForCompletionAsync` cancellation ends observation only. `CancelAsync` and async disposal cancel sign-in and confirm process cleanup. A token supplied to `BeginLoginAsync` owns the full login lifetime. A server must retain the session object while the browser uses the URL and avoid logging the URL/code.

Credential storage belongs to Grok. Set `GROK_HOME` explicitly for a server-owned profile; do not copy another user's CLI profile automatically.

Native references at the pinned upstream revision:

- [Headless execution](https://github.com/xai-org/grok-build/blob/2bdd1d6a6369de0e8c68132ea4539e9abd9e14a8/crates/codegen/xai-grok-pager/docs/user-guide/14-headless-mode.md)
- [Custom models](https://github.com/xai-org/grok-build/blob/2bdd1d6a6369de0e8c68132ea4539e9abd9e14a8/crates/codegen/xai-grok-pager/docs/user-guide/11-custom-models.md)
- [Device sign-in](https://github.com/xai-org/grok-build/blob/2bdd1d6a6369de0e8c68132ea4539e9abd9e14a8/crates/codegen/xai-grok-login/src/device_code.rs)
