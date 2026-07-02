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

- **Grammar files:** `src/Cvolo.Syntax/Grammar/*.g4`
- **Pre-generated C#:** `src/Cvolo.Syntax/Generated/*.cs`

The generated C# files are checked into the repository so **Java is NOT required for normal builds**.

### Regenerating After Grammar Changes

If you modify the `.g4` grammar files, regenerate the parser with:

```bash
cd src/Cvolo.Syntax/Generated
java -jar /tmp/antlr-4.13.1-complete.jar -Dlanguage=CSharp -o . -visitor -listener ../Grammar/CvoloLexer.g4
java -jar /tmp/antlr-4.13.1-complete.jar -Dlanguage=CSharp -o . -visitor -listener -lib . ../Grammar/CvoloParser.g4
```

Requires Java. Verify the new files compile with `dotnet build`.

## Project Structure

| Project | Path | Role |
|---|---|---|
| **Cvolo.Core** | `src/Cvolo.Core/` | AST node types, diagnostic bag |
| **Cvolo.Syntax** | `src/Cvolo.Syntax/` | ANTLR-based lexer/parser |
| **Cvolo.Analysis** | `src/Cvolo.Analysis/` | Type checking, symbol table, borrow checker |
| **Cvolo.Emitter.LLVM** | `src/Cvolo.Emitter.LLVM/` | LLVM IR code generation (text + native) |
| **Cvolo** | `src/Cvolo/` | CLI entry point |

## Troubleshooting

| Error | Cause | Fix |
|---|---|---|
| `clang not found` | Clang not installed or not on PATH | Install LLVM, or use `--llvm` to emit `.ll` only |
| `error CS3021` | ANTLR-generated CLSCompliant attributes | Suppressed via `<NoWarn>` in `Cvolo.Syntax.csproj` |
| `Duplicate 'Compile' items` | Stale `.cs` files in `Grammar/` | Delete any `.cs` files from `src/Cvolo.Syntax/Grammar/` |
| `error MSB4018: ResolvePackageAssets failed` | NuGet cache has stale Windows paths | Run `dotnet restore` |
