<div align="center">

![OSDEVICO](https://github.com/user-attachments/assets/5f4e521e-ef49-411e-a729-93fce4048634)

# OSDev-IDE

**One IDE. Two compilers. Bare metal to the PLC floor.**

Write x86 operating systems in SubsetC and watch them boot in QEMU — *or* write industrial control logic in C and transpile it straight to Allen-Bradley ladder logic. Both from the same window, both with compilers written from scratch.

[![License](https://img.shields.io/badge/license-MIT-blue.svg?style=flat-square)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows-0078d4?style=flat-square)](https://github.com/PointerToObject/OSDev-IDE)
[![OS Target](https://img.shields.io/badge/OS-x86_32_Protected_Mode-orange?style=flat-square)](#side-a--osdev-x86-kernels)
[![PLC Target](https://img.shields.io/badge/PLC-Allen--Bradley_·_Beckhoff_·_Mitsubishi-e63e3e?style=flat-square)](#side-b--cst-c--plc-ladder-logic)
[![AI](https://img.shields.io/badge/AI-Ollama-purple?style=flat-square)](#ai-assistant-ollama)

</div>

---

## What is this?

OSDev-IDE started as a from-scratch toolchain for writing x86 operating systems on Windows. It now does a second thing that's arguably more useful on a job site: it compiles plain C into **PLC ladder logic** you can import into Allen-Bradley Studio 5000 — plus a built-in **simulator and HMI designer** so you can test the whole machine before you ever touch hardware.

Neither side wraps an existing compiler. Both the **SubsetC** (x86) and **CST** (PLC) compilers are hand-written: preprocessor → tokenizer → recursive-descent parser → AST → code generator.

- **Side A — OSDev:** SubsetC → NASM x86-32 → flat binary → QEMU.
- **Side B — CST:** C → ladder logic (`.L5X`) for Studio 5000, or Structured Text for Beckhoff TwinCAT / Mitsubishi FX.

Pick the side you care about and skip the other.

---

# Side B — CST: C → PLC Ladder Logic

> **Why:** ladder logic is slow to write and miserable to refactor. Write your control logic in C, let CST transpile it to ladder, and import the result into the vendor's software. You get version control, real functions, and actual data structures — and the PLC still runs stock ladder any technician can read.

## The pipeline

```
your C code                cst compiler                Studio 5000 / TwinCAT / GX Works
───────────         →      ───────────         →       ────────────────────────────────
 main.c                    C  → AST                     import .L5X  →  download to PLC
                           AST → ladder IR
                           IR  → .L5X (Allen-Bradley)
                                  .st  (Beckhoff ST)
                                  .gxil (Mitsubishi IL)
```

You **cannot** push ladder straight into an AB controller from a third-party tool — that step is proprietary and signed by Rockwell. So CST nails the handoff: it emits a clean `.L5X` that imports into Studio 5000 first try, and you download from there. Everything else — simulation, HMI, live tag work — happens right in this IDE.

## Targets

| Backend | Output | Status |
|---|---|---|
| **Allen-Bradley Logix (Ladder)** | `.L5X` (RLL) | ✅ UDTs, TIMER/COUNTER, native instructions |
| Allen-Bradley Logix (ST) | `.st` | ✅ Structured Text |
| Beckhoff TwinCAT 3 | `.st` | ✅ Structured Text |
| Mitsubishi FX1S/FX1N | `.gxil` | ✅ Instruction List |

Select a target with the toolbar dropdown, a `#include <allen_bradley_ll>` pragma, or `-target=allen_bradley_ll` on the CLI.

## Write C, get ladder

```c
#include <allen_bradley_ll>
#include "cst_runtime.h"

// buttons
bool start_btn;
bool stop_btn;

// inputs
int  scale;

// outputs
bool valve_in;
bool valve_out;
bool pump;
bool done;

// state
bool pouring;

int main() {
    pouring = cst_seal(pouring, start_btn, stop_btn) && scale < 16;

    valve_in  = pouring;
    valve_out = pouring;
    pump      = pouring;

    done = scale >= 16;
}
```

That `cst_seal(...)` line compiles to a textbook seal-in rung:

```
[XIC(pouring),XIC(start_btn)] XIO(stop_btn) LES(scale,16) OTE(pouring);
```

Press start, it latches. Press stop (or hit the fill target), it drops. One line of C, one rung of ladder.

## The runtime — `cst_*` helpers

All of these lower to **native** Logix instructions (no custom AOIs to maintain):

| Helper | Lowers to | Use |
|---|---|---|
| `cst_seal(state, set, reset)` | seal-in rung | Set-priority latch (START/STOP) |
| `cst_seal_rp(state, set, reset)` | reset-first seal-in | **E-stops** (STOP wins ties) |
| `cst_within(v, lo, hi)` | `LIM(lo,v,hi)` | Range check |
| `cst_timer_on/off/done` | `TON` / `RES` / `XIC(t.DN)` | On-delay timers (ms) |
| `cst_tof_*` | `TOF` | Off-delay timers |
| `cst_ctu_*` / `cst_ctd_*` | `CTU` / `CTD` | Counters |
| `cst_redge_* / cst_fedge_*` | `ONS` / `OSF` | Edge detection |
| `cst_memcpy / cst_memset` | `COP` / `FILL` | Block ops |
| `cst_abs / min / max / clamp` | `ABS` / `MIN` / `MAX` / `LIMIT` | Math |

Control flow maps the obvious way: `if/else` → contact-gated rungs, `while`/`for` → `JMP`/`LBL` loops, `switch` → `EQU` cascades, function calls → `JSR`, structs → Logix **UDTs**.

## Built-in simulator

Hit **Simulate** and the IDE runs your compiled ladder in a real scan-cycle VM — no hardware needed.

- **Ladder VM** — evaluates the actual rungs (XIC/XIO/OTE/OTL/OTU/TON/CTU/CPT/MOV/EQU/LIM/JSR/JMP/…) with series/branch power-flow and scan-delta timer accumulation
- **PROGRAM / RUN mode** toggle, adjustable scan rate, single-step, reset
- **Live ladder view** — watch contacts and coils light up green as power flows, exactly like Studio 5000's online monitoring
- **Force tags** — right-click any tag → Force ON/OFF or Force value. Reads return the forced value, writes are dropped, forced rows highlight orange. Fake an input, override an output, test a fault path
- **Tag grid + auto-HMI** — every tag shown live, struct members expanded

## HMI designer

A drag-and-drop HMI builder, saved as a `.hmi` JSON next to your source.

- Drag widgets from the palette onto a grid canvas; click to select, drag to move (snaps to grid), 8-grip resize, marquee multi-select, Ctrl+D duplicate, copy/paste, z-order, align
- **Industrial widget set:** Tank (glass + level marks), Flame, SteamStack (animated), Pump/Motor (spinning), Valve, Gauge, PressureGauge (PSI dial + red zone), Bargraph, Trend chart, Lamp, Button (Momentary/Latching/Set/Reset), Toggle, Selector, NumberDisplay/Entry, AlarmStrip, PIDBlock
- Alarm bands (low/high) recolor analog widgets green/amber/red automatically
- In RUN mode the HMI drives the live simulation — toggle inputs, watch the machine react

## Editor support for PLC

- Autocomplete for every `cst_*` function with full signatures
- Snippets: type `seal`, `motor_starter`, `fault_latch`, `ton_idiom`, `plc_main` + Tab
- Hover any `cst_*` symbol for its docstring
- **Deploy to Studio 5000** button copies the `.L5X` to a watched import folder

---

# Side A — OSDev: x86 Kernels

Everything that made this an OS-dev IDE in the first place still works.

**SubsetC compiler** — hand-written multi-pass compiler emitting NASM x86-32. C-like syntax with structs, enums, typedefs, pointers to any depth, arrays, casts, `sizeof`, inline `asm()`, and a real preprocessor (`#include`, `#define`, `#ifdef`). cdecl, flat memory model, default org `0x1000`.

**x86 disassembler** — inspect compiler output at the machine-code level: ModR/M, SIB, displacement, immediates, `0x0F` prefixes.

**Visual bootloader generator** — build a real-mode bootloader from a GUI: load address, sector count, stack, A20, protected-mode switch, boot message, VGA mode → ready-to-assemble `bootloader.asm`.

**One-click build & run** — SubsetC → NASM (via WSL) → flat binary → QEMU.

### Minimal kernel

```c
#include "stdlib.c"

void kernel_main() {
    vga_clear();
    vga_setcolor(15, 0);                 // white on black
    vga_println("Hello from bare metal.");
    while (1) {}
}
```

### stdlib v3 (kernel runtime)

```c
printf("Score: %d  0x%08x\n", n, ptr);   // %d %u %x %X %p %s %c %b %%
vga_clear(); vga_setcolor(fg,bg); vga_puts(s); vga_putc_at(x,y,ch);
outb(port,v); inb(port);  outw/inw  outl/inl
cli(); sti(); halt();
read_cr0(); write_cr0(v); read_cr3(); write_cr3(v);
memcpy(dst,src,n); memset(dst,val,n);
```

---

## AI Assistant (Ollama)

An assistant running locally through [Ollama](https://ollama.com/). It understands both the SubsetC and CST languages, the runtimes, x86, bootloaders, and ladder logic — and generates code that actually compiles. Default model `codellama`; swap in any Ollama model.

---

## Getting Started

### Prerequisites

| Tool | Purpose |
|---|---|
| Windows 10/11 | Host OS |
| Visual Studio 2019+ | Build the IDE |
| .NET 8 SDK | IDE is WPF / net8.0-windows |
| WSL + NASM | OSDev side: assembles x86 output |
| QEMU | OSDev side: emulates the kernel |
| Studio 5000 *(optional)* | PLC side: imports the `.L5X`, downloads to controller |
| Ollama *(optional)* | Local AI assistant |

### Build & run

```bash
git clone https://github.com/PointerToObject/OSDev-IDE.git
cd OSDev-IDE
# open BootstrapCompiler.sln in Visual Studio
# build Release (Ctrl+Shift+B), then launch IDE.exe
```

For the OSDev side, install NASM inside WSL:

```bash
sudo apt install nasm
```

### Your first PLC program

1. New project → drop the beer-dispenser snippet (or type `plc_main` + Tab) into `Source/main.c`
2. Set the toolbar target to **Allen-Bradley Logix (Ladder)**
3. **Compile** → produces `Output/CST_Program.L5X`
4. **Simulate** → toggle inputs, flip to RUN, watch the ladder energize
5. **Deploy to Studio 5000** → copies the `.L5X` to your import folder
6. In Studio 5000: Tasks → Import Program → pick the `.L5X` → download

---

## Project Structure

```
BootstrapCompiler/
├── BootstrapCompiler/         # SubsetC compiler (x86 OSDev side)
│   ├── Tokenizer/ Parser/ Codegen/
│   └── Main.c
├── IDE/                       # WPF IDE (net8.0-windows)
│   ├── MainWindow.xaml(.cs)   # editor, file tree, build, toolbar
│   ├── Subsetccompletion.cs   # autocomplete + snippets + hover docs
│   ├── x86dissassembler.cs    # x86-32 disassembler
│   ├── BootloaderGenerator.cs # visual bootloader config
│   ├── OllamaService.cs        # local AI integration
│   └── Sim/                   # ── PLC simulator + HMI ──
│       ├── TagDatabase.cs      # observable tag store + force
│       ├── L5XReader.cs        # parse compiled .L5X back in
│       ├── RungParser.cs       # rung text → AST + expr evaluator
│       ├── LadderVm.cs         # scan-cycle ladder VM
│       ├── LadderView.xaml(.cs)# live graphical ladder monitoring
│       ├── HmiModel.cs         # .hmi JSON model
│       ├── ThemedWidgets.cs    # industrial HMI widgets
│       ├── HmiDesigner.xaml(.cs)# drag-drop HMI designer
│       └── SimWindow.xaml(.cs) # PROGRAM/RUN, tabs, force UI
└── README.md

CST compiler (separate tree):
CST/CST/CST/
├── Tokenizer/ Parser/ CST-Generation/
└── CST-Generation/Targets/
    ├── beckhoff.c            # TwinCAT 3 Structured Text
    ├── allen_bradley.c       # AB Structured Text
    ├── allen_bradley_ll.c    # AB Ladder Logic (.L5X)  ← the main event
    └── mitsubishi_fx.c       # Mitsubishi Instruction List
```

---

## Feature Status

| Feature | Status |
|---|---|
| **PLC** — C → AB ladder (`.L5X`) | ✅ |
| **PLC** — UDTs / TIMER / COUNTER emission | ✅ |
| **PLC** — Beckhoff ST / Mitsubishi IL backends | ✅ |
| **PLC** — seal-in / timer / counter / edge helpers | ✅ |
| **PLC** — scan-cycle ladder simulator | ✅ |
| **PLC** — live graphical ladder monitoring | ✅ |
| **PLC** — force tags | ✅ |
| **PLC** — drag-drop HMI designer + industrial widgets | ✅ |
| **PLC** — live PLC comms (libplctag / OPC-UA / Modbus) | 🚧 Planned |
| **PLC** — PID + analog scaling instructions | 🚧 Planned |
| **OS** — SubsetC compiler / preprocessor / inline asm | ✅ |
| **OS** — x86 disassembler | ✅ |
| **OS** — visual bootloader generator | ✅ |
| **OS** — one-click build & run (QEMU) | ✅ |
| Ollama AI assistant | ✅ |
| Interactive debugger / breakpoints | 🚧 Planned |

---

---

<div align="center">


</div>
