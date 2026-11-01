# 0001 — Use the official Grok Build process

Status: accepted for the initial SDK.

## Decision

Build an independent .NET core and optional MEAI adapter around the official installed CLI. Follow native headless events, native device sign-in and native session persistence. Keep the upstream repository as a pinned submodule for protocol review.

GitHub research found no reusable C# Grok Build agent SDK with this scope. Repositories that merely launch a UI or call xAI's model API do not implement this boundary. The official Rust CLI is the protocol and execution owner.

## Consequences

The SDK requires a compatible installed CLI. Tool execution and credential persistence retain their native semantics. The adapter supports text/history, sessions, streaming, model selection, explicit JSON schema and native options; unsupported MEAI inputs fail visibly.

Native tools do not become MEAI function calls, which would cause duplicate execution. A fresh text history is a labelled prompt; resumed sessions use native persisted history. Structured streaming waits for native schema validation.

The integration harness executes the real CLI against a test-owned HTTP model endpoint. This validates the agent process, protocol, tool execution, persistence and cleanup without hosted-model calls. Device authorization additionally depends on xAI's live authorization service and remains a separately selected test.
