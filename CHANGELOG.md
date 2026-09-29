# Changelog

All notable changes to Cvolo are documented here.

## [0.0.21-alpha] - 2026-09-28

_Development version - features land here as they are merged, remaining unversioned until v0.0.21-alpha is tagged._

## [Unreleased]

### Added

- **Local package manager phase 3/4 work**: `cvolo pkg` now supports `install` from a lock file, `add`, `remove`, `list`, `update [id]`, and `cache list/prune/clear`; `.cvlproj` supports `<LocalFeed>`; local directory feeds can be scanned directly or loaded from `index.json`; dependency resolution writes deterministic `cvolo.lock.json` entries with source and BLAKE3 content hashes; package references can be edited in-place; cache installs are skipped when already present. The greedy resolver supports exact, `*`, caret, tilde, and floating ranges and resolves transitive dependencies from local feeds only. Indexed feeds now validate archive identity and BLAKE3 content hash before resolving. Installing unsigned packages is rejected when `~/.cvolo/keys/trusted.json` contains trusted keys. `pkg update <id>` reports `CVLP3021` when the project does not reference that package. `build`/`check`/`run` now fail package projects with `CVLP3030` when direct package references are missing from `cvolo.lock.json` or no longer satisfy their requested range. Added packaging tests for floating resolution, transitive conflicts, deterministic lock files, manifest editing, indexed-feed validation, and trusted-key unsigned rejection.

- feat(packages): implement CvlSliceManifest parser with strict validation

Extract and parse the Sector 1 Universal Slice Manifest using UTF-8 JSON.
Enforce strict size bounds (1 MiB manifest, 8 MiB layout trailing bytes).
Address critical parser vulnerabilities: prevent `ulong` overflow in bounds checks.
Add `CVLF1920` diagnostic for JSON parsing failures and missing/invalid 'Format' headers.
Throw `CVLF1920` on empty/missing 'Triple' fields instead of silently ignoring them.
Add comprehensive test corpus including bounds overflows, missing fields, and bad JSON.
Make `CvlSectorIndexEntry` a readonly struct.

- feat(packages): implement CvlArchiveReader with full validation and crypto verification

Implement `CvlArchiveReader.Read()` executing all 13 checks from §1.7 before memory mapping.
Add `CvlArchive` disposable handle wrapping MemoryMappedFile for zero-allocation sector access.
Implement full BLAKE3 page-chunked Merkle tree verification (including empty sector domain separation).
Zero out header `MerkleRootHash` field during `leaf_0` reconstruction to match hash pass state.
Implement Ed25519 signature verification over the Merkle root hash using NSec.Cryptography.
Add comprehensive suite in `CvlArchiveReaderTests` covering valid mounts, tampered hashes, and invalid bounds.

- feat(packages): add .cvlib container primitives and diagnostics

Implement `CvlArchiveHeader` (80-byte) and `CvlSectorIndexEntry` (32-byte) with strict Little-Endian serialization.
Add `CvlFormatException` for non-recoverable structural archive errors.
Define `CvlFormatDiagnosticIds` mapping to `CVLF19xx` codes per spec v0.2.6.
Enable `AllowUnsafeBlocks` in Cvolo.Core for fixed buffer header layout.
Add `Cvolo.Tests.Packages` with struct memory layout assertions and round-trip verification tests.

