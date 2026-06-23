# 🗺️ Cvolo Grammar & Syntax Specification

This document provides a human-readable guide to the syntax, rules, and structure of the **Cvolo** programming language.

---

## 1. Variables and Mutability

Cvolo enforces safety by requiring mutability to be explicit. All variables are declared using either `val` (immutable) or `var` (mutable).

### Immutable Bindings (`val`)
An immutable variable cannot be reassigned or modified after initialization. This is the recommended default for safety.
```csharp
val limit = 100;
val int count = 10; // Explicitly typed
```

### Mutable Variables (`var`)
To allow a variable's value to change, use the `var` keyword.
```csharp
var score = 0;
score = 10; // Allowed

var double temperature = 36.6; // Explicitly typed
```

---

## 2. Ownership and References

To achieve high performance without a Garbage Collector, Cvolo tracks variable ownership. Passing a non-primitive variable (like a struct) to another variable or function transfers ("moves") its ownership. To avoid moving, you can "borrow" variables using references.

### Immutable Borrowing (`ref`)
By default, references are read-only. This allows reading data without taking ownership. You can have multiple active immutable references to the same data at the same time.
```csharp
val myData = "Secure Info";

// Create an immutable reference (read-only borrow)
ref string dataRef = ref myData; 
```

### Mutable Borrowing (`ref var`)
Allows both reading and modifying the referenced data. Only one active mutable reference is allowed for a piece of data at any time to prevent data conflicts.
```csharp
var health = 100;

// Create a mutable reference
ref var int healthRef = ref health;
healthRef = 90; // Modifies the original 'health' variable
```

---

## 3. Functions

Function declarations in Cvolo look like standard C# functions, but they require explicit parameters describing how variables are passed.

### Function Structure
Functions are declared with a return type, name, and parameter list. By default, function parameters are immutable.
```csharp
int CalculateArea(int width, int height) {
    return width * height;
}
```

### Parameter Modifiers
Because parameters are immutable by default, we do not use the `val` keyword in signatures. You can use modifiers to specify how data is received:
*   `int x`: The function receives a read-only copy or takes ownership (if it is a non-copy type).
*   `ref Point p`: The function borrows a read-only reference to a struct.
*   `ref var Point p`: The function borrows a mutable reference to a struct.

```csharp
void ResetCoordinate(ref var Point p) {
    p.X = 0;
    p.Y = 0;
}
```

---

## 4. Structures (Structs)

Structs are custom data types that hold groups of related variables. They use C# structure definitions.

### Definition
Fields in structures are declared with their types and names.
```csharp
struct User {
    int Id;
    string Username;
    bool IsActive;
}
```

### Instantiation
To create an instance of a struct, you specify its name and initialize its fields using the `Field: Value` syntax:
```csharp
val newUser = User {
    Id: 1,
    Username: "admin",
    IsActive: true
};
```

---

## 5. Control Flow

Cvolo uses familiar C# control flow statements. Parentheses are required around conditions, and blocks are wrapped in curly braces.

### Conditionals (`if` / `else`)
```csharp
if (score >= 50) {
    printf("Passed");
} else {
    printf("Failed");
}
```

### While Loops
```csharp
var count = 5;
while (count > 0) {
    printf("%d\n", count);
    count = count - 1;
}
```

### For Loops
The loop counter variable must be explicitly declared as mutable (`var`) inside the loop definition.
```csharp
for (var int i = 0; i < 10; i = i + 1) {
    printf("%d\n", i);
}
```

---

## 6. External Functions (extern) & Native Interoperability

To interact with the operating system, C standard libraries (`libc`), or pre-compiled C libraries, Cvolo provides the `extern` keyword. This allows you to declare functions that are defined outside the Cvolo compiler's scope, enabling direct compilation and linking with native code.

### Syntax
An external function declaration consists of the `extern` keyword, followed by a standard C-style function signature, and ends with a semicolon. No body `{ ... }` is provided.

