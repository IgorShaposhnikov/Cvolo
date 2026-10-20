# Instrumentation map

This map lists every observability instrumentation site in the compiler and tooling
repositories: where the event is produced, at which depth, which outcome/reason vocabulary it
uses, and which automated test proves it. It is the human-readable companion to the event catalog
in `Cvolo.Observability/EventCatalog.cs` and the requirements traceability matrix.

Depth levels follow the specification:

- **L0 / boundary** — every public compiler/tooling entry point, pipeline transition, error,
  outcome, version and duration. Recorded in `basic` mode and above.
- **L1 / phase** — expensive internal stages (parser, binder passes, index construction, symbol
  resolution, feature execution). Recorded in `detailed` mode and above.
- **L2 / targeted forensic** — bounded, on-demand detail for one symbol/operation. Not yet used by
  a production path.

Status values are honest (`covered`, `partial`, `planned`); a stage is never marked `covered`
unless it both measures time and records an outcome.

## Runtime and session

| Area | Level | Site | Events | Status |
|---|---|---|---|---|
| Session lifecycle | L0 | `Cvolo/CliObservabilitySession.cs` (`Start`, `Dispose`) | `session.started`, `session.ended` | covered |
| Recording transitions | L0 | `Cvolo.Observability/CompilerObserver.cs` (`SetMode`) | `recording.mode_changed` | covered |
| Span terminals | L0 | `Cvolo.Observability/OperationScope.cs` (`Complete`, `Dispose`) | `<stage>.completed`, `.faulted`, `.cancelled`, `operation.*` | covered |
| Queue loss | L0 | `Cvolo.Observability/BoundedEventQueue.cs` (`TryEnqueue`) | loss counters + `trace.events_dropped` | covered |
| Writer durability | L0 | `Cvolo.Observability/JsonlEventWriter.cs` (`Flush`, `Dispose`) | durable sequence checkpoint, tail flush | covered |

Tests: `Cvolo.Tests.Observability/OperationLifecycleTests.cs`, `QueueAndWriterTests.cs`,
`RotationAndCorruptionTests.cs` (including `Dispose_flushes_a_tail_enqueued_after_the_background_loop_has_exited`),
`ModeTransitionTests.cs`, and `Cvolo.Tests/CliObservabilityTests.cs`.

## Workspace, snapshots and shared lazy values

| Area | Level | Site | Events | Status |
|---|---|---|---|---|
| Shared lazy factories | L1 | `Cvolo.Compiler.Tooling/Internal/ObservedLazy.cs` (`Value`) | `lazy.started_by_consumer`, `lazy.waited_for_existing`, `lazy.reused_completed`, `lazy.failed_cached` | covered |
| Snapshot identity | L0 | `Cvolo.Compiler.Tooling/Workspace/ProjectSnapshot.cs` (constructor) | snapshot id allocated only when enabled; carried on every snapshot event | covered |
| Observer propagation | — | `CvoloWorkspace.Create(observer)` → `CvoloProject` → `ProjectSnapshot.WithDocument` | observer reference threaded, never a static | covered |

Lazy kinds currently instrumented: `analysis`, `navigation`, `code_fixes`, `occurrences`,
`parsed_units`.

Tests: `Cvolo.Tests.Tooling/ObservabilityLazyTests.cs`,
`Cvolo.Tests.Tooling/ConcurrentObservabilityTests.cs` (winner/waiter classification, cached failure,
shared-computation single execution).

## Parsing and binding

| Area | Level | Site | Events | Status |
|---|---|---|---|---|
| Document parse | L1 | `Internal/ParserAdapter.cs` (`ParseDocument` overload with observer) | `parse.document.started`, `parse.document.completed` (error counts) | covered |
| Project parse | L1 | `Internal/ParserAdapter.cs` (`ParseAll`) | `parse.project.started`, `parse.project.completed` | covered |
| Binder stage | L1 | `Internal/BinderAdapter.cs` (`AnalyzeSnapshot`) | `bind.project.started/completed/faulted` | covered |
| Binder passes | L1 | `Cvolo.Analysis/Binder.cs` (`Bind` overload) → `BinderAdapter` | `bind.pass.started/completed/faulted` for `declaration`, `validation`, `flow`, `safety`, `ownership` | covered |
| Suppressed diagnostics | L1 | `Internal/BinderAdapter.cs` | `bind.diagnostics_suppressed` | covered |
| Degraded analysis | L1 | `Internal/BinderAdapter.cs` | `analysis.degraded`, `analysis.faulted` | covered |

Tests: `Cvolo.Tests.Tooling/AnalysisInstrumentationTests.cs`.

## Navigation and symbol resolution

| Area | Level | Site | Events | Status |
|---|---|---|---|---|
| Declaration index | L1 | `Internal/NavigationService.cs` (`NavigationIndex` constructor) | `declaration_index.started/completed` | covered |
| Symbol resolution | L1 | `NavigationService.cs` (`NavigationIndex.Lookup`) | `symbols.resolve.started/completed/faulted` | covered |
| Resolution decision | L1 | same | `decision.made` (outcome, miss reason, resolved kind, conformance redirect) | covered |
| Definition mapping | L0 | `NavigationService.cs` (`NavigationService.GetDefinitions`) | `navigation.definition.started/completed/faulted` | covered |
| Package source map | L1 | `NavigationService.cs` (`GetPackageSourceDefinitions`) | `navigation.source_map` | covered |
| Rejected mapping | L1 | `NavigationService.cs` (`Definitions`) | `definition.mapping.rejected` (`foreign_snapshot`, `unknown_symbol`) | covered |

