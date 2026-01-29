# OSDev-IDE

**Integrated Development Environment for OS Development on Windows**  
A complete IDE for building x86 operating systems with the SubsetC compiler. Build real bootable kernels with a single click.
<img width="1395" height="898" alt="image" src="https://github.com/user-attachments/assets/df29b07b-58a4-41a1-ace0-bd5dd93f76d7" />


---

## Overview

OSDev-IDE is a specialized development environment for writing x86 operating systems on Windows. It includes the **SubsetC compiler**—a custom language (subset of C) designed specifically for kernel development—along with integrated tooling for the full OS development workflow.

### Key Features

- **SubsetC Compiler** – Custom language compiler optimized for bare metal x86 kernel development
- **32-bit Protected Mode Target** – Generates x86 assembly for protected mode kernels
- **One-Click Build & Run** – Compile source to bootable binary and launch in emulator instantly
- **Integrated Build System** – Automatic compilation through SubsetC → NASM → binary
- **Modern IDE** – Syntax highlighting, code editing, and project management

---

## What You Can Build

With OSDev-IDE, you can create:

- **Bootable Kernels** – Real x86 kernels that run on bare metal or in emulators
- **Device Drivers** – Port I/O operations, interrupt handlers, and hardware control
- **Memory Management** – Paging, segmentation, and dynamic memory allocation
- **Custom Operating Systems** – From simple kernels to complex OS implementations

---

## SubsetC Compiler Capabilities

The SubsetC language is a C-like language designed specifically for OS kernel development. It will branch into its own distinct language with additional features in the future.

### Data Types
- Primitive types: `int`, `char`, `void`, `short`, `long`
- Type modifiers: `unsigned`, `signed`, `const`, `volatile`
- Pointers with multiple indirection levels
- Arrays (single and multi-dimensional)
- Structures with member access (`.` and `->`)
- Enumerations
- Typedefs for custom type aliases

### Language Features
- **Control Flow**: `if`/`else`, `while`, `for`, `break`, `continue`, `return`
- **Operators**: Arithmetic, logical, bitwise, comparison, assignment, ternary (`?:`)
- **Functions**: Declaration, definition, forward declarations, parameters
- **Preprocessor**: `#include`, `#define`, `#pragma` directives
- **Storage Classes**: `static`, `extern`, `register`
- **Qualifiers**: `inline`, `volatile`, `const`, `__packed`
- **Inline Assembly**: Full `asm` and `__asm__` support for mixing C and assembly

### Kernel-Specific Features
- Port I/O functions: `inb`, `outb`, `inw`, `outw`, `inl`, `outl`
- Interrupt control: `cli`, `sti`, `halt`
- Control register access: `read_cr0`, `write_cr0`, `read_cr3`, `write_cr3`
- Memory operations: `memcpy`, `memset`
- VGA text mode output: `print_string`, `print_fmt` (with format specifiers)
- Type casting including pointer casts
- `sizeof` operator for type and expression size calculation

### Code Generation
- **Target Architecture**: x86 (32-bit protected mode only)
- **Output Format**: NASM-compatible assembly
- **Calling Convention**: cdecl (C declaration)
- **Binary Output**: Flat binary format for kernels
- **Base Address**: Configurable (default `org 0x1000`)

---

## Getting Started

### Prerequisites

- Windows 10 or 11
- Visual Studio 2019 or later (for building the IDE itself)
- WSL (Windows Subsystem for Linux) – required for build toolchain
- NASM (Netwide Assembler) – for assembling generated x86 code
- QEMU or similar x86 emulator – for testing kernels

### Installation

1. **Clone the repository:**
   ```bash
   git clone https://github.com/PointerToObject/OSDev-IDE.git
   cd OSDev-IDE
   ```

2. **Open the solution:**
   Open `BootstrapCompiler.sln` in Visual Studio

3. **Build the IDE:**
   - Select `Release` configuration
   - Build → Build Solution (or press `Ctrl+Shift+B`)

4. **Run OSDev-IDE:**
   - Launch the compiled executable
   - The IDE includes the SubsetC compiler
   - Ensure WSL and NASM are properly configured

---

## Usage

### Creating a New Kernel Project

1. Open OSDev-IDE
2. Click **New Project** or **File → New → OS Project**
3. Choose a project template:
   - **Blank Kernel** – Minimal kernel with entry point
   - **VGA Text Mode** – Kernel with screen output
   - **Advanced Kernel** – Includes memory management and interrupts
