# Cvolo.Compiler.Tooling — Changelog

This file records changes to the public tooling surface and its observable semantics. It ships
inside the Tooling artifact bundle under `docs/`.

## 0.0.5.3

Adds the first symbol-identity and source-navigation surface (language-server LSP-4:
hover, go-to-definition, document symbols).

### New public API

| Type / member | Purpose |
| --- | --- |
| `SymbolId` | Opaque, snapshot-scoped identity of one semantic symbol. Equality is meaningful only for ids obtained from the same `ProjectSnapshot`; the backing representation is not public API and must not be persisted across restarts. |
| `ToolingSymbolKind` | Compiler-owned classification (`Struct`, `Function`, `ExtensionMethod`, `Field`, `Parameter`, `Local`, `Global`, `EnumMember`, ...). |
| `SymbolLookupResult(SymbolId, TextSpan SubjectSpan, ToolingSymbolKind Kind, string Name, string DisplayText, string? Documentation)` | The symbol bound at a source position, its occurrence span, a compiler-owned display string, and the declaration's leading line-comment documentation (if any). |
| `SymbolDefinition(DocumentId, TextSpan Range, TextSpan SelectionSpan)` | One source declaration of a symbol. |
| `DocumentSymbolInfo(SymbolId, Name, Detail, Kind, Range, SelectionSpan, Children)` | One node of a document's declaration outline. |
| `ProjectSnapshot.GetDefinitions(SymbolId)` | Source declarations of a symbol within the snapshot. |
| `DocumentSnapshot.GetSymbolAtPosition(int)` | Resolves the symbol at a UTF-16 offset. |
| `DocumentSnapshot.GetDocumentSymbols()` | Hierarchical declaration outline for the document. |
| `CvoloWorkspace.Create(bool includeStandardLibrary = false)` | When true, opened projects also compile the discovered standard library (`libraries/`) alongside their own sources, mirroring the compiler. The bundle ships `libraries/` beside the tooling assembly. |

### Semantics

* `GetSymbolAtPosition` resolves through the compiler's own binding — the position-scoped
  local/parameter map, resolved call targets, global references, type resolution, and member
  access — never by textual name matching. Unresolvable positions return `null`.
* `GetDefinitions` returns declarations from any document in the same snapshot, including closed
  project documents; multiple legitimate declarations are all returned.
* A `SymbolId` used with a different or derived snapshot yields no results (empty list), never a
  silently unrelated symbol.
* `GetDocumentSymbols` returns declarations in deterministic source order with a name selection
  span contained in the declaration range; local variables, parameters, and anonymous syntax are
  excluded from the outline.
* `Documentation` is the contiguous `//` or `///` comment block directly above the declaration,
  with comment markers and inline `<summary>`/`</summary>` tags stripped. It is always read from
  the declaring file, including for cross-file definitions.
* All queries are pinned to the supplied immutable snapshot: no disk reread, no project reopen,
  and no cross-snapshot leakage. Concurrent queries are deterministic.
* With `includeStandardLibrary: true`, standard-library declarations (e.g. `System.Console`) are
  part of the snapshot, so stdlib/package APIs resolve instead of producing false overload
  errors. Closed standard-library documents participate in navigation but are never edited.

### Internal (compiler)

* New `Cvolo.Analysis.Semantics` namespace: `ResolvedSymbol`, `SymbolResolver`, `DeclarationIndex`.
* New `CompletionQuery.ResolveSymbol` / `CompletionQuery.DescribeDeclaration` entry points.
* `ScopedVariable` now carries its declaring syntax node so locals and parameters can be mapped
  back to their declarations.

## Earlier revisions

Versions before `0.0.5.3` predate this changelog. In summary:

* `0.0.5` / `0.0.5.1` — completion foundation: `CompletionResult`, `CompletionCandidate`,
  `CompletionKind`, and `DocumentSnapshot.GetCompletions`.
* `0.0.5.2` — completion hardening: bare-`.` member recovery, partially typed top-level
  declaration keywords, and `~Type()` destructor snippets (`CompletionCandidate.IsSnippet`).
