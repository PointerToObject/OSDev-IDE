# SubsetC Standard Library Reference

## Quick Start

```c
#include "stdlib.c"

void kernel_main() {
    vga_clear();
    print_line("Hello, World!");
    
    while (1) { asm("hlt"); }
}
```

---

## VGA Text Output

### Screen Control

| Function | Description |
|----------|-------------|
| `vga_clear()` | Clear screen, reset cursor to (0,0) |
| `vga_set_color(fg, bg)` | Set text color (foreground, background) |
| `vga_set_cursor(x, y)` | Move cursor to position |
| `vga_putchar(c)` | Print single character at cursor |
| `vga_putchar_at(x, y, c)` | Print character at specific position |
| `vga_scroll()` | Scroll screen up one line |

### Print Functions

| Function | Description |
|----------|-------------|
| `print_char(c)` | Print single character |
| `print_string(str)` | Print null-terminated string |
| `print_line(str)` | Print string + newline |
| `print_int(n)` | Print integer (decimal) |
| `print_hex(n)` | Print integer (hex with 0x prefix) |
| `print_newline()` | Print newline character |

### Color Constants

```c
BLACK, BLUE, GREEN, CYAN, RED, MAGENTA, BROWN, LIGHT_GRAY
DARK_GRAY, LIGHT_BLUE, LIGHT_GREEN, LIGHT_CYAN, LIGHT_RED
LIGHT_MAGENTA, YELLOW, WHITE
```

### Example

```c
vga_set_color(YELLOW, BLUE);  // Yellow text on blue background
vga_clear();
print_string("Score: ");
print_int(100);
print_newline();
```

---

## Keyboard Input

### Functions

| Function | Description |
|----------|-------------|
| `keyboard_init()` | Initialize keyboard (called automatically) |
| `keyboard_ready()` | Returns 1 if key available, 0 otherwise |
| `keyboard_getchar()` | Wait for and return ASCII character |
| `keyboard_readline(buf, maxlen)` | Read line into buffer with backspace support |

### Example

```c
print_string("Enter name: ");
char name[32];
keyboard_readline(name, 32);
print_string("Hello, ");
print_line(name);
```

---

## String Functions

| Function | Description |
|----------|-------------|
| `strlen(str)` | Return length of string |
| `strcmp(s1, s2)` | Compare strings (0 if equal) |
| `strncmp(s1, s2, n)` | Compare first n characters |
| `strcpy(dst, src)` | Copy string |
| `strncpy(dst, src, n)` | Copy up to n characters |
| `strcat(dst, src)` | Append src to dst |

### Example

```c
char cmd[32];
keyboard_readline(cmd, 32);

if (strcmp(cmd, "help") == 0) {
    print_line("Available commands: help, clear, exit");
}
```

---

## Memory Functions

| Function | Description |
|----------|-------------|
| `memset_byte(dst, val, count)` | Fill memory with byte value |
| `memcpy_byte(dst, src, count)` | Copy memory bytes |
| `malloc(size)` | Allocate memory (bump allocator) |
| `heap_reset()` | Reset heap to start |

### Example

```c
char* buffer = malloc(256);
memset_byte(buffer, 0, 256);
strcpy(buffer, "Hello");
```

---

## Conversion Functions

| Function | Description |
|----------|-------------|
| `atoi(str)` | String to integer |
| `itoa(n, buf)` | Integer to string |

### Example

```c
char numstr[12];
itoa(12345, numstr);
print_line(numstr);  // Prints "12345"

int value = atoi("42");  // value = 42
```

---

## Utility Functions

| Function | Description |
|----------|-------------|
| `abs(n)` | Absolute value |
| `min(a, b)` | Minimum of two values |
| `max(a, b)` | Maximum of two values |
| `delay(count)` | Simple busy-wait delay |
| `srand(seed)` | Seed random number generator |
| `rand()` | Get random number (0-32767) |

---

## Character Functions

| Function | Description |
|----------|-------------|
| `isdigit(c)` | Is '0'-'9' |
| `isalpha(c)` | Is letter |
| `isalnum(c)` | Is letter or digit |
| `isspace(c)` | Is whitespace |
| `isupper(c)` | Is uppercase |
| `islower(c)` | Is lowercase |
| `toupper(c)` | Convert to uppercase |
| `tolower(c)` | Convert to lowercase |

---

## Complete Shell Example

```c
#include "stdlib.c"

void cmd_help() {
    print_line("Commands:");
    print_line("  help   - Show this message");
    print_line("  clear  - Clear screen");
    print_line("  echo   - Echo text");
    print_line("  add    - Add two numbers");
}

void cmd_echo(char* text) {
    print_line(text);
}

void cmd_add() {
    char buf[16];
    int a;
    int b;
    
    print_string("First number: ");
    keyboard_readline(buf, 16);
    a = atoi(buf);
    
    print_string("Second number: ");
    keyboard_readline(buf, 16);
    b = atoi(buf);
    
    print_string("Result: ");
    print_int(a + b);
    print_newline();
}

void kernel_main() {
    char cmd[64];
    
    vga_set_color(LIGHT_GREEN, BLACK);
    vga_clear();
    
    print_line("MyOS v1.0");
    print_line("Type 'help' for commands");
    print_newline();
    
    while (1) {
        vga_set_color(LIGHT_CYAN, BLACK);
        print_string("$ ");
        vga_set_color(WHITE, BLACK);
        
        keyboard_readline(cmd, 64);
        
        if (strcmp(cmd, "help") == 0) {
            cmd_help();
        } else if (strcmp(cmd, "clear") == 0) {
            vga_clear();
        } else if (strncmp(cmd, "echo ", 5) == 0) {
            cmd_echo(cmd + 5);
        } else if (strcmp(cmd, "add") == 0) {
            cmd_add();
        } else if (strlen(cmd) > 0) {
            print_string("Unknown command: ");
            print_line(cmd);
        }
    }
}
```

---

## Port I/O (Built-in Runtime)

These are provided by the compiler runtime, not stdlib:

| Function | Description |
|----------|-------------|
| `outb(port, value)` | Write byte to port |
| `inb(port)` | Read byte from port |
| `outw(port, value)` | Write word to port |
| `inw(port)` | Read word from port |
| `outl(port, value)` | Write dword to port |
| `inl(port)` | Read dword from port |
| `cli()` | Disable interrupts |
| `sti()` | Enable interrupts |
| `halt()` | Halt CPU |

---

## Memory Layout

| Address | Usage |
|---------|-------|
| `0x1000` | Kernel load address |
| `0x90000` | Stack (grows down) |
| `0xB8000` | VGA text buffer |
| `0x100000` | Heap start (1MB) |
| `0x400000` | Heap end (4MB) |

---

## Tips

1. **Always initialize**: Call `vga_clear()` at start
2. **Keyboard**: `keyboard_init()` is called automatically on first use
3. **Heap**: Use `heap_reset()` to reclaim all allocated memory
4. **Colors**: Set color BEFORE printing
5. **Strings**: Always null-terminate, check buffer sizes