```csharp
extern return_type function_name(parameter_type parameter_name, ...);
```

### Key Use Cases

#### 1. Console Output (`printf`)
Standard C formatting uses variadic arguments (represented by `...`). Cvolo allows declaring these using the same format:

```csharp
// Import printf from C standard library
extern void printf(string format, ...);

void Main() {
    val x = 42;
    // Call the external function
    printf("The answer is: %d\n", x); 
}
```

#### 2. Manual Memory Management (`malloc` & `free`)
While Cvolo's compiler enforces safety using ownership, you can bypass this for low-level system tasks or custom allocators by declaring `malloc` and `free`.

```csharp
// Declaring memory operations
extern any_ptr malloc(int size);
extern void free(any_ptr ptr);
```
*(Note: `any_ptr` represents a raw, unsafe pointer type used specifically for compatibility with native C APIs).*

### Ownership Boundaries and Safety Rules

When values cross the boundary between safe Cvolo code and unsafe `extern` functions, the following rules apply:

1. **Primitive Types:** Types like `int`, `double`, and `bool` are passed directly by value as copy-types. They do not affect the ownership state of the caller.
2. **Strings:** Cvolo `string` types are passed to `extern` functions as standard C-style null-terminated character pointers (`const char*`). 
3. **No Safety Guarantees inside `extern`:** The Cvolo compiler cannot analyze or enforce ownership checks on the logic written inside external C functions. Once a resource is passed to an `extern` function by mutable reference (`ref var`), the developer is responsible for ensuring that the external code does not violate memory safety.
4. **No Destructors for Externally Allocated Memory:** If you allocate memory via an external function like `malloc`, Cvolo's automatic resource cleanup will not track it. You must manually free it using the corresponding external call:

```csharp
void AllocateDemo() {
    // Allocation bypasses Cvolo's safe heap tracking
    val rawMemory = malloc(1024); 
    
    // Developer must manually free it to avoid memory leaks
    free(rawMemory); 
}
```

---

## 7. Scopes and Resource Deallocation (RAII)

Cvolo uses lexical scopes (defined by curly braces `{ ... }`) to manage the lifetimes of resources deterministically. It implements Resource Acquisition Is Initialization (RAII), similar to C++ and Rust, but using C#-style block structures.

### The Scope Rule
When a variable that owns a resource goes out of the scope in which it was declared, its resource is automatically freed (dropped). No Garbage Collector or manual `free()` call is required for safe Cvolo types.

```csharp
void ScopeDemo() {
    // Allocation happens here
    var newUser = User { Id: 1, Username: "Alice", IsActive: true }; 
    
    printf("User created\n");
} // <- 'newUser' goes out of scope here. 
  // Memory for 'newUser' is automatically and safely deallocated.
```

### Scopes and Moved Variables
If ownership of a variable is transferred (moved), the original variable is marked as empty. When it goes out of scope, the deallocation is bypassed to prevent double-free bugs.

```csharp
void MoveDemo() {
    val u1 = User { Id: 2, Username: "Bob", IsActive: true };
    
    // u1's resource is moved to u2. u1 is now uninitialized.
    val u2 = u1; 

} // <- u1 and u2 go out of scope.
  // u2's resource is safely deallocated.
  // u1's deallocation is skipped because its ownership was transferred.
```

---

## 8. Built-in Types and Operators

Cvolo supports a specific set of primitive types and standard operators designed for predictable memory layouts and efficient machine-code generation.

### Primitive Types
Unlike high-level C# types, Cvolo primitives map directly to standard hardware-native sizes:
*   `int`: 32-bit signed integer.
*   `double`: 64-bit double-precision floating-point number.
*   `bool`: Boolean value (`true` or `false`).
*   `char`: 8-bit ASCII character.
*   `string`: An immutable, null-terminated sequence of characters (stored as a read-only pointer).

### Type Casting (Conversion)
Because Cvolo is strongly and statically typed, implicit type conversions are kept to a minimum to prevent precision-loss bugs. Explicit casting uses C#-style parentheses:

