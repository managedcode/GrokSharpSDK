# AGENTS.md

Project: ManagedCode.GrokSharpSDK. .NET 10, C# 14, TUnit, Microsoft.Extensions.AI.

## Scope and ownership

- Read docs/Architecture/Overview.md before changing code. This repository owns a .NET client for the official Grok Build CLI and its IChatClient adapter.
- Treat submodules/xai-grok-build as pinned upstream reference material. Follow actual installed CLI capabilities and upstream wire contracts; never invent flags, models or token fields.
- Keep OAuth, agent tools, model requests, credential storage and session persistence in the native CLI. Do not duplicate them or replace agent execution with raw model API calls.
- Expose only implemented contracts. Reject unsupported MEAI content and options explicitly. Keep core independent of optional adapters.
- Use literal ProcessStartInfo.ArgumentList arguments, bounded concurrent stdout/stderr reads, explicit cancellation and confirmed process cleanup. Never log credentials, prompts or raw sign-in output.
- Every behavior change includes focused regressions and documentation. Use real installed CLI processes; a test-owned deterministic HTTP model endpoint may qualify native execution without live hosted models or account credentials. Do not substitute a fake CLI.
- Use repository-owned tests/.sandbox and artifacts paths. Never use git stash, Python, temporary copied checkouts or /tmp for agent artifacts. Preserve unrelated work.
- Never change release credentials or secret names, force-push, weaken gates or claim an unpublished package was delivered.
- Complete the code and tests, self-review, format, build and run the focused tests before committing on main.

## Commands

- Format: dotnet format ManagedCode.GrokSharpSDK.slnx
- Build: dotnet build ManagedCode.GrokSharpSDK.slnx -c Release -warnaserror
- Tests: dotnet test --solution ManagedCode.GrokSharpSDK.slnx -c Release --max-parallel-test-modules 1 -v quiet --maximum-parallel-tests 20 --progress on --ansi on
- Focused tests use --treenode-filter with native Microsoft.Testing.Platform. Zero executed tests is failed verification.
