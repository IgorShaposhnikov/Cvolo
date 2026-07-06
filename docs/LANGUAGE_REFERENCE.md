# 🦅 Cvolo Language Reference & Developer Guide

Welcome to **Cvolo**! Cvolo is a compiled, high-performance systems programming language designed to combine the clean syntax and ergonomics of C# with the deterministic, compile-time memory safety of Rust.

---

## 1. Type System

Cvolo features a strong, static type system divided into primitives, user-defined structures, references, and collections.

### Primitives
*   **`int`**: Signed 32-bit integer.
*   **`double`**: 64-bit floating-point number.
*   **`bool`**: 1-bit boolean (`true` or `false`).
*   **`char`**: 8-bit character/byte.
*   **`void`**: Represents the absence of a value (only used as a function return type).

### User-Defined Structures (`struct`)
Structures are contiguous blocks of memory containing named fields. They have no hidden runtime overhead, class headers, or virtual tables.
```csharp
struct Point {
    int X;
    int Y;
}

struct Line {
    Point Start;
    Point End;
}
```
*   **Nesting:** Structures can natively nest inside other structures, as seen in `Line` above.
*   **Pass-by-Value:** Passing a structure to a function without reference modifiers copies the data.

### Reference / Pointer Types (Borrows)
References allow you to point to a memory location without copying its contents.
*   **`ref Type`**: An immutable (read-only) reference. The target data cannot be modified through this reference.
*   **`refvar Type`**: A mutable (writable) reference. Allows you to modify the underlying data in-place.

---

## 2. Variables & Mutability

Cvolo is **secure-by-default**. All variables are immutable unless explicitly marked otherwise.

### Explicit Variable Declarations
*   **Immutable (Default):** Declaring a variable by writing its type directly makes it read-only.
    ```csharp
    Point p = Point { X: 10, Y: 20 };
    // p.X = 15; // Compile Error: Cannot assign to immutable variable 'p'
    ```
*   **Mutable:** To allow re-assignment or field modification, prefix the declaration with the `var` keyword.
    ```csharp
    var Point p = Point { X: 10, Y: 20 };
    p.X = 15; // Allowed
    ```

### Type Inference
If you do not want to write the explicit type, you can use `val` or `var` to let the compiler infer the type from the initializer expression:
```csharp
val x = 100;       // Infers an immutable 'int'
var y = 200.5;     // Infers a mutable 'double'
refvar r = ref y;  // Infers a mutable reference of 'double'
```

---

## 3. The Memory Safety Engine

Cvolo guarantees memory safety entirely at compile-time with zero runtime garbage collection pauses.

### A. Affine Types & Move Semantics (Ownership)
Every resource (like a struct) has a single owner variable. 
* **The Move Rule:** Passing a structure to a function by value, or assigning it to another variable, transfers ownership (**moves** it). The original variable is invalidated.
  ```csharp
  void Consume(Point p) { ... }

  int main() {
      var Point p1 = Point { X: 1, Y: 2 };
      Consume(p1); // p1's ownership is moved to 'Consume'
      
      // var int x = p1.X; // Compile Error: Use of moved variable 'p1'
  }
  ```
* **Re-initialization:** Re-assigning a new value to an invalidated mutable variable reactivates it safely.
  ```csharp
  var Point p = Point { X: 1, Y: 2 };
  Consume(p); // 'p' is moved

  p = Point { X: 5, Y: 5 }; // Re-assigned! 'p' is active again
  var int x = p.X;          // Allowed!
  ```

### B. The Borrow Checker (Exclusive Mutability)
To allow safe data sharing without moving ownership, Cvolo implements the **Aliasing XOR Mutability** rule:
1. You can have any number of active read-only references (`ref`) to a variable.
2. You can only have exactly **one** active mutable reference (`refvar`) to a variable, and no other references can exist at the same time.

```csharp
var Point p = Point { X: 1, Y: 2 };

ref r1 = ref p;      // OK: Read-only borrow 1
ref r2 = ref p;      // OK: Read-only borrow 2
// refvar r3 = ref p; // Compile Error: Cannot borrow 'p' mutably because incompatible borrows are active
```

### C. Lifetime Safety (Dangling Pointer Prevention)
To prevent stack-allocated memory from being cleaned up while pointers still point to it, Cvolo enforces strict lifetime bounds:
* **The Return Rule:** You can never return a reference (`ref` or `refvar`) pointing to a local stack variable declared inside that function.

```csharp
refvar Point GetDangling() {
    var Point p = Point { X: 1, Y: 1 };
    return ref p; // Compile Error: Cannot return reference to local variable 'p' (dangling reference)
}
```

---

## 4. Memory Allocation & RAII

Cvolo divides memory management between the fast CPU stack and the persistent heap.

### Stack Allocation
Standard variable declarations (such as `var Point p;`) allocate memory on the stack. This memory is automatically and instantly reclaimed as soon as the enclosing block scope ends.

### Heap Allocation (`heap` keyword)
To allocate an object whose lifetime needs to persist beyond the function that created it, use the `heap` keyword. Under the hood, this calls system `malloc`:
```csharp
var Point pPtr = heap Point { X: 10, Y: 20 }; // Allocated on the heap
```

### Automated Heap Cleanup (RAII)
To prevent memory leaks, **Cvolo developers never write `free`**. The compiler automatically tracks heap-allocated variables. When a heap-owning variable goes out of scope, the compiler checks if its ownership was moved:
*   If the variable was **moved**, no cleanup happens (responsibility is transferred).
*   If the variable was **not moved**, the compiler automatically injects a call to `free` right before the scope ends.

---

## 5. Collections (Arrays & Slices)

### Static Arrays (`Type[Size]`)
Static arrays are fixed-size, stack-allocated contiguous memory blocks.
```csharp
int[3] arr = { 10, 20, 30 }; // Explicit array initialization
var arrInferred = { 10, 20, 30 }; // Inferred as int[3]
```

### Slices (`Type[]`)
Slices represent safe, dynamic views over a portion of memory. Internally, a slice is represented as a fat pointer structure: `{ pointer, length }`.
* Slices expose a read-only **`.Length`** field.

### Spatial Safety (Bounds Checking)
Both static array indexing (`arr[i]`) and slice indexing (`slice[i]`) are fully bounds-checked at runtime using unsigned comparison (`icmp ult`). If an index is out of bounds, the program halts immediately with a runtime error detailing the file, line, and character position of the violation.

---

## 6. Generics & Modularization

### Generics
You can write highly generic structure templates and function templates using angle-bracket syntax (`<T>`).
```csharp
struct Pair<T, U> {
    T First;
    U Second;
}

void Swap<T>(refvar T a, refvar T b) {
    T temp = a;
    a = b;
    b = temp;
}
```
* **Monomorphization:** At compile-time, Cvolo duplicates and compiles these templates into highly optimized concrete implementations (e.g., `Pair<int, double>`) with zero runtime overhead.

### Namespaces & Imports (`using`)
Cvolo supports standard namespaces to modularize code across multiple files:
```csharp
namespace App.Collections;

using App.Geometry;

int main() {
    Point p = Point { X: 10, Y: 20 }; // Resolved from App.Geometry
    return 0;
}
```