```csharp
val int x = 42;
// Explicitly cast 'int' to 'double'
val double y = (double)x; 
```

### Operator Precedence & Evaluation
Cvolo operators evaluate from left to right, following standard mathematical precedence rules:

1.  **Unary Operators:** `-`, `!`, `ref` (Highest precedence)
2.  **Multiplicative:** `*`, `/`, `%`
3.  **Additive:** `+`, `-`
4.  **Relational:** `<`, `>`, `<=`, `>=`
5.  **Equality:** `==`, `!=`
6.  **Logical AND:** `&&`
7.  **Logical OR:** `||` (Lowest precedence)

```csharp
val int result = (5 + 3) * 2; // Parentheses can override default precedence
```

---

## 9. Arrays and Slices (Memory Buffers)

Cvolo supports fixed-size arrays allocated on the stack, as well as contiguous memory slices (views), combining C# array syntax with safe compile-time memory boundaries.

### Fixed-Size Arrays
An array is a fixed-length sequence of elements of the same type. The size of the array must be known at compile time.

```csharp
// Declaring an immutable array of 5 integers on the stack
val int[5] numbers = int[5] { 10, 20, 30, 40, 50 };

// Accessing elements (0-indexed)
val first = numbers[0]; 
```

### Array Ownership
An array owns all of its elements. Moving an array transfers ownership of the entire contiguous memory block. Because elements cannot be moved individually out of an array (which would leave "holes" in memory), elements of non-copy types must be accessed via references.

```csharp
var User[3] users = User[3] { ... };

// Error: Cannot move a single element out of an array
// val singleUser = users[0]; 

// Correct: Borrow a reference to the element instead
ref var User userRef = ref users[0];
```

### Slices (`ref T[]`)
A slice is a dynamically-sized view into a contiguous sequence of elements (like a subset of an array). It does not own the data; it only references it.

```csharp
void PrintFirstTwo(ref int[] slice) {
    printf("First: %d, Second: %d\n", slice[0], slice[1]);
}

void Main() {
    val int[5] numbers = int[5] { 1, 2, 3, 4, 5 };
    
    // Pass a reference to the array as a slice
    PrintFirstTwo(ref numbers); 
}
```

---

## 10. Unsafe Code and Raw Pointers

To write low-level code, interact with hardware, or perform manual pointer arithmetic, Cvolo provides an `unsafe` keyword. This functions similarly to C# but turns off the compiler's borrow checker inside the specified block.

### Unsafe Blocks
Safe Cvolo code cannot use raw pointers. To use them, you must wrap the code in an `unsafe` block:

```csharp
void UnsafeDemo() {
    var int value = 42;
    
    unsafe {
        // Create a raw pointer pointing to 'value'
        int* ptr = &value; 
        
        // Dereference and modify the value directly in memory
        *ptr = 99; 
        
        printf("Modified value: %d\n", value); // Prints 99
    }
}
```

### Raw Pointer Operators
Inside an `unsafe` context, Cvolo supports standard C-style pointer operators:
*   `T*`: Declares a raw pointer to type `T`.
*   `&x`: The address-of operator (returns a raw pointer to `x`).
*   `*ptr`: Dereferences a pointer to access or modify the underlying data.
*   **Pointer Arithmetic:** You can add or subtract offsets from raw pointers (e.g., `ptr + 1`).

```csharp
unsafe {
    val int[3] arr = int[3] { 10, 20, 30 };
    int* ptr = &arr[0];
    
    // Move pointer to the next element
    int* nextPtr = ptr + 1; 
    printf("Next: %d\n", *nextPtr); // Prints 20
}
```

---

## 11. Error Handling and Panic

Cvolo does not use a standard `try-catch` exception model because throwing heap-allocated exception objects requires a heavy runtime execution environment. Instead, it uses **Panic** for unrecoverable errors and **Result patterns** for recoverable errors.

