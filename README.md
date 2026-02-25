<div align="center">

```
 ██████╗ ███████╗██████╗ ███████╗██╗   ██╗    ██╗██████╗ ███████╗
██╔═══██╗██╔════╝██╔══██╗██╔════╝██║   ██║    ██║██╔══██╗██╔════╝
██║   ██║███████╗██║  ██║█████╗  ██║   ██║    ██║██║  ██║█████╗  
██║   ██║╚════██║██║  ██║██╔══╝  ╚██╗ ██╔╝    ██║██║  ██║██╔══╝  
╚██████╔╝███████║██████╔╝███████╗ ╚████╔╝     ██║██████╔╝███████╗
 ╚═════╝ ╚══════╝╚═════╝ ╚══════╝  ╚═══╝      ╚═╝╚═════╝ ╚══════╝
```

**A full-stack IDE for building x86 operating systems from scratch on Windows.**  
Write bare-metal kernels in SubsetC. Compile. Assemble. Boot. All from one window.

[![License](https://img.shields.io/badge/license-MIT-blue.svg?style=flat-square)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows-0078d4?style=flat-square)](https://github.com/PointerToObject/OSDev-IDE)
[![Target](https://img.shields.io/badge/target-x86_32_Protected_Mode-orange?style=flat-square)](#)
[![Language](https://img.shields.io/badge/compiler-SubsetC-green?style=flat-square)](#subsetc-language)
[![AI](https://img.shields.io/badge/AI-Ollama_Powered-purple?style=flat-square)](#ai-assistant)

</div>

---

## What Is This?

OSDev-IDE is a self-contained development environment built specifically for writing x86 operating systems on Windows. It ships with its own compiler — **SubsetC** — a C-like language purpose-built for bare-metal kernel development, with no standard library dependencies, no runtime, and no bullshit.

You write kernel code. You hit build. You watch it boot in QEMU. That's the whole flow.

The IDE handles everything: preprocessing, tokenization, AST generation, x86 code generation via NASM, binary linking, bootloader injection, and emulator launch. It also ships with an integrated **Ollama-powered AI assistant** trained on the SubsetC language spec, a full **x86 disassembler**, **IntelliSense-style completion**, and a **visual bootloader generator**.

This is not a wrapper around GCC. It's a complete compiler and toolchain written from the ground up.

---

## Features

### SubsetC Compiler
A hand-written multi-pass compiler: preprocessor → tokenizer → recursive-descent parser → AST → x86 code generator.

- Full C-like syntax with kernel-focused extensions
- Generates NASM-compatible x86 32-bit assembly
- cdecl calling convention, flat memory model
- Inline assembly with `asm()` and `asm volatile()`
- Preprocessor with `#include`, `#define`, `#ifdef` / `#ifndef` / `#endif`
- Structs, enums, typedefs, pointers at any depth, arrays, casts, `sizeof`

### IDE & Editor
- Syntax highlighting for SubsetC (keywords, types, stdlib functions, user-defined structs)
- **IntelliSense-style autocomplete** — stdlib v3 functions with signatures, keywords, user-defined symbols, struct member completion via `.` and `->`
- Live code editing with [AvalonEdit](https://github.com/icsharpcode/AvalonEdit)
- Project management with file tree
- Build output console

### x86 Disassembler
A real x86-32 disassembler built into the IDE. Inspect compiler output at the machine code level — handles ModR/M, SIB, displacement, immediates, 0x0F prefixes, and all instructions the SubsetC compiler actually generates.

### AI Assistant (Ollama)
An integrated AI assistant running locally via [Ollama](https://ollama.com/). Understands the SubsetC language spec, the stdlib API, x86, bootloaders, QEMU, and compiler internals. Generates complete, compilable kernel code — no hallucinated APIs, no markdown, just working SubsetC.

Default model: `codellama`. Swap in any Ollama-compatible model.

### Visual Bootloader Generator
A GUI tool for generating x86 real-mode bootloaders — no assembly knowledge required. Configure:
- Kernel load address and sector count
- Stack address
- A20 line enable/disable
- Protected mode setup
- Boot message text
- VGA video mode

Outputs a complete `bootloader.asm` ready for NASM.

### One-Click Build & Run
The full pipeline: SubsetC → NASM (via WSL) → flat binary → QEMU. One button. Watching your kernel boot in real time never gets old.

---

## SubsetC Language

SubsetC is a C-like language designed from scratch for bare-metal x86 kernel development. It will diverge further from C over time into its own distinct language.

### Types

```c
int, char, void, short, long
unsigned, signed, const, volatile
int*    char**    void***    // Pointers at any depth
int arr[100];               // Arrays
struct, enum, typedef       // Composite types
```

> `unsigned`, `signed`, `short`, `long` are all treated as 32-bit int internally.

### Control Flow

```c
if / else if / else
while (cond) { }
for (init; cond; incr) { }
do { } while (cond);
switch (expr) { case N: ... break; default: ... }
break, continue, return
```

### Operators

```
Arithmetic:   + - * / %
Bitwise:      & | ^ ~ << >>
Logical:      && || !
Comparison:   == != < > <= >=
Assignment:   = += -= *= /=
Increment:    ++ --  (prefix and postfix)
Access:       -> .  &(address-of)  *(dereference)
Cast:         (type)expr
Ternary:      condition ? true_expr : false_expr
```

### Storage Classes & Qualifiers

```c
static, extern, register
inline, volatile, const, __packed
```

### Inline Assembly

```c
asm("hlt");
asm("cli\nsti");
asm volatile("hlt");
```

### Preprocessor

```c
#include "file.c"
#define NAME value
#ifdef NAME
#ifndef NAME
#endif
#pragma (ignored)
```

### Stdlib v3 API

Include `stdlib.c` for kernel-ready runtime functions:

```c
#include "stdlib.c"

// Printing
printf("Score: %d\n", score);       // Full format string support
printf("Addr: 0x%08x\n", ptr);      // %d %u %x %X %p %s %c %b %%
sprintf(buf, "Level %d", level);    // Format into buffer

// VGA Text Mode (80x25, 0xB8000)
vga_clear();
vga_setcolor(fg, bg);               // Colors 0-15
vga_putc(ch);
vga_putc_at(x, y, ch);
vga_puts(str);
vga_println(str);

// Port I/O
outb(port, value);    inb(port);
outw(port, value);    inw(port);
outl(port, value);    inl(port);

// CPU control
cli();   sti();   halt();

// Control registers
read_cr0();    write_cr0(val);
read_cr3();    write_cr3(val);

// Memory
memcpy(dst, src, n);
memset(dst, val, n);
```

---

## Compiler Architecture

```
Source Code (.c)
      │
      ▼
┌─────────────┐
│ Preprocessor│  #include expansion, #define substitution, #ifdef guards
└──────┬──────┘
       │
       ▼
┌─────────────┐
│  Tokenizer  │  Lexical analysis → Token stream
└──────┬──────┘
       │
       ▼
┌─────────────┐
│   Parser    │  Recursive-descent → Abstract Syntax Tree
└──────┬──────┘
       │
       ▼
┌─────────────┐
│  Code Gen   │  AST → NASM x86-32 assembly
└──────┬──────┘
       │
       ▼
┌─────────────┐
│    NASM     │  Assembly → flat binary (via WSL)
└──────┬──────┘
       │
       ▼
┌─────────────┐
│    QEMU     │  Boot and run
└─────────────┘
```

---

## Target Platform

| Property         | Value                          |
|------------------|--------------------------------|
| Architecture     | x86 (IA-32)                    |
| Mode             | 32-bit Protected Mode          |
| ABI              | cdecl                          |
| Output Format    | Flat binary                    |
| Default Org      | `0x1000`                       |
| Memory Model     | Flat (CS=DS=ES=SS)             |
| Assembler        | NASM (via WSL)                 |

---

## Getting Started

### Prerequisites

| Tool | Purpose |
|------|---------|
| Windows 10/11 | Host OS |
| Visual Studio 2019+ | Build the IDE |
| WSL | Runs NASM and the build toolchain |
| NASM | Assembles generated x86 output |
| QEMU | Emulates your kernel |
| Ollama *(optional)* | Local AI assistant |

### Build & Run

```bash
# Clone
git clone https://github.com/PointerToObject/OSDev-IDE.git
cd OSDev-IDE

# Open in Visual Studio
# Open BootstrapCompiler.sln

# Build (Release config, Ctrl+Shift+B)
# Launch the executable
```

Make sure WSL is set up and NASM is installed inside it:

```bash
# Inside WSL
sudo apt install nasm
```

---

## Example: Minimal Kernel

```c
#include "stdlib.c"

void kernel_main() {
    vga_clear();
    vga_setcolor(15, 0);  // White on black
    vga_println("Hello from bare metal.");
    
    while (1) {}
}
```

## Example: Port I/O — Serial Init

```c
#include "stdlib.c"

void init_serial() {
    outb(0x3F8 + 1, 0x00);  // Disable interrupts
    outb(0x3F8 + 3, 0x80);  // Enable DLAB
    outb(0x3F8 + 0, 0x03);  // Baud divisor low
    outb(0x3F8 + 1, 0x00);  // Baud divisor high
    outb(0x3F8 + 3, 0x03);  // 8N1
}

void kernel_main() {
    init_serial();
    vga_println("Serial initialized.");
    while (1) {}
}
```

## Example: Inline Assembly + Control Registers

```c
void enable_paging(unsigned int pd_addr) {
    write_cr3(pd_addr);
    unsigned int cr0 = read_cr0();
    cr0 = cr0 | 0x80000000;
    write_cr0(cr0);
    asm("nop");
}
```

---

## Feature Status

| Feature | Status |
|---------|--------|
| SubsetC compiler (core) | ✅ Complete |
| Preprocessor | ✅ Complete |
| Inline assembly | ✅ Complete |
| Structs / enums / typedefs | ✅ Complete |
| Pointer arithmetic | ✅ Complete |
| Type casting + sizeof | ✅ Complete |
| Switch / do-while | ✅ Complete |
| Syntax highlighting | ✅ Complete |
| IntelliSense / autocomplete | ✅ Complete |
| x86 Disassembler | ✅ Complete |
| Visual bootloader generator | ✅ Complete |
| Ollama AI assistant | ✅ Complete |
| One-click build & run | ✅ Complete |
| Project management | ✅ Complete |
| Stdlib v3 runtime | ✅ Complete |
| Multi-file projects | 🚧 In Progress |
| Interactive debugger | 🚧 Planned |
| Breakpoint support | 🚧 Planned |
| SubsetC language extensions | 🚧 Planned |

---

## Project Structure

```
OSDev-IDE/
├── BootstrapCompiler/
│   ├── Tokenizer/
│   │   ├── Tokenizer.c / .h          # Lexical analysis
│   │   └── Preprocessor/
│   │       ├── Preprocessor.c / .h   # #include, #define, #ifdef
│   ├── Parser/
│   │   ├── Parser.c / .h             # Recursive-descent parser + AST
│   ├── Codegen/
│   │   ├── Codegen.c / .h            # x86-32 code generation
│   ├── Main.c / .h                   # Entry point + AST printer
│   └── Includes.h                    # Shared C headers
├── IDE/
│   ├── MainWindow.xaml / .cs         # Main IDE window (WPF)
│   ├── OllamaService.cs              # Ollama AI integration
│   ├── OllamaWindow.cs               # AI assistant UI
│   ├── SubsetCCompletion.cs          # IntelliSense provider
│   ├── x86Disassembler.cs            # x86-32 disassembler
│   └── BootloaderGenerator.cs        # Visual bootloader config
├── Runtime/
│   └── stdlib.c                      # Kernel stdlib v3
└── README.md
```

---

## Contributing

Pull requests welcome. If you're adding language features to SubsetC, open an issue first to discuss the design — the language is intentionally constrained for kernel use cases, not general purpose C compatibility.

---

<div align="center">

Built for people who want to understand how computers actually work.

</div>