4. Write your kernel code in the integrated editor

### Building Your Kernel

1. Write your kernel code in the SubsetC language
2. Click **Build** or press `F7`
3. The compiler processes your code through:
   - Preprocessing (`#include`, `#define` expansion)
   - Tokenization and parsing
   - Abstract syntax tree generation
   - x86 assembly code generation (32-bit protected mode)
   - Assembly to binary (via NASM in WSL)

### Running Your Kernel

1. After successful build, click **Run** or press `F5`
2. The IDE launches your kernel in the configured emulator
3. Watch your OS boot and execute in real-time

### Example Kernel Code

```c
// kernel.c - Minimal bootable kernel

void kernel_main() {
    // Clear screen
    char* video = (char*)0xB8000;
    for (int i = 0; i < 80 * 25 * 2; i++) {
        video[i] = 0;
    }
    
    // Print "Hello, OS World!"
    const char* message = "Hello, OS World!";
    for (int i = 0; message[i] != 0; i++) {
        video[i * 2] = message[i];
        video[i * 2 + 1] = 0x0F; // White on black
    }
    
    // Halt CPU
    while (1) {
        asm("hlt");
    }
}
```

### Advanced Example with Port I/O

```c
// Port I/O example
void init_serial() {
    outb(0x3F8 + 1, 0x00);  // Disable interrupts
    outb(0x3F8 + 3, 0x80);  // Enable DLAB
    outb(0x3F8 + 0, 0x03);  // Set divisor (low byte)
    outb(0x3F8 + 1, 0x00);  // Set divisor (high byte)
    outb(0x3F8 + 3, 0x03);  // 8 bits, no parity, one stop bit
}

unsigned char read_serial() {
    while ((inb(0x3F8 + 5) & 1) == 0);
    return inb(0x3F8);
}
```

---

## Project Structure

```
/OSDev-IDE
  /BootstrapCompiler      - IDE's built-in C compiler
    /Tokenizer.c          - Lexical analysis
    /Parser.c             - Syntax analysis & AST
    /Codegen.c            - x86 code generation
    /Preprocessor.c       - Macro expansion & includes
  /IDE                    - IDE application source
    /Editor               - Code editor component
    /Project              - Project management
    /Build                - Build system integration
  /Runtime                - Kernel runtime functions
  /Examples               - Sample kernel projects
  /docs                   - Documentation
  README.md               - This file
```

---

## Compiler Architecture

```
Source Code (SubsetC language)
    ↓
Preprocessor (handle #include, #define)
    ↓
Tokenizer (lexical analysis)
    ↓
Parser (syntax analysis → AST)
    ↓
Code Generator (AST → x86 assembly)
    ↓
NASM (via WSL - assembly → machine code)
    ↓
Bootable Binary
```

---

## Feature Status

| Feature                          | Status        |
|----------------------------------|---------------|
| SubsetC Compiler (core features) | ✅ Supported  |
| Inline assembly                  | ✅ Supported  |
| Struct/enum/typedef              | ✅ Supported  |
| Preprocessor directives          | ✅ Supported  |
| Pointer arithmetic               | ✅ Supported  |
| Type casting                     | ✅ Supported  |
| One-click Build & Run            | ✅ Supported  |
| Integrated emulator              | ✅ Supported  |
| Syntax highlighting              | ✅ Supported  |
| Project management               | ✅ Supported  |
| Kernel runtime functions         | ✅ Supported  |
| Multi-file projects              | 🚧 In Progress|
| Interactive debugger             | 🚧 Planned    |
| Breakpoint support               | 🚧 Planned    |
| SubsetC language extensions      | 🚧 Planned    |

---

## Technical Details

### Supported Language Standard
- **Language**: SubsetC (C-like syntax, subset of C with kernel-focused features)
- **Future**: Will branch into its own distinct language with extended capabilities
- **Focus**: Features essential for bare metal kernel development without OS dependencies

### Target Platform
- **Architecture**: x86 (IA-32)
- **Mode**: 32-bit protected mode (no real mode support)
- **ABI**: cdecl calling convention
- **Output**: Flat binary format

### Memory Model
- **Segmentation**: Flat model (CS=DS=ES=SS)
- **Stack**: Grows downward from high memory
- **Heap**: Manual allocation in kernel space

---

