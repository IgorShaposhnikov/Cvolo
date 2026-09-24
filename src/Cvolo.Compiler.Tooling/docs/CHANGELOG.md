# Cvolo.Compiler.Tooling — Changelog

This file records changes to the public tooling surface and its observable semantics. It ships
inside the Tooling artifact bundle under `docs/`.

## 0.0.5.6

Hardens LSP-6 rename planning.

- rejects renames that introduce new compiler errors, including duplicate matching signatures;
- preserves valid same-name overloads when their signatures remain distinct;
- keeps rename planning snapshot-pure and all-or-nothing.

## 0.0.5.5

Adds project-semantic references and compiler-owned rename planning for LanguageServer LSP-6.

### New public API

| Type / member | Purpose |
| --- | --- |
| `SymbolReference` | One project-source occurrence of a snapshot-scoped symbol, including whether it is a declaration. |
| `ProjectSnapshot.GetReferences(SymbolId, bool)` | Enumerates semantic occurrences across the immutable project snapshot. |
| `RenamePreparation` / `DocumentSnapshot.PrepareRename(int)` | Validates an exact source occurrence and returns its compiler-owned placeholder. |
| `RenameEdit`, `RenameSuccess`, `RenameFailure` | Snapshot-pure semantic rename planning result. |
| `ProjectSnapshot.RenameSymbol(SymbolId, string)` | Produces one complete validated edit set or a semantic failure without mutating the snapshot. |

### Semantics

* Reference discovery scans project source occurrences but accepts them only after compiler-backed symbol resolution matches the requested snapshot-scoped `SymbolId`; comments and string contents are never textual references.
* `includeDeclaration` is preserved by Tooling rather than reconstructed by the protocol layer.
* External/package symbols may have project-local references but remain non-renameable when no editable project declaration exists.
* Rename validates Cvolo identifier syntax and speculatively rebinds every edited occurrence in a derived immutable snapshot. A collision or rebinding rejects the complete plan rather than returning partial edits.
* Rename is pure: no source file, workspace, or input `ProjectSnapshot` is mutated.

## 0.0.5.4

Completes the native-package tooling increment: imported native ABI declarations now participate in
completion, semantic highlighting, hover/navigation identity, and compiler-backed signature help.

### New public API

| Type / member | Purpose |
| --- | --- |
| `NativeInteropKind` / `NativeInteropMetadata` | Structured native delegate, raw-union, and foreign-global metadata for editor hover/details without reparsing declaration attributes. |
| `SymbolLookupResult.NativeInterop` | Optional native-interop metadata attached to resolved symbols. |
| `SignatureHelpParameter`, `SignatureHelpItem`, `SignatureHelpResult` | Protocol-neutral signature-help DTOs. |
| `DocumentSnapshot.GetSignatureHelp(int)` | Returns the binder-resolved ordinary-function or nominal-delegate signature at a UTF-16 cursor offset. |

### Semantics

* Package API declarations reconstructed from `.cvlib` metadata receive snapshot-local `SymbolId`s,
  so hover and semantic navigation can resolve them even when the package ships no source.
  `GetDefinitions` remains empty for those symbols rather than fabricating a local document location.
* Public native delegates preserve `IsNative` and their calling convention in hover and signature
  help. Raw unions retain their raw/unsafe identity.
* Imported foreign globals expose calling convention, import symbol, library name, and target-specific
  library paths reconstructed from package metadata or inherited extern-block binding metadata.
* Extern-block globals participate in document navigation outlines and source go-to-definition.
* Signature help uses the compiler's resolved function/delegate target and counts nested argument
  delimiters when selecting the active parameter.

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
| `CvoloWorkspace.Create()` | Opened projects now build the same semantic universe as the compiler by default: project sources, the discovered standard library, merged `ProjectReference` sources and package/`.cvlib` API units. |

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
* The project universe is built by the shared `Cvolo.Projects` layer used by both the compiler
  driver and the tooling, so the compiler and the editor observe one authoritative set of
  project sources, standard-library sources, `ProjectReference`s, package/`.cvlib` API units and
  configuration. Standard-library/package declarations resolve (no false overload errors) and
  source-backed declarations keep real source definitions; compiled package units that lack
  source metadata never fabricate a definition location.

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