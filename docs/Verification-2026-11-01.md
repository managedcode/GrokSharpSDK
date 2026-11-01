# Initial verification — 2026-11-01

Environment: macOS arm64, .NET 10, official Grok Build 1.0.70. Upstream reference revision: `2bdd1d6a6369de0e8c68132ea4539e9abd9e14a8`.

Executed successfully:

- `dotnet format ManagedCode.GrokSharpSDK.slnx` and `--verify-no-changes`.
- `dotnet build ManagedCode.GrokSharpSDK.slnx -c Release -warnaserror`: zero warnings and errors.
- Full real-CLI suite after the Windows launcher/pipe-lifecycle correction: 13 passed, zero failed/skipped. This includes 12 deterministic native integration scenarios and one real xAI device authorization scenario.
- The SDK received an HTTPS sign-in URL/user code; observer cancellation and sign-in cancellation completed as expected. The npm launcher regression completed a real turn, then cancelled another and confirmed child-pipe closure.

The full test invocation used Release, native Microsoft.Testing.Platform, one test module, a maximum of 20 tests, progress/ANSI enabled and quiet MSBuild verbosity. Exact reproducible commands are in [Testing](Testing.md).

The deterministic suite uses the actual native CLI and its persisted sessions/tools with a local HTTP model endpoint. It does not claim hosted-model qualification. Browser approval and completed account sign-in were not executed. Windows/Linux results require their CI jobs. NuGet publication is separate from these local verification results.
