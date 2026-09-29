# 🛠 Cvolo Compilation Guide

## Prerequisites

- **.NET 10 SDK** — required to build and run the compiler
- **LLVM / Clang** — required for native `.exe`/`.dll` output. Install from [llvm.org](https://github.com/llvm/llvm-project/releases) and ensure `clang` is on your PATH.

## Building the Compiler

```bash
cd src
dotnet build
```

The compiler CLI is at `src/Cvolo/`.

## Compilation Pipeline

```
.cv → [Syntax Parser] → AST → [Type Checker] → AST → [IrEmitter] → .ll → [clang] → .exe
```

## Output Modes

All modes accept a `.cv` source file as the first argument.

| Flags | Output | Requires | Description |
|---|---|---|---|
| *(none)* | `.exe` | clang | Default: compile and link to executable |
| `--llvm` | `.ll` | none | Generate LLVM IR only, skip linking |
| `--shared` | `.dll` / `.so` | clang | Build as shared library instead of executable |
| `--emit-ir` | stdout | none | Print LLVM IR to console (combinable) |
| `--emit-native` | `.exe` | libLLVM + clang | Use LLVMSharp native codegen instead of text IR |

**Examples:**

```bash
# Default: .ll → .exe (requires clang)
dotnet run --project src\Cvolo -- program.cv

# IR only, no linking needed
dotnet run --project src\Cvolo -- program.cv --llvm

# IR only + print to stdout
dotnet run --project src\Cvolo -- program.cv --llvm --emit-ir

# Shared library
dotnet run --project src\Cvolo -- program.cv --shared
```

Flags can be combined (e.g. `--llvm --emit-ir`).

## ANTLR Parser Generation

The compiler uses ANTLR 4.13.1 for lexing and parsing.

- **Grammar files:** `src/Cvolo.Syntax.Antlr/Grammar/*.g4`
- **Generated C#:** produced at build time by `Antlr4BuildTasks` under `src/Cvolo.Syntax.Antlr/obj/`; nothing is checked in.

Modify the `.g4` grammar files and rebuild with `dotnet build`; the generated parser and lexer are regenerated automatically (no separate Java step required).

## SDK Layers (Base and System)

The SDK source lives in `libraries/`:

| Layer | Path | Loading | Contents |
|---|---|---|---|
| **Base** | `libraries/Base/` | implicit, always present | `Option<T>`, `Result<T,E>`, `Type`, compiler-recognized `*Attribute` markers, and the `builtin` declaration anchors for compile-time operators |
| **System** | `libraries/System/` | import-driven (`using System...;` or qualified `System.*`) | `System.Console`, `System.Math`, and other hosted namespaces |

The loader walks up from the input to find a `libraries/` directory containing `Base/` and
`System/`. A local tree that contains the input is authoritative over the compiler's bundled
copy, and the two are never merged (so editing an authoritative `Base`/`System` source never
yields duplicate declarations). `--freestanding` (or `<Freestanding>true</Freestanding>`)
compiles with Base only and rejects any System dependency (`CVL1097`).

## Project Structure

| Project | Path | Role |
|---|---|---|
| **Cvolo.Core** | `src/Cvolo.Core/` | AST node types, diagnostic bag |
| **Cvolo.Core.Packages** | `src/Cvolo.Core.Packages/` | `.cvlib` container format |
| **Cvolo.Syntax** | `src/Cvolo.Syntax/` | AST rewriters and shared syntax services |
| **Cvolo.Syntax.Antlr** | `src/Cvolo.Syntax.Antlr/` | ANTLR-based lexer/parser and SDK library resolver |
| **Cvolo.Analysis** | `src/Cvolo.Analysis/` | Type checking, symbol table, borrow checker, builtins |
| **Cvolo.Projects** | `src/Cvolo.Projects/` | Project/universe loading, project references, explicit libraries |
| **Cvolo.Packaging** | `src/Cvolo.Packaging/` | Package build/restore, `.cvlib` packing |
| **Cvolo.Emitter.LLVM** | `src/Cvolo.Emitter.LLVM/` | LLVM IR code generation (text + native) |
| **Cvolo.Compiler.Tooling** | `src/Cvolo.Compiler.Tooling/` | Editor-facing workspace/navigation/completion API |
| **Cvolo** | `src/Cvolo/` | CLI entry point |

## Troubleshooting

| Error | Cause | Fix |
|---|---|---|
| `clang not found` | Clang not installed or not on PATH | Install LLVM, or use `--llvm` to emit `.ll` only |
| `error CS3021` | ANTLR-generated CLSCompliant attributes | Suppressed via `<NoWarn>` in `Cvolo.Syntax.csproj` |
| `Duplicate 'Compile' items` | Stale `.cs` files in `Grammar/` | Delete any `.cs` files from `src/Cvolo.Syntax/Grammar/` |
| `error MSB4018: ResolvePackageAssets failed` | NuGet cache has stale Windows paths | Run `dotnet restore` |
