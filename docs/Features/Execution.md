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

## CLI metadata and managed installation

`GetCliMetadata(timeout, maximumOutputCharacters)` reads native `--version` and `models` without an inference request. Model IDs and the default come only from the installed CLI catalog. An invalid or incomplete catalog fails visibly. Existing installations can report versions different from the SDK target.

`InstallOrUpdateCliAsync` installs the pinned official npm package into an SDK-owned, unique generation. npm runs through an explicit Node/npm-cli.js pair; Bun uses its documented [trusted lifecycle installation](https://bun.com/docs/pm/cli/install). Installation receives an explicit safe environment, isolated home/config/cache/temp directories and a private GROK_HOME. It never reads a creator's npm configuration or credentials. Output updates contain counts only. The native binary extracted by the official package must report the exact target version before its immutable launch descriptor is returned. Cancelled or incomplete generations are removed after confirmed process cleanup; unconfirmed cleanup keeps the generation for investigation and blocks safe replacement.

Launch descriptors use standard System.Text.Json constructor binding and source generation. A persisted launch can be reloaded without a custom converter. The consumer owns descriptor storage and retirement of previously successful generations.

Execution and installation failures expose `CliExecutionFailureException.RootProcessExitConfirmed`. A confirmed failure is reported only after the operation joins its process and readers. An unconfirmed cleanup exception overrides cancellation or the provider error; consumers must retain their execution fence. Native tool events expose only the standard `native_tool` activity tag and thoughts expose `reasoning`; raw native records remain caller-owned data.