Tests: `Cvolo.Tests.Tooling/NavigationAndClassificationInstrumentationTests.cs`.

## Analysis locks

| Area | Level | Site | Events | Status |
|---|---|---|---|---|
| Binder-context lock | L1 | `Internal/ObservedBinderLock.cs` (`Run`) | `lock.wait.started/completed`, `lock.hold.completed` | covered |

Used by `NavigationIndex.Lookup`, `SemanticTokenService.Compute`, `CompletionService.RunQuery`,
`SignatureHelpService.Compute`. Lock ordering is unchanged: the observed helper wraps the existing
`lock`, never replaces it with a different discipline.

Tests: `NavigationAndClassificationInstrumentationTests.cs`,
`ConcurrentObservabilityTests.cs` (`Binder_lock_reports_wait_and_hold_times_when_a_waiter_blocks`,
`Reentrant_binder_lock_on_the_same_gate_completes_without_deadlock`).

## Editor features (public tooling entry points)

| Area | Level | Site | Events | Status |
|---|---|---|---|---|
| Semantic classification | L1 | `Internal/SemanticTokenService.cs` (`Compute`) | `classification.document.started/completed`, `semantic_tokens.encoded` | covered |
| Completion | L0 | `Internal/CompletionService.cs` (`Compute` wrapper) | `completion.document.started/completed/faulted` | covered |
| Completion recovery decision | L1 | `CompletionService.cs` (`EmitRecoveryDecision`) | `decision.completion.recovery` (parse status, applied probe/fallback, query context, candidate count) | covered |
| Signature help | L0 | `Internal/SignatureHelpService.cs` (`Compute` wrapper) | `signature_help.document.started/completed/faulted` | covered |
| Document symbols | L0 | `NavigationService.cs` (`GetDocumentSymbols`) | `document_symbols.started/completed/faulted` | covered |
| References, rename | L0 | `Internal/ReferenceRenameService.cs` | — | planned |
| Hover / highlights | L0 | `Internal/DocumentHighlightService.cs` | — | planned |
| Code actions | L0 | `Internal/CodeFixService.cs` | — | planned |
| Code lens | L0 | `Internal/CodeLensService.cs` | — | planned |
| Folding, selection | L0 | `Internal/FoldingRangeService.cs`, `SelectionRangeService.cs` | — | planned |
| Inlay hints | L0 | `Internal/InlayHintService.cs` | — | planned |
| Type hierarchy, layout | L0 | `Internal/TypeHierarchyNavigation.cs`, `TypeLayoutAdapter.cs` | — | planned |
| Diagnostics collection | L1 | `Internal/DiagnosticAdapter.cs` | — | planned |

Notes:

- `document_symbols.*` is an instrumentation family introduced for the document-symbols entry
  point; the specification's minimum stage list names the expensive analysis stages but requires
  L0 coverage of every public tooling entry point, which this event satisfies.
- Hot code-lens internals (`LookupOccurrence`, `TypeAt`) are deliberately not instrumented; they
  are called per visible line and would dominate the trace without adding decision evidence.
- Remaining `planned` rows are honest gaps, not silent omissions; they are tracked in the
  requirements traceability matrix and must reach `covered` before release acceptance.

Tests: `Cvolo.Tests.Tooling/CompletionAndSignatureInstrumentationTests.cs`,
`NavigationAndClassificationInstrumentationTests.cs`, `ConcurrentObservabilityTests.cs`.

## Command-line compiler

| Area | Level | Site | Events | Status |
|---|---|---|---|---|
| CLI root operation | L0 | `Cvolo/CliHost.cs` (`Run`) | `compiler.invocation.started/completed/faulted` (arg count, process id, exit code) | covered |
| CLI session | L0 | `Cvolo/CliObservabilitySession.cs` | `session.started`, `session.ended` | covered |
| CLI pipeline stages (parse/bind/emit scopes) | L1 | `Cvolo.Drivers/CompilerDriver.cs` | — | planned |

The CLI writes a trace only when `CVOLO_TRACE_PATH` is set; `CVOLO_OBS_MODE` selects the mode
(`off`, `basic`, `detailed`, `repro`), defaulting to `basic`. A test collector can inject any
`ICompilerObserver`, so the same event stream is observable in memory without disk I/O. Process
exit codes and stdout are unchanged with telemetry on or off; commands return their exit code
instead of terminating the process so the root span always closes with the real outcome.

Tests: `Cvolo.Tests/CliObservabilityTests.cs`.

## Catalog validation

Every event family listed above is registered in the kernel catalog with its allowed outcomes,
allowed reasons and required fields. Tests attach `EventCatalog.CreateKernelCatalog()` to the
observer and assert `UnknownEvents`, `UnknownOutcomes`, `UnknownReasons` and `MissingFields` are
zero after exercising the whole flow:

- `All_navigation_and_classification_events_satisfy_the_kernel_catalog`
- `All_feature_events_satisfy_the_kernel_catalog`
- `Interleaved_multithreaded_work_publishes_balanced_spans_with_no_catalog_violations`
