# 🦅 Cvolo Programming Language

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![ANTLR](https://img.shields.io/badge/Parser-ANTLR_4-red?logo=antlr)](https://www.antlr.org/)
[![LLVM](https://img.shields.io/badge/Backend-LLVM-6A1B9A?logo=llvm)](https://llvm.org/)

**Cvolo** is a compiled systems programming language designed to combine the performance and memory safety of languages like C/Rust with the familiar, clean syntax of C#.

Writing low-level code or performance-critical systems often forces developers to choose between the syntax ergonomics of high-level languages (like C# or Java) and the raw power of GC-free execution (like C++ or Rust). Cvolo bridges this gap. By implementing compile-time ownership tracking instead of a heavy runtime Garbage Collector, Cvolo provides deterministic memory management without sacrificing developer productivity.

> [!NOTE]
> The name "Cvolo" is a combination of the letter **C** (representing its ancestry and C#-style syntax) and **Volo** (Latin for "to fly" or "to wish"), representing speed, freedom from runtime overhead, and intent.

## ✨ Key Features

- 🛠️ **C#-Like Syntax:** Clean, structured, and highly intuitive for developers coming from the .NET ecosystem.
- 🚫 **No Garbage Collector:** Low-level memory control with zero runtime pause times.
- 🛡️ **Compile-Time Memory Safety:** An ownership and borrowing model enforced by the compiler to prevent data races, use-after-free, and double-free bugs.
- 🔄 **Explicit Mutability Control:** Clear distinction between immutable values (`val`) and mutable variables (`var`).
- 🔗 **Explicit Borrowing:** Controlled referencing utilizing `ref Type` (read-only borrow) and `refvar Type` (writable borrow) patterns.
- ⚡ **Native Compilation:** Powered by LLVM, compiling directly to optimized native binaries.
- 🧩 **Zero-Allocation String Interpolation:** Resolves and lowers string interpolations to sequential write streams at compile-time.
- 🏗️ **Struct Extension Blocks:** Out-of-line method definitions separating data layout from behaviors, keeping structures safe and lightweight.

---

## 📝 Syntax Preview

| Category | Cvolo Syntax | Description | Ownership Semantics |
| :--- | :--- | :--- | :--- |
| **Immutable Variable** | `val x = 5;` <br> `val int x = 5;` | Declares an immutable variable (value cannot be reassigned). | The variable owns the resource. Safe default. |
| **Mutable Variable** | `var y = 10;` <br> `var int y = 10;` | Declares a mutable variable. | The owner can modify the value. |
| **Ownership Transfer (Move)** | `val s2 = s1;` | Transfers ownership of a resource from `s1` to `s2`. | `s1` becomes invalid (unusable and inaccessible). |
| **Immutable Reference** | `ref Point r = ref x;` | Borrows a value as read-only (equivalent to `&T` in Rust). | Allows reading the data without transferring ownership. |
| **Mutable Reference** | `refvar Point r = ref y;` | Borrows a value with write access (equivalent to `&mut T` in Rust). | Allows modifying the data. Only one active mutable reference allowed. |
| **Array Replication** | `var int[100] numbers(42);` | Initializes a static array of 100 elements filled with `42`. | Allocates on the stack and generates an LLVM store loop. |
| **Struct Replication** | `var Point[3] points(X: 10, Y: 20);` | Replicates and initializes 3 Points with recursive target-typing. | Emits inline structural constructions inside a GEP loop. |
| **String Interpolation** | `Console.WriteLine($"X: {p.X}");` | Zero-allocation, compile-time lowered string formatting. | Desugars directly into sequential overloaded writes at compile-time. |
| **Extension Blocks** | `extension Point { void Move() { ... } }` | Attaches behavior methods to an existing struct out-of-line. | Compiler automatically infers method mutability (`ref` vs `refvar`). |
| **Entry Point** | `int main() { ... }` | The main entry point of the program. | Starts program execution. |

Here is a quick look at how Cvolo combines C#-like aesthetics with native memory safety concepts:

```csharp
using System;

struct Color {
    int R;
    int G;
    int B;
}

struct Point {
    int X;
    int Y;
    Color BaseColor;
}

extension Point {
    // Compiler automatically infers this method mutates 'X' and 'Y' (requires refvar)
    void Move(int dx, int dy) {
        X = X + dx;
        Y = Y + dy;
    }

    // Compiler automatically infers this method is read-only (requires ref)
    void Print() {
        Console.WriteLine($"Point: X = {X}, Y = {Y}, Color R = {BaseColor.R}");
    }
}

int main() {
    // Immutable variable
    val limit = 100; 

    // Initialize struct using parenthesized target-typed constructors
    var Point myPoint(
        X: 10, 
        Y: 20, 
        BaseColor: (R: 255, G: 0, B: 0)
    ); 

    // Mutate via extension call
    myPoint.Move(5, 5); 

    // Read-only borrow print
    myPoint.Print(); 

    // Single-object heap allocation with RAII cleanup
    var Point p2 = heap Point { X: 100, Y: 200, BaseColor: (R: 0, G: 0, B: 0) };
    Console.WriteLine($"Heap single point X: {p2.X}");

    // Dynamic heap array allocation with RAII cleanup
    var int[] buffer = heap int[limit];
    buffer[0] = 42;

    return 0;
}
```

---

## 🛠 Compiler Architecture ("The Sandwich")

The Cvolo compiler is structured as a pipeline, leveraging established tools for parsing and code generation, while implementing the core type-checking and borrow-checking logic in C#.

```
 [Source Code .cv] 
         │
         ▼
 ┌───────────────┐
 │ ANTLR Frontend│ ──► Parses grammar and builds the AST (Abstract Syntax Tree)
 └───────────────┘
         │
         ▼
 ┌───────────────┐
 │  C# Middle    │ ──► Performs Type Checking & Symbol Table resolution
 │   (Compiler)  │ ──► Evaluates Ownership & Borrowing rules
 └───────────────┘
         │
         ▼
 ┌───────────────┐
 │ LLVM Backend  │ ──► Generates LLVM IR via LLVMSharp
 └───────────────┘
         │
         ▼
 [ Native Binary ] (.exe / elf)
```

1. **Frontend (ANTLR):** Converts the source text into an Abstract Syntax Tree (AST) using a formally defined grammar.
2. **Middle-end (C#):** The core of the compiler. It builds the Symbol Table, checks types, and executes the borrow checker to ensure memory safety before any machine code is produced.
3. **Backend (LLVM via LLVMSharp):** Translates the verified AST into LLVM Intermediate Representation (LLVM IR), letting LLVM perform native optimizations and output target-specific machine code.

---

## 🚀 Quick Start (Compiler Setup)

> [!WARNING]
> 🚧 **WIP Notice:** The compiler is in its early experimental phase. Direct compilation of complex programs is under active development.

### Prerequisites
* [.NET 10 SDK](https://dotnet.microsoft.com/download)
* LLVM (installed on your system and accessible in PATH)

### Building the Compiler
1. Clone the repository:
   ```bash
   git clone https://github.com/YourUsername/cvolo.git
   cd cvolo
   ```
2. Build the project:
   ```bash
   dotnet build
   ```

---

## 🗺 Roadmap

The development of Cvolo is divided into progressive phases, moving from basic system capability to advanced safety guarantees.

### Phase 1: "Hello World" & Toolchain 🛠️
- [x] Configure C# compiler host project with **LLVMSharp**.
- [x] Implement external system calls (linking and calling system `printf`).
- [x] Generate the first working native executable binary.

### Phase 2: Language Basics
- [x] Establish ANTLR grammar for variables and basic operations.
- [x] Core variables: Implement `val` and `var` syntax definitions.
- [x] Implement primitive types (`int`, `double`, `bool`) and arithmetic operations.
- [x] Build the initial Symbol Table to keep track of variables in scopes.

### Phase 3: Control Flow
- [x] Add support for conditional statements (`if` / `else`).
- [x] Add support for loops (`while` and C#-style `for` loops) via LLVM basic blocks.
- [x] Implement nested scopes and lexical scoping rules in the Symbol Table.

### Phase 4: Ownership & Borrow System
- [x] Implement Move semantics (forbidding the use of a variable after its value has been moved).
- [x] Implement reference syntax (`ref` and `refvar`).
- [x] Build the compile-time Borrow Checker to validate reference lifetimes and prevent multiple mutable borrows.

### Phase 5: Complex Types & Structures
- [x] Support custom user-defined structures (`struct`).
- [x] Support member access and struct initialization syntax.
- [x] Implement basic memory allocation on the stack and heap (`malloc`-based heap allocation).

### Phase 6: Advanced Systems Ergonomics (Upcoming `v0.0.1-alpha` Release)
- [x] Type-Based Function Overloading
- [x] Standard Library Auto-Discovery and `System.Console` namespace
- [x] Compile-Time Zero-Allocation String Interpolation
- [x] Parenthesized Array Replication & Custom Constructors
- [x] Struct Extension Blocks (`extension`)
- [x] Dynamic Heap Arrays (`heap T[count]`)
- [ ] Compile-Time `sizeof(T)` Operator
- [ ] RAII Custom Destructors (`void Dispose()`)
- [ ] Standard Library Interactive Input (`Console.Read` / `Console.ReadLine`)

---

## 🤝 Contributing

Contributions to Cvolo are welcome! If you are interested in compiler design, ANTLR grammars, or working with LLVM, feel free to check out our open issues or submit a pull request. 

Please read the [Grammar Specification](docs/GRAMMAR.md) before making changes to the parser.
