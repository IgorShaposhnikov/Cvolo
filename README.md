# 🦅 Cvolo Programming Language

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![ANTLR](https://img.shields.io/badge/Parser-ANTLR_4-red?logo=antlr)](https://www.antlr.org/)
[![LLVM](https://img.shields.io/badge/Backend-LLVM-6A1B9A?logo=llvm)](https://llvm.org/)

**Cvolo** is a compiled system programming language designed to combine the performance and memory safety of languages like C/Rust with the familiar, clean syntax of C#.

Writing low-level code or performance-critical systems often forces developers to choose between the syntax ergonomics of high-level languages (like C# or Java) and the raw power of GC-free execution (like C++ or Rust). Cvolo aims to bridge this gap. By implementing compile-time ownership tracking instead of a heavy runtime Garbage Collector, Cvolo provides deterministic memory management without sacrificing developer productivity.

> [!NOTE]
> The name "Cvolo" is a combination of the letter **C** (representing its ancestry and C#-style syntax) and **Volo** (Latin for "to fly" or "to wish"), representing speed, freedom from runtime overhead, and intent.

## ✨ Key Features

- 🛠️ **C#-Like Syntax:** Clean, structured, and highly intuitive for developers coming from the .NET ecosystem.
- 🚫 **No Garbage Collector:** Low-level memory control with zero runtime pause times.
- 🛡️ **Compile-Time Memory Safety:** An ownership and borrowing model enforced by the compiler to prevent data races, use-after-free, and double-free bugs.
- 🔄 **Explicit Mutability Control:** Clear distinction between immutable values (`val`) and mutable variables (`var`).
- 🔗 **Explicit Borrowing:** Controlled referencing utilizing `ref val` (read-only borrow) and `ref var` (writable borrow) patterns.
- ⚡ **Native Compilation:** Powered by LLVM, compiling directly to optimized native binaries.

---

## 📝 Syntax Preview

| Category | Cvolo Syntax | Description | Ownership Semantics |
| :--- | :--- | :--- | :--- |
| **Immutable Variable** | `val x = 5;` <br> `val int x = 5;` | Declares an immutable variable (value cannot be reassigned). | The variable owns the resource. Safe default. |
| **Mutable Variable** | `var y = 10;` <br> `var int y = 10;` | Declares a mutable variable. | The owner can modify the value. |
| **Ownership Transfer (Move)** | `val s2 = s1;` | Transfers ownership of a resource from `s1` to `s2`. | `s1` becomes invalid (unusable and inaccessible). |
| **Immutable Reference** | `ref val r = ref x;` | Borrows a value as read-only (equivalent to `&T` in Rust). | Allows reading the data without transferring ownership. Unlimited read-only references allowed. |
| **Mutable Reference** | `ref var r = ref y;` | Borrows a value with write access (equivalent to `&mut T` in Rust). | Allows modifying the data. Only one active mutable reference is allowed at a time. |
| **Function Signature** | `int Add(val int a, ref var int b)` | Defines a function using C#-style syntax. | `a` is passed by value (copied/moved), `b` is passed by mutable reference. |
| **Conditional Statement** | `if (x > 0) { ... } else { ... }` | Conditional branching. | Parentheses are required, identical to C#. |
| **While Loop** | `while (x > 0) { ... }` | Pre-test loop. | Standard behavior. |
| **For Loop** | `for (var int i = 0; i < 10; i++) { ... }` | Loop with a counter. | The counter `i` is declared using `var` because its value is modified on each iteration. |
| **Structures** | `struct Point {`<br>`  int X;`<br>`  int Y;`<br>`}` | Declares a custom data structure. | Passed by value (Move semantics) unless marked as a Copy type. |
| **Structure Methods** | `struct Rect {`<br>`  int W; int H;`<br>`  int Area() { return W * H; }`<br>`}` | Structure containing a method. | The method has direct access to the fields of the structure. |
| **Entry Point** | `void Main() { ... }` | The main entry point of the program. | Starts program execution. |
| **External Functions** | `extern void printf(string format, ...);` | Imports external C-functions for the linker. | Used during Phase 1 for basic console output. |

Here is a quick look at how Cvolo combines C#-like aesthetics with native memory safety concepts:

```csharp
struct Point {
    int X;
    int Y;
}

// Accepts a read-only reference (borrow)
void PrintPoint(ref val Point p) {
    // p.X = 10; // Compile error: p is read-only (ref val)
    print("Point position: %d, %d\n", p.X, p.Y);
}

void Main() {
    // Immutable by default
    val limit = 100; 

    // Mutable struct instance
    var Point myPoint = Point { X: 10, Y: 20 }; 

    // Create a mutable reference
    ref var Point pointRef = ref myPoint;
    pointRef.X = 50; // Allowed

    PrintPoint(ref myPoint);
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
- [ ] Configure C# compiler host project with **LLVMSharp**.
- [ ] Implement external system calls (linking and calling system `printf`).
- [ ] Generate the first working native executable binary.

### Phase 2: Language Basics (In Progress 🚧)
- [ ] Establish ANTLR grammar for variables and basic operations.
- [ ] Core variables: Implement `val` and `var` syntax definitions.
- [ ] Implement primitive types (`int`, `double`, `bool`) and arithmetic operations.
- [ ] Build the initial Symbol Table to keep track of variables in scopes.

### Phase 3: Control Flow
- [ ] Add support for conditional statements (`if` / `else`).
- [ ] Add support for loops (`while` and C#-style `for` loops) via LLVM basic blocks.
- [ ] Implement nested scopes and lexical scoping rules in the Symbol Table.

### Phase 4: Ownership & Borrow System
- [ ] Implement Move semantics (forbidding the use of a variable after its value has been moved).
- [ ] Implement reference syntax (`ref val` and `ref var`).
- [ ] Build the compile-time Borrow Checker to validate reference lifetimes and prevent multiple mutable borrows.

### Phase 5: Complex Types & Structures
- [ ] Support custom user-defined structures (`struct`).
- [ ] Support member access and struct initialization syntax.
- [ ] Implement basic memory allocation on the stack and heap (`malloc`-based heap allocation for complex objects).

---

## 🤝 Contributing

Contributions to Cvolo are welcome! If you are interested in compiler design, ANTLR grammars, or working with LLVM, feel free to check out our open issues or submit a pull request. 

Please read the [Grammar Specification](docs/GRAMMAR.md) before making changes to the parser.