### Unrecoverable Errors (`panic`)
A panic is triggered by fatal runtime violations (such as array out-of-bounds access) or explicitly by the developer. It prints an error message, cleans up the current stack frame, and terminates the program safely.

```csharp
int GetElement(ref int[5] arr, val int index) {
    if (index < 0 || index >= 5) {
        // Terminate execution safely on critical boundary violation
        panic("Index out of bounds!"); 
    }
    return arr[index];
}
```

### Recoverable Errors
For expected errors (like failing to open a file or parse an integer), functions return status codes or tuple-like structures containing the success flag and the resulting data, similar to modern system APIs:

```csharp
struct ParseResult {
    bool Success;
    int Value;
}

ParseResult TryParseInt(string input) {
    // Parsing logic...
    if (/* failed */) {
        return ParseResult { Success: false, Value: 0 };
    }
    return ParseResult { Success: true, Value: 42 };
}
```

---

## 12. Generic Types (Generics)

To support code reuse without runtime performance costs, Cvolo supports compile-time generics using C#-style `<T>` angle-bracket syntax. Generics in Cvolo are monomorphized (specialized) at compile time, meaning the compiler generates native machine code for each concrete type used, resulting in zero runtime overhead.

### Generic Structures
You can declare structures that can hold any data type.

```csharp
struct Pair<T> {
    T First;
    T Second;
}

void Main() {
    // Instantiate a Pair of integers
    val intPair = Pair<int> { First: 1, Second: 2 };
    
    // Instantiate a Pair of booleans
    val boolPair = Pair<bool> { First: true, Second: false };
}
```

### Generic Functions
Functions can also accept generic types. Parameters can use references (`ref` or `ref var`) with generic placeholders.

```csharp
// Swaps the values of two variables of any type T
void Swap<T>(ref var T a, ref var T b) {
    val temp = a; // Ownership of 'a' moves to 'temp'
    a = b;        // Ownership of 'b' moves to 'a'
    b = temp;     // Ownership of 'temp' moves to 'b'
}

void Main() {
    var int x = 10;
    var int y = 20;
    
    Swap<int>(ref x, ref y);
}
```

---

## 13. Heap Allocation & Smart Pointers (`Box<T>`)

By default, all local variables and structures in Cvolo are allocated on the stack for maximum performance. However, for large structures, dynamically sized data, or recursive types, memory must be allocated on the heap. 

To make heap allocation safe and automatic, Cvolo provides a built-in smart pointer type called `Box<T>`.

### Allocating on the Heap
`Box<T>` allocates memory on the heap, moves the specified value into it, and acts as the unique owner of that heap memory.

```csharp
struct LargeData {
    int[1000] Buffer;
}

void HeapDemo() {
    // Allocates LargeData on the heap. 'boxedData' is the owner.
    var Box<LargeData> boxedData = Box<LargeData>.New(LargeData { ... });
    
    // Accessing fields is transparent
    boxedData.Buffer[0] = 42; 
    
} // <- 'boxedData' goes out of scope here.
  // The heap-allocated memory is automatically freed (no Garbage Collector).
```

### Box Ownership and Moving
Because a `Box<T>` uniquely owns its heap allocation, assigning it to another variable transfers (moves) the pointer and the ownership of the heap memory.

```csharp
void TransferDemo() {
    val b1 = Box<int>.New(100);
    
    // Ownership of the heap memory moves to b2. b1 is now invalid.
    val b2 = b1; 
    
    // printf("%d\n", *b1); // Compile Error: b1 has been moved
} // <- Only b2 deallocates the heap memory when going out of scope
```

### Recursive Data Structures
`Box<T>` is required to define recursive structures (like linked lists or trees), because the compiler must know the byte size of structures at compile time. A pointer (`Box`) has a fixed size, whereas nesting a struct directly inside itself would create an infinitely large type.

```csharp
struct Node {
    int Value;
    // Boxed reference to the next node (optional/nullable pointer)
    Box<Node> Next; 
}
```
