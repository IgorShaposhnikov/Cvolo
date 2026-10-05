# Cvolo.Tests.Shared

Shared test infrastructure for all Cvolo test projects.

## Rules

- Fixed Cvolo programs belong under the owning test project's `TestCases/` directory, never in C# string literals.
- Fixture files use PascalCase role names (`Consumer.cvl`, `Producer.cvl`, `Struct.cvl`, `Union.cvl`, `FirstDeclaration.cvl`). One-letter names such as `a.cvl`, `s.cvl`, or `u.cvl` are not allowed.
- `TestCases` owns fixture path/read/copy/template/cursor-marker operations.
- `TestProject` owns temporary `.cvlproj` creation and writable fixture workspaces.
- `CompilerHarness` owns build/check/run, single-file isolation and artifact lookup.
- `NativeToolchain` owns clang/lld discovery.
- Project-specific test classes should contain orchestration and assertions only; reusable file/process/compiler behavior belongs here.

`CompilerTestBase` remains as a thin compatibility facade so existing compiler/std tests can migrate without duplicating infrastructure.