- **Namespaced global variables with fully-qualified reference resolution**: globals may be declared inside namespaces (`namespace Ns; global var int Value = 1;`) and referenced by their qualified name (`Ns.Value`, `System.Math.UInt.MaxValue`). `DeclareGlobalVariable` now records each global under its **qualified** name in `BindingContext` (`GlobalsByQualifiedName` + `GlobalsByShortName`), and every identifier resolution path (validation, safety, flow-analysis, and codegen) falls back to `ResolveGlobalReference` when a scope lookup misses — restoring bare-name global access (e.g. `Counter += 1;`) that regressed when per-symbol table registration was removed to fix short-name collisions. Same short names in different namespaces coexist and are reached via qualification; an unqualified reference that matches several imported namespaces is **CVL1077** (`AmbiguousGlobalReference`). Own-namespace symbols shadow imported ones; a root-namespace global wins over a `using`, and `using` alone never silently picks one of several candidates. Codegen resolves qualified keys through a single `_globalShortNames`/`ResolveGlobalKey` map and seeds bare short names into each function body, so reads, writes, `+=`, ctor-in-place assignment, member access, and extension-method receivers all work for globals regardless of namespace. Tests: `GlobalsTests` gains `FullyQualified_Execution` (QualifiedAccess, MathQualifiedGlobals), `SameShortName_QualifiedAccess` (SameNameDifferentNamespaces, SameNameAcrossPackageBoundary), `AmbiguousUnqualifiedReference_Rejected` (CVL1077), and an in-process `BindingContext_TracksSameShortNameAcrossNamespaces` unit test — cases in `TestCases/Globals/FQ/`. See *Diagnostic System Specification* §5 and *Diagnostic ID Registry*.
- **`System.Math` standard library (in progress)**: `libraries/System/Math/` — `Float`/`Double` libm-backed intrinsics (`__libm_*f` float wrappers renamed to avoid the double-typed `__libm_tan`/`__libm_*` symbol collisions), `Constants` (PI/E/Tau/NaN/±∞ globals per float and double), per-integer-namespace `MinValue`/`MaxValue` globals and `Abs`/`Min`/`Max`/`Sign`/bit intrinsics, and a `System.Math` facade re-exporting all sub-namespaces. Verified via the new `MathQualifiedGlobals` test.
- **Fix: `Namespaces/ExposeUsingBasic` fixture** implements a real Newton `Sqrt` in its local `MathLib.Double` (the earlier `x * x` stub could never produce the assert-expected `Sqrt: 1.732051`).
- **Non-allocating `try`/`catch`/`finally` and catch-expressions**: structural `try { ... } catch { ... } finally { ... }` and the single-operator `expr catch ...` are syntactic lowerings performed by `TryCatchRewriter` before `DeferRewriter` and string-interpolation lowering. `finally` is optional and belongs only to structural `try`; inline `expr catch ...` remains an independent local error-handling expression and **does not generate a `defer`**. When `finally` is present, the compiler automatically lowers it to a compiler-internal **synthetic finally-defer** around the complete try/catch operation — users never need to write an outer `defer` by hand. This internal defer uses lexical, boundary-aware binding rather than ordinary source-level defer capture-by-value: it does not copy or move outer `ResourceMove` bindings at registration, observes their current state when cleanup executes, and runs only after all crossed `try`/selected-`catch` scopes (including their user defers, automatic ResourceMove destruction, and compiler error-state cleanup) have exited. Consequently the guaranteed order is `try-scope cleanup -> catch -> catch-scope cleanup -> compiler error-state cleanup -> finally -> outer-scope cleanup`. `try`/`catch` locals do not have their lifetime extended into `finally`; ordinary values end at their lexical scope and ResourceMove locals are destroyed there, so `finally` is cleanup for the overall operation, not a replacement for RAII/destructors. A user-written `defer` **inside** `try` remains supported and independent, firing when that try scope exits. A `catch` extends a `Result<T, E>`-yielding expression at four seams — declaration (`var int x = Divide(10, 3) catch 0;`), assignment (`x = ... catch 0;`), return (`return ... catch -1;`), and whole expression statement (discard) — with a literal fallback or an arrow handler `catch (e) => { ... }` that yields the replacement value via an explicit trailing `return` (bodies without one are CVL1058; bare tail-expressions are not supported). Structural `try` still short-circuits through compiler-generated labeled blocks and per-error-type state/slots, then dispatches catches in source order; every produced error type must be covered (CVL1057). No `try`/`catch`/`finally` nodes, exceptions, closures, runtime cleanup frames, or unwind tables survive lowering. See `docs/syntax/Advanced Flow & Unmanaged Syntax Specification.md` §6 and `docs/syntax/Syntax & Control Flow Specification.md` §5.E.
- Diagnostics **CVL1051** (`catch` on a non-`Result`/unsupported shape or untyped declaration seam — error), **CVL1053** (duplicate `catch` clause pattern in one `try` — error), **CVL1054** (unreachable `catch` clause subsumed by an earlier one — error), **CVL1057** (unhandled error type emitted in a `try` block with no matching clause — error), **CVL1058** (`catch (e) => { ... }` body must end with a `return` statement — error), **CVL1059** (`catch` pattern type not marked `[Error]` — error), **CVL1067** (value-pattern `catch (E.V)` requires `E` to be an `enum` — error). Reported by `TryCatchRewriter` at rewrite time. See *Diagnostic System Specification* §5 and *Diagnostic ID Registry*.
- **`[Error]` intrinsic attribute** registered for `Struct`/`Union`/`Enum` targets (all safety contexts) in the `IntrinsicAttributes` table — accepted and erased at codegen like the other metadata attributes. See *Attributes & Metadata Specification*.
- Tests: `TryCatchTests` (9 execution cases — literal seam, arrow lambda with `return` tail, return seam, success path, first/second-clause dispatch, unmatched-error fall-through (no bubbling), multi-receiver type dispatch, `[Error]` enum + union type-pattern binding; 8 rejection cases — CVL1051 nested + untyped decl, CVL1053 duplicate clause, CVL1054 unreachable clause, CVL1057 uncovered error type, CVL1058 lambda without `return`, CVL1059 pattern without `[Error]`, CVL1067 value-pattern on a struct) in `TestCases/TryCatch/`.
- **`foreach` loop (Hardened Structural Iteration)**: `foreach (val|var|refvar item in collection) { ... }` with arrays/slices desugared to a zero-alloc index-based while loop, and user-defined types iterated structurally via `GetEnumerator()` returning a type with `bool MoveNext()` and `Current`. Binding matrix: `val`/explicit `T item` binds a read-only item (value copy or immutable reference binding when `Current` returns `ref T`/`refvar T`); `var item` always yields a detached mutable local copy (never propagates back to the collection); `refvar item` yields a mutable reference slot that mutates collection elements directly (requires `Current` to return `refvar T`). `foreach` is a contextual keyword. The `forEachBinding` parser rule accepts `val`/`var` with optional explicit type, bare explicit type, or `refvar`; a `refvar T item` combination is rejected. The loop body is a block statement (`BlockStatementSyntax`). `foreach` participates in loop labels, `break`/`continue`, defer splicing (`DeferRewriter`), generic substitution (`BindingContext`/`ValidationPass`), and safety analysis (`SafetyPass`). The emitter lowers it with `continue`-to-increment semantics and deferred enumerator destruction (`EmitForEachEnumeratorCleanup`) on all exit paths; for reference bindings the item slot is a pointer that enables write-through to the collection.
- Diagnostics **CVL1080** (collection type has no `GetEnumerator` — error), **CVL1081** (enumerator missing `bool MoveNext()` — error), **CVL1082** (enumerator missing `Current` — error), **CVL1083** (`MoveNext()` does not return `bool` — error), **CVL1084** (read-only foreach item assigned — error), **CVL1085** (explicit item type does not match `Current` yield type — error), **CVL1086** (`refvar` over by-value `Current` — error), **CVL1087** (`refvar` over read-only `ref T` `Current` — error), **CVL1088** (escape-boundary violation: reference loop variable leaks out of loop body — error), **CVL1089** (ambiguous `GetEnumerator()` overloads — error), **CVL1090** (`GetEnumerator()` inaccessible due to visibility — error). See *Diagnostic System Specification* §5.
- Section 4.D immutable borrow contract: structural mutation of the collection (reassignment, mutating method calls) inside the `foreach` body is a compile-time error.
- Tests: `ForEachTests` (16 execution cases including refvar array, val/var/refvar over `ref T` and `refvar T` Current, ambiguity regression guard; 14 rejection cases covering CVL1080-CVL1090 including escape, borrow contract, visibility folder, refvar+explicit-type rejection) in `TestCases/ForEach/`.
- **Inlining control attributes `[Inline]` / `[NeverInline]`** (Function/Method/Constructor level): `[Inline]` forces LLVM inline expansion by attaching the `alwaysinline` function attribute; `[NeverInline]` suppresses inlining via the `noinline` function attribute. Emitted in `CodeGenerator.DeclareFunction` from `FunctionSymbol.IsInline` / `FunctionSymbol.IsNeverInline`. Combining both on one declaration is an error; `[Inline]` on a directly recursive function warns.
- Diagnostics **CVL1400** (`[Inline]` and `[NeverInline]` on the same declaration — error) and **CVL1401** (`[Inline]` on a recursive function — warning, suppressible). See *Diagnostic System Specification* §5 and *Attributes & Metadata Specification* §2.B.
- Tests: `AttributesTests` inline coverage (3 execution cases incl. an extension method, 2 IR-attribute assertions via `--emit-ir -O0`, recursion warning, mutual-exclusion rejection) in `TestCases/Attributes/`.
- **Field-sensitive TBAA `!tbaa` metadata** for scalar struct fields (`TbaaMetadata` in `Cvolo.Emitter.LLVM`): struct-path tags `{struct node, field leaf node, i64 offset}` in the verifier-conformant schema (struct node embeds ascending `(leaf, byteOffset)` descriptor pairs; root = `!"scalar"`, one leaf per primitive type). Applied at member load/store/incdec/`this`-field sites. Suppressed for all `ref`/`refvar`/pointer/slice/interface dereferences, inside `[UnsafeBody]`/`unsafe` contexts, and for any struct containing a reference-layer field. See *Memory & Safety Specification* §4.
- Tests: `AttributesTests` TBAA coverage (tagged owned-scalar loop, tagged refvar access, `[UnsafeBody]` suppression, ref-field-struct suppression; each asserts `!tbaa` presence/absence in `--emit-ir -O0` output and executes the binary) in `TestCases/Attributes/Tbaa/`.
- **`--no-tbaa` CLI flag** for `build`/`run`: disables generation of `!tbaa` alias-analysis metadata nodes (the TBAA gate in `TbaaMetadata.GetFieldTag` is a new `_enableTbaa` flag on `CodeGenerator`, threaded through `CompilerDriver`/`ICompilerDriver`). Handy for A/B-measuring the optimizer's use of field-sensitivity.
- **Benchmark harness `src/Cvolo.Benchmarks`**: a BenchmarkDotNet suite comparing Cvolo versus `rustc -O` head-to-head over five scenario programs (scalar field loop, array-of-struct sum, heap linked list, heap ring buffer, n-body — see `docs/PERFORMANCE.md`). Each `[Benchmark]` runs the prebuilt scenario binary as a subprocess and verifies the printed `Answer: N` checksum; BenchmarkDotNet auto-selects iteration counts for stable timings and writes CSV/Markdown/HTML reports. `ScalarLoop` is measured at O0/O2/O3 with TBAA on and off; the other scenarios add O0/O2/O3 versus Rust.
- **Pointer/`refvar` member TBAA relaxation**: fields accessed through `refvar`/`ref`/heap pointers to structs *without* reference-layer fields now carry `!tbaa` metadata (distinct struct types are disambiguated by TBAA). Previously all refvar-path accesses were unconditionally suppressed. This is observable in the new `PtrMix` benchmark, where the `[NeverInline]` function's loop is vectorized with 4 memory ops (tagged) vs 13 memory ops (untagged) after O3. Suppression remains for `[UnsafeBody]`/`unsafe`, structs with reference fields, and `--no-tbaa`. See `docs/TBAA_PERFORMANCE_ANALYSIS.md`.
- **Benchmark `PtrMix`** (pointer-aliasing showcase): `[NeverInline] int Mix(refvar Point, refvar Box, int)` — exercises the TBAA relaxation + linkage fix at O3 with tags on and off plus Rust.
- **`docs/TBAA_PERFORMANCE_ANALYSIS.md`**: analysis of why TBAA is invisible on inlined loops, the `[NeverInline]` fix, the QPC prototype lesson, and the PtrMix result.
- **`defer` statement**: new `defer <statement>;` that schedules cleanup at block exit. Grammar adds the `DEFER` lexer token and `deferStatement` parser rule (body restricted to a single expression statement or a block); AST gains `DeferStatementSyntax` (`SyntaxKind.DeferStatement`); the new `DeferRewriter` (registered first in `CompilerDriver`'s rewriter chain) duplicates the body — LIFO — immediately before every `return` in the block (at any nesting depth) and on natural fall-through, so deferred cleanup is guaranteed and **zero-cost** (pure syntactic lowering, no runtime frame). Per-block positional registration: inner-block defers run before outer-block defers at nested returns; a nested block's fall-through runs only its own defers. Loop bodies fire per-iteration. No new diagnostics. Tests: `DeferTests` (Lifo, EarlyReturn, Nested — LIFO order, early-return fire-once, cross-block ordering) in `TestCases/Defer/`. See `problems/Inc - defer Statement.md` and `docs/syntax/Advanced Flow & Unmanaged Syntax Specification.md` §1.
- **Authoritative benchmark numbers published**: `docs/PERFORMANCE.md` gains a "Latest results" section (full `DefaultJob` run, grouped by scenario, sorted fastest-first by mean); `docs/TBAA_PERFORMANCE_ANALYSIS.md` §1 is refreshed with post-fix means. Summary: Cvolo `-O3` beats `rustc -O3` (`-C lto=fat`) on LinkedList (6.168 vs 6.498 ms), MatrixSum (6.319 vs 7.077 ms), Nbody (14.681 vs 15.137 ms), PtrMix (7.966 vs 8.295 ms), RingBuffer (6.632 vs 6.984 ms) and ScalarLoop (8.038 vs 8.338 ms).

- **Labeled loops + flat `break label;` / `continue label;` (v0.3.0)**: loops may carry a label (`name: for (...)`, `name: while (...)`) and `break label;` / `continue label;` transfer to an ancestor loop by name, letting an inner loop terminate (`break`) or restart (`continue`) a specific outer iteration. Bare `break;` / `continue;` target the innermost loop as before. Labels are unparenthesized — the parenthesized forms `break(label)` / `continue(label)` are now rejected by the parser. `DeferRewriter` (single-pass scope-stack model, diagnostics-injected) flushes a labeled jump's crossing `defer` scopes in LIFO order — innermost first, then the target loop's body defers — exactly once each (the spec §4 example replays the inner `ResetIterationBuffers()` defer before the outer one).
- **Targeted `defer label:` (v0.3.0)**: a defer may name an ancestor label to anchor its body to that loop's body scope instead of the immediately enclosing block (it fires only when the targeted scope exits — fall-through or any jump out of it). The colon is mandatory; flat `defer label body;` is CVL1067 (`TargetedDeferMissingColon`), detected by a parse-time token scan.
- Diagnostics **CVL1062** (duplicate loop label in the same enclosing scope — error), **CVL1063** (unresolved labeled branch target or targeted-defer anchor — error, reported by the binder for jumps and by `DeferRewriter` for targeted defers), **CVL1067** (missing colon on a targeted defer — error), **CVL1070** (`break`/`continue` outside a loop or switch-case body context — error; an unlabeled `break` is legal inside a switch case, where it exits the switch C-style, while `continue` remains loop-only), **CVL1071** (cross-iteration defer barrier: a targeted defer cannot anchor to an outer loop across a nested loop boundary — now enforced by `DeferRewriter`). See *Diagnostic System Specification* §5.
- Tests: `LabeledLoopsTests` (unlabeled break/continue, flat labeled break/continue, defer-splicing across `continue outer;`, targeted-defer anchoring across a bare block, `break;`/`continue;` inside switch cases (break exits the switch C-style and fires case-local defers; continue targets the enclosing loop), five semantic rejections + CVL1067 + CVL1071, and the parenthesized-break parse error) in `TestCases/LabeledLoops/`.
- **`break;` inside switch cases (C-style switch exit)**: an unlabeled `break` in a switch case terminates the switch and resumes after it — even when a loop encloses the switch — without touching the enclosing loop's defers. `continue;` inside a switch case still targets the enclosing loop. `DeferRewriter` treats each case body as its own defer scope (case-local defers previously never fired), `CodeGenerator` tracks an explicit per-switch break target (`_switchBreakStack`), and `ValidationPass` accepts a bare `break` (not `continue`) at switch-case depth. The `Enums & Algebraic Types Specification` switch samples (which use `break;`) now compile unchanged.
- **Block-scoped `defer` + labeled blocks + `break label;` (supersedes the v0.3.0 `defer label:` form)**: `defer <expr>;` and `defer { ... }` register an action with the current block; `defer L { ... }` anchors to the nearest enclosing block or loop labeled `L`; `L: { ... }` labels a block and `break L;` exits it. Defers run LIFO (innermost first) on every exit path — fall-through, `return`, or `break L;` — and free variables are captured **by value at registration time** (`val __defer_cap_N = <var>;`), so later mutations of the source variables are invisible to the action. `return expr;` lowers to bind `val __ret = expr;`, run the defers, then `return __ret;`. `DeferRewriter` now processes defer/return/break/continue as single-statement positions (nested if/while branches included) and drops the cross-iteration barrier. The old `defer label: body;` colon syntax is removed; a labeled expression body (`defer session f();`) is a syntax error.
- Diagnostics **CVL1061** (a `defer L { ... }`/`break L;` names a label not in scope — error; the old `LabeledBranchTargetNotFound` meaning is folded in), **CVL1062** (label redeclared in the same enclosing scope — error, wording unified for blocks and loops), **CVL1063** (control flow cannot leave a `defer` body — error; `return`/`break`/`continue` inside defer), **CVL1064** (`defer` nested inside another `defer` — error), **CVL1065** (`defer` requires a statement or block body — error; `defer;`, `defer if (...) ...`), **CVL1066** (`break` requires a label in this version — error; `break;` outside a loop/switch). **CVL1067** and **CVL1071** are removed (colon-syntax and cross-iteration-barrier obsolete). The `expose using` namespace-not-found diagnostic moves from CVL1061 to **CVL1050**. See *Diagnostic System Specification* §5.
- Tests: `DeferTests` grows the spec §7 corpus (block-body defer, labeled blocks + `break L;`, targeted `defer L { ... }` incl. nested-anchor and break-trigger cases, nested LIFO ordering, per-iteration loop registration, capture-by-value, `return computing-expr` firing defers after the value is computed, plus CVL1061–CVL1066 rejection cases) in `TestCases/Defer/`; `LabeledLoopsTests` moves to the new defer syntax (`defer outer { ... }`) and the new ids. See `problems/Inc - Block-Scoped Defer and Labeled Block Control Flow.md` and `docs/syntax/Advanced Flow & Unmanaged Syntax Specification.md` §1.

### Fixed
- **`[NeverInline]` was silently inlined at `-O1+`**: LLVM's attributor stripped the `noinline` string attribute from `internal` functions during `default<O1+>` pipelining, after which the normal inliner could inline the callee. `[NeverInline]` functions are now declared with external linkage (`LLVMExternalLinkage`), which the LLVM inliner never touches. Constant-propagation folding of fully-constant-argument calls at O3 is unrelated (interprocedural SCCP, not the inliner) and correct. See `docs/TBAA_PERFORMANCE_ANALYSIS.md` §4.2.
- **`-O` level was never forwarded to the clang link step**: Cvolo optimizes its in-memory IR in-process (`default<O3>` via LLVMSharp) and writes a textual `.ll`, but the `clang -o <exe> <ll>` invocation passed **no `-O` flag**, so clang's backend silently regenerated the whole module at its default `-O0` — the loop that looked vectorized in the `.ll` became scalar at runtime. `ICompilationStrategy.Execute` now takes the `optLevel` (default `"Os"`) and `LinkStrategy` passes `-O<level>` before the subsystem flag (`O3`→`-O3`, `Os`→`-Os`); `IrOnlyStrategy` ignores it. ScalarLoop result, authoritative `DefaultJob`: best Cvolo config <code>-O2</code> **8.038 ms vs Rust 8.338 ms** (previously 12.674 ms at `-O3`); Cvolo `-O3` now beats Rust on all six benchmark scenarios. See `docs/TBAA_PERFORMANCE_ANALYSIS.md` §2 and `docs/PERFORMANCE.md` "Latest results".

## [0.0.10-alpha] - 2026-09-06

### Added
- **Optional type syntax sugar `T?`**: `T?` desugars to the stdlib `Option<T>` union via `OptionalSyntaxRewriter` (structural positions: declarations, parameters, returns, fields, extension/interface/protocol signatures) plus a `ResolveType` fallback for residual positions (generic type arguments, casts, `default(T?)`). `ref T?` / `refvar T?` lower to the flat NPO pointer (`Option<ref T>` / `Option<refvar T>`), giving the `?` syntax direct access to zero-cost nullable references. Plain-value initializers and `x = value` assignments on `T?` locals/globals (including `ref T?` borrow RHS) are wrapped into `Option<T> { Some: value }`; `Option.None` initializers/assignments are lowered to `Option<T> { None: void }`; explicit `Option<...>`/`default(Option<T>)`/`default(T?)` right-hand sides are recognized and not double-wrapped. `System.Option` declares the `None` void variant first, so its tag is `0` and `default(T?)` yields `None`.
- Diagnostics **CVL1100** (strict-option disables the sugar), **CVL1101** (chained `??` rejected at parse level), **CVL1102** (`?` on `void`/function types), **CVL1103** (`ref`/`refvar` type argument on a non-Option generic — formerly CVL1045), **CVL1104** (`null` init/assign on `T?`). See *Diagnostic System Specification* §5.
- `--strict-option` CLI flag for `build`/`check`/`run` and `<StrictOption>true</StrictOption>` in `.cvlproj`.
- `is`/`is None` pattern matching over tagged `Option<T>`, including `ref`/`refvar` operands and refvar aliases (binds the payload as a reference; fixed a codegen crash where a refvar operand was loaded as the union value). An NPO `Some` check lowers to a single `icmp ne ptr, null`.
- **Bare `default` in declarations**: `int x = default;` and `int? x = default;` (also globals) are lowered to `default(T)` / `default(Option<T>)` by `OptionalSyntaxRewriter`, so `int? x = default;` yields `None`. Bare `default` anywhere without an inferable type reports an error (use `default(T)`).
- Tests: `OptionalsTests` (6 execution + 3 rejection cases + strict-option via CLI flag and via `.cvlproj` + chained-`?` rejection + NPO flat-layout IR assertion) in `TestCases/Optionals/`.

- **Type aliases (`alias`)**: top-level `alias Name = Type;` and generic `alias Name<T> = Type;` directives that expand transparently to the underlying type at bind time — zero cost, scoped by namespace. Chained aliases (`alias B = A; alias C = B;`) and alias-of-alias are resolved transitively. Aliases may be used in all type positions: declarations, parameters, returns, struct fields, generic arguments, `where` constraint targets (error), and `extension` blocks. Aliases are skipped by `ValidationPass`, `SafetyPass`, `FlowAnalysisPass`, and `CodeGenerator` via standard `is` pattern-matching (no whitelist edits required).
- Diagnostics **CVL1200** (alias target does not resolve to a known type), **CVL1201** (alias cycle detected), **CVL1202** (alias used in a `where` constraint). See *Diagnostic System Specification* §5.
- AST node `TypeAliasDeclarationSyntax` with `SyntaxKind.TypeAliasDeclaration`; parser (`AntlrSyntaxParser.BuildAliasDeclaration`) wired into `BuildDeclaration`; `CvoloSourcePrinter` prints alias declarations; ANTLR grammar extended (`aliasDeclaration` rule + `ALIAS` lexer token).
- `DeclarationPass` handles alias registration: Pass 0a-pre runs `DeclareAlias` for every `TypeAliasDeclarationSyntax` before type/struct registration; Pass 0a-post runs `ValidateAliases` (CVL1200 on unknown RHS, CVL1201 on cycles, CVL1202 on constraint targets, duplicate-name detection, real-type-name conflicts). `BindingContext.ResolveType` step 1b performs transparent macro expansion of alias references during resolution; `ExpandAliasTarget` recursively resolves the RHS string, with cycle-guard via `_aliasExpansionStack` and duplicate-cycle reporting via `ReportedAliasCycles`.
- Tests: `TypeAliasesTests` (3 execution + 6 rejection cases) in `TestCases/TypeAliases/`.

### Fixed
- **`is` / `is None` pattern matching over `ref`/`refvar` operands of NPO options**: `if (ref opt is Some node)` now matches by reference (operand unwraps to the option's storage and its flat pointer value is tested against null — mirroring the switch-by-ref emitter). The `Some` payload promotes to the inner `ref`/`refvar` and binds correctly for locals, struct-field borrows (`refvar a.Next`), and mutable binds. Previously a borrow operand caused an "Undefined variable" codegen crash and the bound variable was never registered.
- **Tests**: `NpoIsPattern` execution case (Some/None paths, field borrow, mutable bind) in `NpoTests`.

## [0.0.9-alpha] - 2026-09-05

### Added
- **`ref`/`refvar` generic type arguments restricted to Option-shaped unions**: `Option<ref T>` / `Option<refvar T>` (including generic referents like `Option<refvar Node<T> >`) remain NPO-optimized flat 8-byte null-pointer-optimized references, but using a `ref`/`refvar` as a type argument for any other generic type (structs, protocols, non-Option unions) is now rejected.
- Diagnostic **CVL1103** (`Generic type '{0}' cannot use 'ref'/'refvar' as a type argument; only Option-shaped unions ...`). See *Diagnostic System Specification* §5.
- Tests: `NpoGenericRefArg` execution case (NPO reference to a generic struct field) and `NpoRefArgNonOptionFail` rejection case in `NpoTests`.

## [0.0.8-alpha] - 2026-09-05

### Added
- **Constructor chaining** (`T(args) : this(...)`): a constructor may delegate to another constructor of the same type after its parameter list. The initializer is resolved with the normal constructor overload rules, respects visibility, and the target constructor receives the implicit `this` and runs **before** the delegating body. Covers generic ctors (e.g. `HeapArray(int size) : this(default(A), size)`) and monomorphized instantiations; enums/unions are out of scope (no chaining there).
- Diagnostics **CVL1043** (cyclic `this(...)` delegation — error) and **CVL1044** (non-empty delegating-constructor body — warning). See *Diagnostic System Specification* §5.
- Tests: `ConstructorChaining` / `ConstructorChainingGeneric` execution cases, `ConstructorChainingCycleFail` rejection, `ConstructorChainingNonEmptyBody` warning, and `ConstructorChainingAccess` multi-file accessibility rejection in `GenericsTests`.

## [0.0.7-alpha] - 2026-09-04

### Added
- **Default generic parameters** (`where default P : T` on `struct` / `union` / `extension` declarations): when a trailing generic type argument is omitted at instantiation, its declared default type is substituted — `struct Buffer<T, A> where default A : MallocAllocator {}` lets `Buffer<int>` desugar to `Buffer<int, MallocAllocator>`. Defaults must be Trivial Copy Types.
- **`default(T)` operator**: yields the zero-initialized value of a Trivial Copy Type (`default(int)` → `0`, `default(Point)` → `zeroinitializer`); ZST (zero-sized structural types) cost nothing. Using it on a Resource Move type is rejected.
- Diagnostics **CVL1040** (default generic parameter is not a Trivial Copy Type), **CVL1041** (omitted generic argument has no default), **CVL1042** (default type violates a generic-parameter constraint — reserved for the upcoming constraint model). See *Diagnostic System Specification* §5.
- Tests: `DefaultGenericParams` / `DefaultOperator` parser and analysis cases plus CVL1040 / CVL1041 rejection cases in `GenericsTests`.

## [0.0.6-alpha] - 2026-09-02

### Added
- **Visibility modifiers** (`private` / `internal` / `public`) across all top-level declarations, struct & union fields, and extension constructs — see the *Visibility & Access Control Specification*. The compiler previously had no access-control system; this release introduces it as an alpha feature with breaking changes.
  - **Triad semantics**: `private` = file scope, `internal` = module scope (the whole compilation is a single module today, so `internal` is always reachable), `public` = universal (ABI).
  - **Defaults**: struct/union fields → `private`; top-level declarations (function/struct/union/enum/protocol/interface) → `internal`; FFI `extern` → `internal` (and may never be `public`); `global` variables may be any.
  - **Extension blocks**: a block may carry a visibility modifier; members inherit the block's visibility and may only *narrow* it (a `public` method inside an `internal` block is rejected).
  - **`--legacy-visibility` CLI flag** (on `build` / `run` / `check`): treats every declaration as public and suppresses all visibility diagnostics, restoring v0.2.0-alpha behavior.
- Visibility enforcement diagnostics **CVL1030–CVL1036** and **CVL1038** (see *Diagnostic System Specification* §5):
  - **CVL1030** Inaccessible member (private field accessed cross-file, `obj.field` or `obj->field`).
  - **CVL1031** Visibility expansion inside an extension block.
  - **CVL1032** External struct literal initializing a private field.
  - **CVL1033** `public` FFI `extern` declaration.
  - **CVL1034** Switch pattern binding a hidden union payload variant.
  - **CVL1035** Unbound sandbox mutating a non-visible `ref`/`refvar` structural field.
  - **CVL1036** `public` global of a multi-word container type (slice, interface, or >8-byte struct/union/enum) without a `Lock`/`Mutex`.
  - **CVL1038** Generic instantiation whose host visibility exceeds a type-argument visibility.
  - **CVL1037** (friend-package spoofing) and **CVL1039** (private/anonymous symbol export) are reserved: they require the not-yet-implemented package/friend-module and backend-export models.
- `Visibility` enum (`Cvolo.Core/AST/Base/Visibility.cs`), a `Visibility` / `SyntacticVisibility` property on declaration AST nodes, and `Visibility` / `DeclaringUnit` on symbols and type symbols; visibility is threaded through generic monomorphization, embed promotion, and extension constructors/destructors.
- `VisibilityTests` (10 tests) + `TestCases/Visibility/` corpus (`AccessControl/`, `UnboundSandbox/`, `PrivateVariant/` multi-file folders and single-file cases) covering all enforced diagnostics and the `--legacy-visibility` flag.

### Fixed
- Generic-instantiated struct/union types now preserve per-field and type visibility (previously all instantiated fields reverted to `private`/`internal` defaults).
- Fixed a namespace collision where `Cvolo.Analysis.Visibility` (namespace) shadowed the `Visibility` enum used as a type; the checker namespace is now `Cvolo.Analysis.VisibilityChecks`.

## [0.0.5-alpha] - 2026-08-29

### Added
- Close remaining v0.0.5-alpha safety gaps (Memory & Safety §2 / §5 / §6):
  - **Caller-side unsafe invocation check**: calling a raw `unsafe fn` from safe code is now a compile error (**CVL1009** — *"Cannot call unsafe function '{name}' from safe code. Wrap the call in an 'unsafe { }' block or mark this function 'unsafe'."*). `[UnsafeBody]` functions remain callable from safe code; an inline `unsafe { }` block satisfies the caller requirement.
  - **CVL1008 extended to struct-field escapes**: an `unbound`-scoped local reference may no longer be stored into the `ref`/`refvar` reference field of a non-local variable (function parameter), preventing unbound references from escaping through structural fields.
  - **Structural field-mutation isolation** (Rule 7, **CVL1012**): writing to a struct's `ref`/`refvar` reference field in safe code is a compile error — reads and traversal remain legal, only `unbound` may mutate structural reference fields. Raw-pointer fields (`Node*`) are unaffected.
- Lifecycle extension-block & static destructor safety conformance (Memory & Safety §C/§2):
  - **Extension receiver markers** `ref this` (read-only reference) and `refvar this` (mutable reference); an unmarked receiver is auto-inferred from field mutation, emitting a **CVL1011** warning when inference selects mutability (declaration-site; suppressible via `[SuppressWarning("CVL1011")]` / `--nowarn CVL1011`).
  - **`[StrictMutability]`** attribute on a struct: every extension method must declare `ref this` or `refvar this` (error otherwise); **constructors and destructors are exempt** (no receiver markers).
  - **Array destructor loop**: static `T[n]` arrays of Resource-Move (or transitively move-bearing) element types auto-generate a reverse `Length-1..0` per-element destructor loop on scope exit. Linear element types are excluded (no obligation).
  - **Nested move-type field drop**: a struct without its own `~T()` recursively drops its transitively embedded resource-move fields (arrays, unions, nested structs) down to a dtor-bearing leaf; a struct with its own dtor owns its internals (no implicit nested drop, avoiding double-drop).
  - **Cyclic destructor depth limit** (1024): a compile-time destructor-graph walk over owned by-value fields errors with *"Cyclic destructor nesting depth exceeded. Please use an arena allocator or manual cleanup."* when the nesting depth exceeds 1024. Pointer edges are not ownership edges and are skipped (a self-pointer struct does not error). By-value struct cycles are not expressible in safe Cvolo, so cycle detection is defensive; the depth cap is the enforced surface.
  - **Deferred**: the Panic-Unwind `invoke`/landingpad invariant (§2 §B), documented as moot under the current abort-based panic model (no unwinding).
- Null-Pointer Optimization (NPO): `Option<ref T>` / `Option<refvar T>` now compiles to a single flat 8-byte LLVM pointer (Some = non-zero address, None = `0`) with zero size/tag overhead. The Option-shape recognition is now structural (`UnionTypeSymbol.IsOption` / `IsNpoEligible`) rather than name-based `Contains("Option")` matching.
- NPO construction and member access: `Some: value` stores the reference directly into the flat slot; `None` / `null` stores `nullptr`; `opt.Some` reads the flat pointer (no tag GEP, no payload bitcast).
- `NpoTests` corpus with `NpoFlatPtr` execution case.
- Static-only (monomorphized) interfaces: nominal `interface IName { ... }` declarations with required method signatures, and retroactive conformance via `extension T : IName`. Interface-typed value, `ref`, and `refvar` parameters are lowered to implicit generic templates and monomorphized at each call site with a concrete conforming type — no vtable, no fat pointers, no dynamic dispatch.
- Interface conformance validation: an extension declaring conformance must provide every required member (name + parameter types + return type), and unknown interfaces / missing members are rejected at declaration time.
- Interface dispatch diagnostics: value/ref/refvar interface parameters resolve to a single concrete conforming type per call; non-conforming arguments, conflicting concrete types, argument-count mismatches, and interface-typed (unresolvable-concrete) arguments are rejected. Conformance and dispatch errors are id-less.
- `InterfacesTests` / `TestCases/Interfaces/` corpus covering conformance, value/ref/refvar static dispatch, non-conforming and unresolved-concrete rejections.
- Protocols (implicit structural typing): `protocol IName { ... }` declarations; a concrete type automatically conforms when it provides every required member signature via extension methods — no explicit `implements` needed. Protocol-typed value/`ref`/`refvar` parameters lower to implicit generic templates monomorphized at each call site (zero-cost static dispatch, no vtables, no boxing), reusing the interface dispatch machinery.
- Protocol default implementations: `extension IProtocol { ... }` blocks provide default bodies that conforming types inherit automatically unless they declare their own matching method. Defaults materialize onto the concrete type at first conforming dispatch; a conformer's own method always overrides the default.
- Contract hierarchy: `:` inheritance/aggregation — protocol-to-protocol (`protocol IStream : IReader, IWriter`), interface-to-interface, and interface-to-protocol; conforming types implicitly satisfy the parent capability graph, and ancestor default implementations propagate across hierarchy boundaries.
- `for` requires-clauses: `protocol ISorter for IRecordId` and `interface IButton for IWidget<Self>` anchor external capability requirements on `Self`. Protocol requirements are enforced lazily at the call site; interface requirements aggressively at conformance registration.
- Extension collision rule: two extension blocks providing the same method signature on the same type are rejected with a Duplicate Symbol error; ambiguous protocol dispatch (a member matched by multiple distinct extensions) is rejected at the call site.
- `embed` composition: `struct Warrior embed BaseEntity { ... }` flattens the embedded type's fields at the front of the struct layout (LLVM layout, byte size, and global const init all recompute from the flattened field list) and promotes the embedded type's extension methods onto the outer type — giving implicit structural protocol conformance through promoted methods. Nominal interface conformance remains non-transitive across `embed`. Generic struct templates, cyclic embeds, and conflicting field names are rejected.
- `EmbedTests` / `TestCases/Embed/` corpus covering flattening, promoted-method dispatch, protocol conformance via embed, embed chains, override precedence, and field-collision / generic / interface non-transitivity rejections.
- Enum declarations: `enum Name : byte { ... }` with exact-width storage (`byte`/`sbyte`/`short`/`ushort`/`uint`/`long`/`ulong`/`char`, default `int`), backed by new integer primitives with full sizeof/arithmetic/cast support, C#-style scoped variant access (`Status.Active`), extension blocks over enums with unqualified variant access, empty-enum and duplicate-type/enumerator rejections, and per-enum compile-time constant resolution.
- Exhaustive enum switch matching: a `switch` over an enum demands a case for every variant (or an explicit `default`), with a safe `default: llvm.trap` + `unreachable` fallback for every safe switch jump table (Unsafe-Poisoning Protection + LLVM Unreachable Invariant).
- Safe enum↔integer casts: in the safe zone, `(Enum)n` produces `Option<Enum>` (tagged union, `Some`/`None` switchable); enum→underlying casts return the scalar; inside `unsafe` blocks integer→enum casts lower straight to the raw value with no `Option`.
- `[Flags]` attribute: auto-shift unsigned bitmasks (the zero flag must be named `None`/`Empty`/`Unset`/`Zero`), composite-mask collision detection, a width-locked compile-time constant evaluator, synthesized `|`/`&`/`^`/`~` operators with masked inversion (`& CombinedAtomicMask`) and `HasFlag()`, and relaxed (non-exhaustive) switch coverage.
- Enum metaprogramming: synthesized `Name()` (O(1) `.rodata` string lookup), `Values` (read-only `.rodata` slice; `[Flags]` enums expose only atomic prime variants), and compile-time `Min`/`Max`/`Count` sentinels usable in array-size expressions, guarded by a 1 MB stack-allocation safety threshold.
- `[NonExhaustive]` attribute: open-for-extension enums across unit boundaries — the defining unit keeps strict exhaustive switches, consumers in a different unit must supply a developer `default`/`case _` (the implicit `llvm.trap` fallback is never emitted there), and Non-Void Return Verification blocks non-void functions whose terminal `[NonExhaustive]` switch default does not terminate or lacks a downstream return. No cross-package model yet, so the internal/consumer split is unit-based (per `SymbolUnits` defining-unit tracking).
- `EnumsTests` (49 tests) + `TestCases/Enums/` corpus covering declarations, casts, extension dispatch, exhaustive switches, flags, metaprogramming, and the `[NonExhaustive]` consumer contract incl. multi-file cross-unit folder projects.

## [0.0.4-alpha] - 2026-08-27

### Added
- Safety Tier System: three-tier compilation model (`SafetyTier` enum: `Safe`, `Unbound`, `Unsafe`) with stack-based tier tracking in `SafetyPass`. Function modifiers (`unsafe fn`, `unbound fn`) and inline `unsafe { }` blocks push/pop tiers. `SafetyPass` conditionally skips or relaxes checks: `Unbound` deactivates exclusive-mutability borrow checks; `Unsafe` skips all safety rules entirely.
- `unbound` function modifier: creates a context where multiple `refvar` references can alias the same memory. Borrow checker's exclusive-mutability checks are suspended. Noalias suppressed by default. Escape Prevention Guard (CVL1008) prevents local refs from escaping to globals. Scope scrubbing ensures aliasing cannot leak into safe code.
- `unsafe` function modifier and inline `unsafe { }` block statement: three entry patterns — (A) `unsafe` keyword in signature, (B) `[UnsafeBody]` attribute, (C) inline `unsafe { }` block. All borrowing, move, and lifetime checks suspended. Raw pointer syntax (`T*`, `*`, `&`, `->`) available.
- Raw pointer type system: `RawPointerTypeSymbol` (distinct from `PointerTypeSymbol` for `ref`/`refvar`). C-style `T*` pointer type syntax in grammar. `BindingContext.ResolveType` handles `T*` by detecting trailing `*`. `VariableSymbol.IsRawPointer` flag. Typed LLVM pointer emission.
- `[NoAlias]` dual-level emitter support: function-level `[NoAlias]` applies LLVM `noalias` to all reference parameters; parameter-level applies to specific parameter. Uses unsafe fixed-pointer marshaling for LLVM C API interop.
- Grammar extensions: `unbound` keyword, `functionModifier` rule (`UNSAFE | UNBOUND`), `unsafeBlockStatement`, `pointerType` (`type STAR`), `dereferenceExpression` (`*expr`), `addressOfExpression` (`&expr`), `arrowMemberAccessExpression` (`expr->Identifier`).
- `UnsafeBlockStatementSyntax` AST node with `SyntaxKind.UnsafeBlockStatement`. `FunctionDeclarationSyntax.Modifier` property (`SafetyTier?`). `MemberAccessExpressionSyntax.Operator` property for dot vs arrow disambiguation.
- CVL1004 (AttributeWrongTier), CVL1005 (RawPointerOutsideUnsafe), CVL1006 (DereferenceOutsideUnsafe), CVL1007 (AddressOfOutsideUnsafe), CVL1008 (RefEscapesUnboundScope), CVL1010 (UnboundNoRefParams) diagnostics.
- `UnsafeOperationScanner` expanded to detect dereference (`*`), address-of (`&`), and pointer casts (`(TypeName*)`).
- 11 new Sandbox test cases: `UnsafeModifierBasic`, `UnboundModifierBasic`, `UnboundNoRefParams`, `NoAliasOnUnbound`, `InlineUnsafeBlock`, `UnsafeFunctionCall`, `DerefOutsideUnsafe`, `AddrOfOutsideUnsafe`, `RawPtrDeclOutsideUnsafe`, `RefEscapeToGlobal`, `RefEscapeUnsafeInUnbound`.

### Fixed
- DeclarationPass attribute context validation: was hardcoded `!contexts.Contains("Safe")` (a no-op since all intrinsics include "Safe"). Now correctly validates `!contexts.Contains(safetyTier)` against the actual compilation tier. `[NoAlias]` in safe context is properly rejected (CVL1004).
- ValidationPass traversal of `UnsafeBlockStatementSyntax`: previously missing, so variables inside inline `unsafe { }` blocks were never registered in `context.VariableSymbols`, making them invisible to the SafetyPass.

### Changed
- `SafetyPass` now uses a `_currentTierStack` (Stack of `SafetyTier`) to track nested tier contexts. `CurrentTier` property peeks the stack. Functions without an explicit modifier default to `Safe`.
- `FunctionSymbol` gains `SafetyTier` property (`Safe` by default). `DeclarationPass` sets tier from modifier or `[UnsafeBody]` attribute.
- `BindingContext` gains `CurrentSafetyTier` property.
- `UnsafeOperationScanner` expanded from 2 unsafe kinds (heap allocations) to include pointer operations (dereference, address-of, casts).
- `DeclarationPass.DeclareGlobalVariable` rejects `global var ref/refvar` with clear diagnostic. Ref/refvar globals are inherently mutable.
- `GlobalLifetimeAssignOK.cvl` and `GlobalLifetimeAssignFail.cvl` updated from `global var ref` to `global ref`.

## [0.0.3-alpha] - 2026-08-26

### Added
- Lexical Last-Use Analysis (LUA): ref/refvar borrow lifetimes are now determined by forward-scanning the AST for the last read/write of the reference variable. Borrows are released as soon as no future uses exist, enabling earlier reassignment/movement of parent objects. Conservative extension for loops and conditionals (activity extends to block end). Replaced the previous block-scope-only borrow cleanup.
- Borrow Lock: when any field of an object is borrowed (`ref arr[0]`, `ref s.field`), the parent object is locked — it cannot be moved, reassigned, or returned by value while the borrow is active. The lock is released when LUA determines the borrow is dead. Array Index Locking: borrowing `arr[i]` locks the entire parent array; multiple simultaneous element borrows are rejected.
- Lifetime-Constrained Move Types (§3C): returning a struct by value is blocked if any of its reference fields point to local-origin variables. The compiler tracks which variables struct ref-fields reference (`_structRefTargets`) and validates origin on return. Cycle-Cut Rule: self-referential struct reference chains are detected via visited-set cycle detection and bounded to prevent infinite recursion.
- Global Lifetime Inequality (§3F): assigning a local-origin or parameter-origin reference to a `global var ref` variable is rejected — only global-origin references may be stored in globals, preventing dangling pointers from stack/heap outliving their source.
- Struct field provenance: `StructFieldSymbol` now carries `Origin` and `IsCycleCut` properties for tracking ref-field lifetime origins.
- 14 new Safety test cases covering borrow lock (move, reassign, return, multi-element, sequential, struct-field), lifetime-constrained return (fail and ok), cycle-cut (self-ref and transitive), global lifetime inequality (fail and ok), and codegen verification (refvar reassignment, pointer chase).

### Fixed
- Refvar reassignment codegen bug: `curr = ref next` incorrectly loaded the old pointer from the alloca and stored the new value *through* it (modifying the previous target) instead of storing the new pointer *into* the alloca. Fixed by distinguishing `BorrowExpressionSyntax` (pointer reassignment → store into alloca) from value write-through (load pointer, store through it). This was §3G's spec: reassignment compiles to `store ptr %target_address, ptr %curr_storage_alloca`.

### Changed
- `VariableSymbol.PointsToParameter` (bool) replaced with `Origin` property (`OriginKind` enum). `IsGlobal` kept for backward compatibility with CodeGenerator.
- `SafetyPass.VerifyReturnLifetime` rewritten: origin-based checking with 4 cases — `return ref expr`, `return refvar`, `return refvar` (direct variable), and struct by-value return (checking ref-field origins).
- `SafetyPass.GetBaseIdentifierName` extended to handle `IndexExpressionSyntax` (array element borrows resolve to parent variable) and `BorrowExpressionSyntax` (unwrap borrow to inner expression).
- `SafetyPass` now runs `WhileStatementSyntax` and `ForStatementSyntax` cases in `CheckStatementSafety` for proper loop-body analysis.
- 6 new Provenance test cases covering parameter-origin return, global-origin return, local-origin rejection, refvar-from-local rejection, refvar-from-parameter return, and two-hop local rejection.

## [0.0.2-alpha] - 2026-08-25

### Added
- Copy/Move Classification Engine: struct types are automatically classified as `TrivialCopy` (≤16 bytes, no destructor, no ref fields), `LargeCopy` (>16 bytes, same criteria), or `ResourceMove` (has destructor, pointer fields, or transitively contains a ResourceMove field). TrivialCopy structs copy on by-value assignment/argument passing with both sides remaining active. LargeCopy structs also copy but emit `CVL1003` performance warning. ResourceMove structs move (source invalidated after transfer). Classification is cyclic-safe and transitive. `ClassificationAnalyzer` runs lazily from the SafetyPass.
- `CVL1003` warning: `'X' is N bytes. Copying by value duplicates the payload. Consider passing by 'ref'.` Emitted at by-value function arguments, variable initializations from copied variables, and copy-assignment sites for LargeCopy structs. Suppressed via `--nowarn CVL1003` or `[SuppressWarning("CVL1003")]`. Struct literal creation does NOT trigger this warning (no copy involved).
- `SafetyPass` now uses `ResolveExpressionType` for type resolution at argument and initialization sites, enabling warnings on non-identifier expressions (function calls returning LargeCopy, etc.).
- 9 new CopyMove test cases covering TrivialCopy, LargeCopy (by-value, by-ref, init, assignment, nowarn), ResourceMove, transitive ResourceMove, and nested LargeCopy classification.

### Changed
- `SafetyPass.HandleByValueArgument` refactored to resolve expression types instead of pattern-matching on `IdentifierExpressionSyntax`. `IsMoved` still only set on named variables; copy warnings fire for any expression of LargeCopy type. `BorrowExpressionSyntax` (ref/refvar) and `StructInitializationExpressionSyntax` (struct literals) are excluded from copy warnings.
- `MemoryTests.MoveFail.cvl` rewritten to use `Resource` struct with destructor (ResourceMove type), variable renamed from `p1` to `r1`.

## [0.0.1-alpha] - 2026-08-25

### Added
- Attribute syntax `[Name]` / `[Name(args)]` on functions, extension methods, constructors, destructors, structs and parameters. Attributes are compile-time-only and fully erased before code generation. Two intrinsics ship in this milestone: `[UnsafeBody]` (marks a function/method/constructor/destructor body as unsafe for the upcoming safety pass) and `[NoAlias]` (marks reference parameters as non-aliasing; accepted only in unbound/unsafe contexts for now). `System.UnsafeBodyAttribute` / `System.NoAliasAttribute` usage rules (valid targets x valid safety contexts) are enforced by the binder with dedicated diagnostics; repeating the same intrinsic on one declaration (`[A] [A]` or `[A, A]`) is rejected as a duplicate. User-defined attributes arrive once the language has enums/inheritance.
- Compiler warnings with a new `DiagnosticSeverity` model: warnings are printed (`Analysis Warning <id>`) but never fail compilation; only errors set the exit code. Two shipped warnings: `CVL1001` — applying `[UnsafeBody]` to a body that contains no unsafe operations reports "'[UnsafeBody]' attribute has no effect because function contains no unsafe operations."; `CVL1002` — unknown attribute names are accepted and erased but flagged ("Unknown attribute 'X'; it will be ignored.") so typos stay visible. The intrinsic `[SuppressWarning("id")]` attribute (valid on all attributed declarations) silences a known warning id such as `"CVL1001"` or `"CVL1002"` — order-independent within one attribute list; unknown ids or non-string-literal arguments are compile errors. The `--nowarn` flag (see below) suppresses invocation-wide.
- Diagnostic identifiers with reserved prefix families (modeled on .NET's `CS`/`SYSLIB`/`CA` convention): `CVLxxxx` core compiler diagnostics, `SYSLIBxxxx` standard-library deprecations, plus reserved `CVLSxxxx` (system library), `CVLAxxxx` (analyzers/linters), `CVLDxxxx` (documentation checks), `CVLXxxxx` (extensions/macros), `CVLFxxxx` (FFI/C-ABI). Stable ids so far: `CVL1001` = `[UnsafeBody]` has no effect, `CVL1002` = unknown attribute ignored. New `--nowarn` flag on `build` and `run` takes a comma-separated id list (`--nowarn CVL1001,CVL9999`, case-insensitive) and drops matching warnings from the output entirely; warnings carrying an id are labeled `Analysis Warning CVL1001`.
- `null` keyword reserved by the lexer and parsed as a dedicated literal expression; any use in safe code is rejected with a clear diagnostic ("requires a pointer type") until pointer types land in the unbound/unsafe milestone.
- `global` variables: `global T name = <constant>;` declares data-segment storage with `'global` lifetime, readable from any function. Consistent with Cvolo's read-only-by-default philosophy, bare `global` (and explicit `global val`) is immutable — writes are rejected with the standard immutability diagnostic — while `global var T name` opts into shared mutable state. Initializers must be compile-time constants (literals, negations, struct literals); omitted initializers zero-initialize. Read-only globals emit as LLVM constants (.rodata candidates). Provenance/lifetime-inequality rules arrive in the next milestone.
- Struct constructors: extension blocks can declare `StructName(args) { ... }` with no return type. Called as `var FileStream stream = FileStream("logs.txt");`; the constructor populates the variable's storage in place via an implicit `this` parameter. **Defensive initialization** is enforced at bind time: every field of `this` must be assigned before the constructor exits. Name must match the extended type; constructors resolve through the existing overload machinery (registered under the struct's own name).
- C#-style destructors: extension blocks can declare `~StructName() { ... }`. The destructor body runs automatically when a value of that struct type goes out of scope (stack: block exit; heap: before `free`). Name must match the extended type; duplicate destructors across extension blocks are a compile error. Internally lowered to a `~T` void extension method, so validation, mutability inference and emission reuse existing machinery.

### Changed
- **Single-file compilation semantics**: passing an explicit `.cvl` file to `build`/`check`/`run` now compiles exactly that file (plus the standard library) instead of every sibling `.cvl` in its directory. Whole-directory loading still applies to directory and `.cvlproj` inputs.
- Extracted the ANTLR parser backend into a new `Cvolo.Syntax.Antlr` project. `Cvolo.Syntax` now contains only the parser contract (`ISyntaxParser`) and AST rewriters; ANTLR references are confined to `Cvolo.Syntax.Antlr`.
- `CompilerDriver` consumes the parser through the `ISyntaxParser` interface at the composition root.

### Fixed
- Emitter: `return <expr>` evaluated its expression *after* scope cleanup freed all locals, so any return value reading a heap-allocated local (`return data[0];`, struct returns by pointer) loaded from freed memory and produced garbage. Return values are now materialized into registers first; cleanup then runs before the actual `ret`.
- Test harness (`CompilerTestBase`): single-file `.cvl` test cases are staged into isolated per-case folders before invoking the CLI. Whole-directory compilation previously made sibling test cases (redeclaring `Main`, `Point`, etc.) poison every single-file build. Directory projects such as `Modular/App` keep whole-directory semantics.
- Test suite is green again: 63/63 passing (GenericsTests excluded separately).

### Docs
- Added `ready.md` and `release.md`; updated AGENTS.md for the new project layout.

## [0.0.1-alpha] - 2026-07-15

### Added
- Type-based function overloading with dynamic overload resolution.
- Qualified function calls and dotted namespace resolution.
- `System.Console` standard library and compile-time lowered string interpolation (`$"..."`).
- Dynamic heap arrays (`heap T[n]`) with compile-time borrow checking.
- Zero-syntax struct extension methods with mutability auto-inference.
- Compile-time `sizeof<T>()` operator.
- Automatic destructors (custom RAII) with double-free loop-hole fixes.
- AST rewriter framework and string-interpolation lowering pipeline.
- `Console.ReadLine` in the standard library.
- Parenthesized array replication with recursive target expansion.

### Fixed
- Implicit directory-level source loading and aligned recursive AST spans.
- Call-site `ref`/`refvar` pointer mutability validation and code-generation coercions.
- Missing return statement validation in non-void functions.

### Changed
- Removed obsolete IrEmitter; README updates; test cleanups.

## [0.0.0.5-alpha] - 2026-07-09

### Fixed
- Test fixes.

## [0.0.0.4-alpha] - 2026-07-06

### Added
- E2E BankSystem project test covering generics and namespaces.

## [0.0.0.3-alpha] - 2026-07-06

### Fixed
- Track base variable initialization for member assignments in flow analyzer.

## [0.0.0.2-alpha] - 2026-07-04

### Added
- Platform-specific bundled clang tooling with automated copy integration.

## [0.0.0.1-alpha] - 2026-07-04

### Changed
- Restructured output paths to bin/Debug and obj/Debug; standardized on `.cvlproj`.
