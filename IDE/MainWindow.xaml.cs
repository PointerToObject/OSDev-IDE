using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using System.Xml;
using MaterialDesignThemes.Wpf;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OSDevIDE
{
    public class TabItem
    {
        public string FilePath { get; set; }
        public string FileName { get; set; }
        public bool IsPinned { get; set; }
        public bool IsActive { get; set; }
    }

    public partial class MainWindow : Window
    {
        private string projectPath = "";
        private string currentFile = "";
        private string selectedTemplate = "Empty Project";
        private string projectType = "OS";
        private ObservableCollection<TabItem> openTabs = new ObservableCollection<TabItem>();
        private TabItem draggedTab = null;
        private Border draggedTabBorder = null;
        private HashSet<string> expandedFolders = new HashSet<string>();
        private Process terminalProcess = null;

        private const string BootloaderAsm = @"[org 0x7C00]
[BITS 16]

start:
    xor ax, ax
    mov ds, ax
    mov es, ax
    mov ss, ax
    mov sp, 0x7BFE

    ; Print '1' - we started
    mov ax, 0x0E31
    int 0x10

    ; Check LBA extensions available
    mov ah, 0x41
    mov bx, 0x55AA
    mov dl, 0x80
    int 0x13
    jc .no_lba
    
    ; Print '2' - LBA supported
    mov ax, 0x0E32
    int 0x10

    ; Load kernel using LBA
    mov si, dap
    mov ah, 0x42
    mov dl, 0x80
    int 0x13
    jc .read_fail

    ; Print '3' - read succeeded
    mov ax, 0x0E33
    int 0x10

    ; Switch to protected mode
    cli
    lgdt [gdt_desc]
    mov eax, cr0
    or eax, 1
    mov cr0, eax
    jmp 0x08:protected_mode

.no_lba:
    mov ax, 0x0E4C  ; 'L' = no LBA
    int 0x10
    jmp hang

.read_fail:
    mov ax, 0x0E52  ; 'R' = read failed
    int 0x10
    ; Fall through to hang

hang:
    mov ax, 0x0E48  ; 'H' = hang
    int 0x10
    jmp $

; Disk Address Packet
align 4
dap:
    db 0x10         ; Size (16 bytes)
    db 0            ; Reserved
    dw 100          ; Sectors to read (~50KB)
    dw 0x0000       ; Offset
    dw 0x0800       ; Segment (0x0800:0x0000 = 0x8000 physical)
    dd 1            ; LBA low (sector 1)
    dd 0            ; LBA high

[BITS 32]
protected_mode:
    mov ax, 0x10
    mov ds, ax
    mov es, ax
    mov ss, ax
    mov esp, 0x90000
    
    ; Write '4' directly to VGA to show we're in PM
    mov byte [0xB8000], '4'
    mov byte [0xB8001], 0x0F
    
    jmp 0x8000

gdt_start:
    dq 0
gdt_code:
    dw 0xFFFF, 0
    db 0, 10011010b, 11001111b, 0
gdt_data:
    dw 0xFFFF, 0
    db 0, 10010010b, 11001111b, 0
gdt_end:

gdt_desc:
    dw gdt_end - gdt_start - 1
    dd gdt_start

times 510 - ($ - $$) db 0
dw 0xAA55";

        private const string StdlibC = @"// ============================================================
// SUBSETC STANDARD LIBRARY v3.0
// Bare-metal x86-32 - No OS dependencies
//
// New in v3:  printf/sprintf, hex dump, better strings,
//             PIT-calibrated delay, arena allocator w/ free,
//             shift-aware keyboard, debug/panic helpers
// ============================================================

#ifndef STDLIB_C
#define STDLIB_C

// ============================================================
// VGA TEXT MODE
// ============================================================

int VGA_WIDTH  = 80;
int VGA_HEIGHT = 25;
int VGA_MEMORY = 0xB8000;

int vga_x    = 0;
int vga_y    = 0;
int vga_attr = 0x0F;

// Color constants
int BLACK    = 0;    int COLOR_BLACK    = 0;
int BLUE     = 1;    int COLOR_BLUE     = 1;
int GREEN    = 2;    int COLOR_GREEN    = 2;
int CYAN     = 3;    int COLOR_CYAN     = 3;
int RED      = 4;    int COLOR_RED      = 4;
int MAGENTA  = 5;    int COLOR_MAGENTA  = 5;
int BROWN    = 6;    int COLOR_BROWN    = 6;
int LGRAY    = 7;    int COLOR_LGRAY    = 7;
int DGRAY    = 8;    int COLOR_DGRAY    = 8;
int LBLUE    = 9;    int COLOR_LBLUE    = 9;
int LGREEN   = 10;   int COLOR_LGREEN   = 10;
int LCYAN    = 11;   int COLOR_LCYAN    = 11;
int LRED     = 12;   int COLOR_LRED     = 12;
int LMAGENTA = 13;   int COLOR_LMAGENTA = 13;
int YELLOW   = 14;   int COLOR_YELLOW   = 14;
int WHITE    = 15;   int COLOR_WHITE    = 15;

// ============================================================
// BOX DRAWING / CP437 CHARACTERS
// ============================================================

// Single-line box
int CH_FULL   = 219;    int CH_SHADE1 = 176;
int CH_SHADE2 = 177;    int CH_SHADE3 = 178;
int BOX_H     = 196;    int BOX_V     = 179;
int BOX_TL    = 218;    int BOX_TR    = 191;
int BOX_BL    = 192;    int BOX_BR    = 217;

// Double-line box
int DBOX_H    = 205;    int DBOX_V    = 186;
int DBOX_TL   = 201;    int DBOX_TR   = 187;
int DBOX_BL   = 200;    int DBOX_BR   = 188;

// Single/double crossovers
int BOX_CROSS  = 197;   int BOX_T_DOWN = 194;
int BOX_T_UP   = 193;   int BOX_T_RIGHT = 195;
int BOX_T_LEFT = 180;

// Block/arrow chars
int CH_HALF_L = 221;    int CH_HALF_R = 222;
int CH_ARROW_R = 16;    int CH_ARROW_L = 17;
int CH_ARROW_U = 30;    int CH_ARROW_D = 31;
int CH_DIAMOND = 4;     int CH_BULLET  = 7;
int CH_HEART   = 3;     int CH_SMILEY  = 1;

// ============================================================
// KEYBOARD SCANCODES (PS/2 Set 1)
// ============================================================

int KEY_ESC   = 1;
int KEY_1     = 2;     int KEY_2     = 3;     int KEY_3     = 4;
int KEY_4     = 5;     int KEY_5     = 6;     int KEY_6     = 7;
int KEY_7     = 8;     int KEY_8     = 9;     int KEY_9     = 10;
int KEY_0     = 11;
int KEY_MINUS = 12;    int KEY_EQUAL = 13;    int KEY_BKSP  = 14;
int KEY_TAB   = 15;
int KEY_Q     = 16;    int KEY_W     = 17;    int KEY_E     = 18;
int KEY_R     = 19;    int KEY_T     = 20;    int KEY_Y     = 21;
int KEY_U     = 22;    int KEY_I     = 23;    int KEY_O     = 24;
int KEY_P     = 25;
int KEY_LBRACKET = 26; int KEY_RBRACKET = 27;
int KEY_ENTER = 28;
int KEY_LCTRL = 29;
int KEY_A     = 30;    int KEY_S     = 31;    int KEY_D     = 32;
int KEY_F     = 33;    int KEY_G     = 34;    int KEY_H     = 35;
int KEY_J     = 36;    int KEY_K     = 37;    int KEY_L     = 38;
int KEY_SEMI  = 39;    int KEY_QUOTE = 40;    int KEY_TILDE = 41;
int KEY_LSHIFT = 42;   int KEY_BACKSL = 43;
int KEY_Z     = 44;    int KEY_X     = 45;    int KEY_C     = 46;
int KEY_V     = 47;    int KEY_B     = 48;    int KEY_N     = 49;
int KEY_M     = 50;
int KEY_COMMA = 51;    int KEY_DOT   = 52;    int KEY_SLASH = 53;
int KEY_RSHIFT = 54;
int KEY_SPACE = 57;
int KEY_CAPS  = 58;
int KEY_F1    = 59;    int KEY_F2    = 60;    int KEY_F3    = 61;
int KEY_F4    = 62;    int KEY_F5    = 63;    int KEY_F6    = 64;
int KEY_F7    = 65;    int KEY_F8    = 66;    int KEY_F9    = 67;
int KEY_F10   = 68;
int KEY_UP    = 72;    int KEY_LEFT  = 75;
int KEY_RIGHT = 77;    int KEY_DOWN  = 80;
int KEY_DEL   = 83;

// ============================================================
// VGA CORE FUNCTIONS
// ============================================================

void vga_setcolor(int fg, int bg) {
    vga_attr = (bg << 4) | fg;
}

void vga_putc_at(int x, int y, char c) {
    char* vga = (char*)VGA_MEMORY;
    int offset = (y * VGA_WIDTH + x) * 2;
    vga[offset] = c;
    vga[offset + 1] = vga_attr;
}

void vga_putc_at_attr(int x, int y, char c, int attr) {
    char* vga = (char*)VGA_MEMORY;
    int offset = (y * VGA_WIDTH + x) * 2;
    vga[offset] = c;
    vga[offset + 1] = attr;
}

void vga_scroll() {
    char* vga = (char*)VGA_MEMORY;
    int i = 0;
    int row_bytes = VGA_WIDTH * 2;
    int total = row_bytes * (VGA_HEIGHT - 1);
    while (i < total) {
        vga[i] = vga[i + row_bytes];
        i = i + 1;
    }
    i = row_bytes * (VGA_HEIGHT - 1);
    while (i < row_bytes * VGA_HEIGHT) {
        vga[i] = 32;
        vga[i + 1] = vga_attr;
        i = i + 2;
    }
}

void vga_putc(char c) {
    if (c == 10) {
        vga_x = 0;
        vga_y = vga_y + 1;
    } else if (c == 13) {
        vga_x = 0;
    } else if (c == 9) {
        // Tab: advance to next 8-column boundary
        vga_x = (vga_x + 8) & 0xFFFFFFF8;
    } else if (c == 8) {
        if (vga_x > 0) {
            vga_x = vga_x - 1;
            vga_putc_at(vga_x, vga_y, 32);
        }
    } else {
        vga_putc_at(vga_x, vga_y, c);
        vga_x = vga_x + 1;
    }
    if (vga_x >= VGA_WIDTH) {
        vga_x = 0;
        vga_y = vga_y + 1;
    }
    if (vga_y >= VGA_HEIGHT) {
        vga_scroll();
        vga_y = VGA_HEIGHT - 1;
    }
}

void vga_clear() {
    char* vga = (char*)VGA_MEMORY;
    int i = 0;
    int total = VGA_WIDTH * VGA_HEIGHT * 2;
    while (i < total) {
        vga[i] = 32;
        vga[i + 1] = vga_attr;
        i = i + 2;
    }
    vga_x = 0;
    vga_y = 0;
}

void vga_setpos(int x, int y) {
    vga_x = x;
    vga_y = y;
}

int vga_getx() { return vga_x; }
int vga_gety() { return vga_y; }

// ============================================================
// CURSOR CONTROL
// ============================================================

void cur_hide() {
    outb(0x3D4, 0x0A);
    outb(0x3D5, 0x20);
}

void cur_show() {
    outb(0x3D4, 0x0A);
    outb(0x3D5, 0x0E);
    outb(0x3D4, 0x0B);
    outb(0x3D5, 0x0F);
}

void cur_move(int x, int y) {
    int pos = y * VGA_WIDTH + x;
    outb(0x3D4, 0x0F);
    outb(0x3D5, pos & 0xFF);
    outb(0x3D4, 0x0E);
    outb(0x3D5, (pos >> 8) & 0xFF);
}

// ============================================================
// VGA PRINT FUNCTIONS
// ============================================================

void vga_puts(char* str) {
    int i = 0;
    while (str[i] != 0) {
        vga_putc(str[i]);
        i = i + 1;
    }
}

void vga_println(char* str) {
    vga_puts(str);
    vga_putc(10);
}

void vga_newline() {
    vga_putc(10);
}

void vga_putint(int n) {
    char buf[12];
    int i = 0;
    int neg = 0;
    if (n < 0) { neg = 1; n = 0 - n; }
    if (n == 0) { vga_putc(48); return; }
    while (n > 0) {
        buf[i] = 48 + (n % 10);
        n = n / 10;
        i = i + 1;
    }
    if (neg) { vga_putc(45); }
    while (i > 0) { i = i - 1; vga_putc(buf[i]); }
}

void vga_putuint(int n) {
    // Print as unsigned (handles values > 0x7FFFFFFF)
    char buf[12];
    int i = 0;
    if (n == 0) { vga_putc(48); return; }
    // Unsigned division trick: cast through shifting
    // For simplicity, just print as hex if negative
    if (n < 0) {
        vga_puthex(n);
        return;
    }
    while (n > 0) {
        buf[i] = 48 + (n % 10);
        n = n / 10;
        i = i + 1;
    }
    while (i > 0) { i = i - 1; vga_putc(buf[i]); }
}

void vga_puthex(int n) {
    char* hex = ""0123456789ABCDEF"";
    int i = 28;
    int started = 0;
    vga_puts(""0x"");
    while (i >= 0) {
        int digit = (n >> i) & 0xF;
        if (digit != 0) { started = 1; }
        if (started || i == 0) {
            vga_putc(hex[digit]);
        }
        i = i - 4;
    }
}

void vga_puthex_full(int n) {
    // Always print all 8 hex digits
    char* hex = ""0123456789ABCDEF"";
    int i = 28;
    vga_puts(""0x"");
    while (i >= 0) {
        vga_putc(hex[(n >> i) & 0xF]);
        i = i - 4;
    }
}

void vga_putbin(int n, int bits) {
    // Print binary: vga_putbin(0xA, 8) -> ""00001010""
    int i = bits - 1;
    while (i >= 0) {
        if ((n >> i) & 1) { vga_putc(49); }
        else { vga_putc(48); }
        i = i - 1;
    }
}

void vga_int(int n) { vga_putint(n); }

void vga_int_at(int x, int y, int n) {
    char buf[12];
    int i = 11;
    int neg = 0;
    int digit;
    buf[11] = 0;
    if (n == 0) { vga_putc_at(x, y, 48); return; }
    if (n < 0) { neg = 1; n = 0 - n; }
    while (n > 0) {
        i = i - 1;
        digit = n % 10;
        buf[i] = 48 + digit;
        n = n / 10;
    }
    if (neg) { i = i - 1; buf[i] = 45; }
    vga_printat(x, y, &buf[i]);
}

// ============================================================
// VGA GAME/UI HELPER FUNCTIONS
// ============================================================

void vga_put(int x, int y, int c) {
    vga_putc_at(x, y, c);
}

void vga_put_color(int x, int y, int c, int fg, int bg) {
    int attr = (bg << 4) | fg;
    vga_putc_at_attr(x, y, c, attr);
}

void vga_printat(int x, int y, char* str) {
    int i = 0;
    while (str[i] != 0) {
        vga_putc_at(x + i, y, str[i]);
        i = i + 1;
    }
}

void vga_printat_color(int x, int y, char* str, int fg, int bg) {
    int i = 0;
    int attr = (bg << 4) | fg;
    while (str[i] != 0) {
        vga_putc_at_attr(x + i, y, str[i], attr);
        i = i + 1;
    }
}

void vga_center(int y, char* str) {
    int len = 0;
    int x;
    while (str[len] != 0) { len = len + 1; }
    x = (VGA_WIDTH - len) / 2;
    vga_printat(x, y, str);
}

// Fill a region with repeated char + current color
void vga_fill(int x, int y, int w, int h, int c) {
    int i;
    int j = 0;
    while (j < h) {
        i = 0;
        while (i < w) {
            vga_putc_at(x + i, y + j, c);
            i = i + 1;
        }
        j = j + 1;
    }
}

// Repeat a character N times at cursor
void vga_repeat(int c, int count) {
    int i = 0;
    while (i < count) {
        vga_putc(c);
        i = i + 1;
    }
}

// Draw a horizontal line
void vga_hline(int x, int y, int len, int c) {
    int i = 0;
    while (i < len) {
        vga_putc_at(x + i, y, c);
        i = i + 1;
    }
}

// Draw a vertical line
void vga_vline(int x, int y, int len, int c) {
    int i = 0;
    while (i < len) {
        vga_putc_at(x, y + i, c);
        i = i + 1;
    }
}

// Read character at screen position
int vga_peek(int x, int y) {
    char* vga = (char*)VGA_MEMORY;
    int offset = (y * VGA_WIDTH + x) * 2;
    return vga[offset];
}

// Read attribute at screen position
int vga_peek_attr(int x, int y) {
    char* vga = (char*)VGA_MEMORY;
    int offset = (y * VGA_WIDTH + x) * 2;
    return vga[offset + 1];
}

// ============================================================
// DRAWING FUNCTIONS
// ============================================================

void draw_box(int x, int y, int w, int h) {
    int i;
    vga_put(x, y, BOX_TL);
    i = 1;
    while (i < w - 1) { vga_put(x + i, y, BOX_H); i = i + 1; }
    vga_put(x + w - 1, y, BOX_TR);
    i = 1;
    while (i < h - 1) {
        vga_put(x, y + i, BOX_V);
        vga_put(x + w - 1, y + i, BOX_V);
        i = i + 1;
    }
    vga_put(x, y + h - 1, BOX_BL);
    i = 1;
    while (i < w - 1) { vga_put(x + i, y + h - 1, BOX_H); i = i + 1; }
    vga_put(x + w - 1, y + h - 1, BOX_BR);
}

void draw_dbox(int x, int y, int w, int h) {
    int i;
    vga_put(x, y, DBOX_TL);
    i = 1;
    while (i < w - 1) { vga_put(x + i, y, DBOX_H); i = i + 1; }
    vga_put(x + w - 1, y, DBOX_TR);
    i = 1;
    while (i < h - 1) {
        vga_put(x, y + i, DBOX_V);
        vga_put(x + w - 1, y + i, DBOX_V);
        i = i + 1;
    }
    vga_put(x, y + h - 1, DBOX_BL);
    i = 1;
    while (i < w - 1) { vga_put(x + i, y + h - 1, DBOX_H); i = i + 1; }
    vga_put(x + w - 1, y + h - 1, DBOX_BR);
}

void draw_fill(int x, int y, int w, int h, int c) {
    int i;
    int j = 0;
    while (j < h) {
        i = 0;
        while (i < w) { vga_put(x + i, y + j, c); i = i + 1; }
        j = j + 1;
    }
}

void draw_fill_color(int x, int y, int w, int h, int c, int fg, int bg) {
    int i;
    int j = 0;
    while (j < h) {
        i = 0;
        while (i < w) { vga_put_color(x + i, y + j, c, fg, bg); i = i + 1; }
        j = j + 1;
    }
}

void draw_shadow(int x, int y, int w, int h) {
    int i = 1;
    while (i < h) {
        vga_put_color(x + w, y + i, 32, BLACK, BLACK);
        i = i + 1;
    }
    i = 1;
    while (i <= w) {
        vga_put_color(x + i, y + h, 32, BLACK, BLACK);
        i = i + 1;
    }
}

void draw_progress(int x, int y, int w, int value, int max) {
    // Draw a progress bar: [████░░░░░░] value/max
    int filled;
    int i;
    if (max <= 0) { max = 1; }
    filled = (value * w) / max;
    if (filled > w) { filled = w; }
    if (filled < 0) { filled = 0; }
    i = 0;
    while (i < filled) { vga_putc_at(x + i, y, CH_FULL); i = i + 1; }
    while (i < w) { vga_putc_at(x + i, y, CH_SHADE1); i = i + 1; }
}

// ============================================================
// STRING FUNCTIONS
// ============================================================

int str_len(char* str) {
    int len = 0;
    while (str[len] != 0) { len = len + 1; }
    return len;
}

int str_cmp(char* s1, char* s2) {
    int i = 0;
    while (s1[i] != 0 && s2[i] != 0) {
        if (s1[i] != s2[i]) { return s1[i] - s2[i]; }
        i = i + 1;
    }
    return s1[i] - s2[i];
}

int str_ncmp(char* s1, char* s2, int n) {
    int i = 0;
    while (i < n && s1[i] != 0 && s2[i] != 0) {
        if (s1[i] != s2[i]) { return s1[i] - s2[i]; }
        i = i + 1;
    }
    if (i == n) return 0;
    return s1[i] - s2[i];
}

int str_eq(char* s1, char* s2) {
    return str_cmp(s1, s2) == 0;
}

void str_cpy(char* dst, char* src) {
    int i = 0;
    while (src[i] != 0) { dst[i] = src[i]; i = i + 1; }
    dst[i] = 0;
}

void str_ncpy(char* dst, char* src, int n) {
    int i = 0;
    while (i < n && src[i] != 0) { dst[i] = src[i]; i = i + 1; }
    while (i < n) { dst[i] = 0; i = i + 1; }
}

void str_cat(char* dst, char* src) {
    int i = str_len(dst);
    int j = 0;
    while (src[j] != 0) { dst[i] = src[j]; i = i + 1; j = j + 1; }
    dst[i] = 0;
}

// Find first occurrence of char in string (-1 if not found)
int str_chr(char* str, char c) {
    int i = 0;
    while (str[i] != 0) {
        if (str[i] == c) { return i; }
        i = i + 1;
    }
    return -1;
}

// Find last occurrence of char in string (-1 if not found)
int str_rchr(char* str, char c) {
    int last = -1;
    int i = 0;
    while (str[i] != 0) {
        if (str[i] == c) { last = i; }
        i = i + 1;
    }
    return last;
}

// Find substring in string (-1 if not found)
int str_str(char* haystack, char* needle) {
    int i = 0;
    int nlen = str_len(needle);
    int hlen = str_len(haystack);
    if (nlen == 0) { return 0; }
    while (i <= hlen - nlen) {
        if (str_ncmp(&haystack[i], needle, nlen) == 0) {
            return i;
        }
        i = i + 1;
    }
    return -1;
}

// Check if string starts with prefix
int str_starts(char* str, char* prefix) {
    int i = 0;
    while (prefix[i] != 0) {
        if (str[i] != prefix[i]) { return 0; }
        i = i + 1;
    }
    return 1;
}

// Check if string ends with suffix
int str_ends(char* str, char* suffix) {
    int slen = str_len(str);
    int xlen = str_len(suffix);
    if (xlen > slen) { return 0; }
    return str_cmp(&str[slen - xlen], suffix) == 0;
}

// Reverse string in-place
void str_rev(char* str) {
    int left = 0;
    int right = str_len(str) - 1;
    char tmp;
    while (left < right) {
        tmp = str[left];
        str[left] = str[right];
        str[right] = tmp;
        left = left + 1;
        right = right - 1;
    }
}

// Trim leading/trailing spaces (modifies in place, returns str)
char* str_trim(char* str) {
    int start = 0;
    int end;
    int i;
    while (str[start] == 32 || str[start] == 9) { start = start + 1; }
    end = str_len(str) - 1;
    while (end > start && (str[end] == 32 || str[end] == 9)) { end = end - 1; }
    i = 0;
    while (start <= end) {
        str[i] = str[start];
        i = i + 1;
        start = start + 1;
    }
    str[i] = 0;
    return str;
}

// Convert string to uppercase in-place
void str_upper(char* str) {
    int i = 0;
    while (str[i] != 0) {
        if (str[i] >= 97 && str[i] <= 122) { str[i] = str[i] - 32; }
        i = i + 1;
    }
}

// Convert string to lowercase in-place
void str_lower(char* str) {
    int i = 0;
    while (str[i] != 0) {
        if (str[i] >= 65 && str[i] <= 90) { str[i] = str[i] + 32; }
        i = i + 1;
    }
}

// Count occurrences of char in string
int str_count(char* str, char c) {
    int count = 0;
    int i = 0;
    while (str[i] != 0) {
        if (str[i] == c) { count = count + 1; }
        i = i + 1;
    }
    return count;
}

// ============================================================
// MEMORY FUNCTIONS
// ============================================================

void mem_set(char* dst, char val, int count) {
    int i = 0;
    while (i < count) { dst[i] = val; i = i + 1; }
}

void mem_cpy(char* dst, char* src, int count) {
    int i = 0;
    while (i < count) { dst[i] = src[i]; i = i + 1; }
}

int mem_cmp(char* a, char* b, int count) {
    int i = 0;
    while (i < count) {
        if (a[i] != b[i]) { return a[i] - b[i]; }
        i = i + 1;
    }
    return 0;
}

void mem_zero(char* dst, int count) {
    int i = 0;
    while (i < count) { dst[i] = 0; i = i + 1; }
}

// ============================================================
// CONVERSION FUNCTIONS
// ============================================================

int str_to_int(char* str) {
    int result = 0;
    int neg = 0;
    int i = 0;
    while (str[i] == 32) { i = i + 1; }
    if (str[i] == 45) { neg = 1; i = i + 1; }
    else if (str[i] == 43) { i = i + 1; }
    while (str[i] >= 48 && str[i] <= 57) {
        result = result * 10 + (str[i] - 48);
        i = i + 1;
    }
    if (neg) { return 0 - result; }
    return result;
}

int hex_to_int(char* str) {
    int result = 0;
    int i = 0;
    int c;
    // Skip optional ""0x"" prefix
    if (str[0] == 48 && (str[1] == 120 || str[1] == 88)) { i = 2; }
    while (str[i] != 0) {
        c = str[i];
        result = result << 4;
        if (c >= 48 && c <= 57) { result = result + (c - 48); }
        else if (c >= 65 && c <= 70) { result = result + (c - 55); }
        else if (c >= 97 && c <= 102) { result = result + (c - 87); }
        else { break; }
        i = i + 1;
    }
    return result;
}

void int_to_str(int n, char* buf) {
    char tmp[12];
    int i = 0;
    int j = 0;
    int neg = 0;
    if (n < 0) { neg = 1; n = 0 - n; }
    if (n == 0) { buf[0] = 48; buf[1] = 0; return; }
    while (n > 0) { tmp[i] = 48 + (n % 10); n = n / 10; i = i + 1; }
    if (neg) { buf[j] = 45; j = j + 1; }
    while (i > 0) { i = i - 1; buf[j] = tmp[i]; j = j + 1; }
    buf[j] = 0;
}

void int_to_hex(int n, char* buf) {
    char* hex = ""0123456789ABCDEF"";
    int i = 28;
    int j = 0;
    int started = 0;
    buf[j] = 48; j = j + 1;
    buf[j] = 120; j = j + 1;
    while (i >= 0) {
        int digit = (n >> i) & 0xF;
        if (digit != 0) { started = 1; }
        if (started || i == 0) {
            buf[j] = hex[digit];
            j = j + 1;
        }
        i = i - 4;
    }
    buf[j] = 0;
}

void int_to_hex_full(int n, char* buf) {
    // Always 8 digits: ""0x0000001A""
    char* hex = ""0123456789ABCDEF"";
    int i = 28;
    int j = 0;
    buf[j] = 48; j = j + 1;
    buf[j] = 120; j = j + 1;
    while (i >= 0) {
        buf[j] = hex[(n >> i) & 0xF];
        j = j + 1;
        i = i - 4;
    }
    buf[j] = 0;
}

// ============================================================
// PRINTF / SPRINTF
//
// Supports:  %d  %x  %X  %p  %s  %c  %b  %%
//            %u  (unsigned, falls back to hex if negative)
//
// Width:     %8d   (right-justify in 8 chars)
//            %-8d  (left-justify in 8 chars)
//            %08d  (zero-pad to 8 chars)
//
// Usage:     printf(""Hello %s! Score: %d\n"", name, score);
//            printf(""Addr: %08x\n"", ptr);
//
// Up to 8 arguments after format string.
// ============================================================

// Internal: write int to buffer, returns length written
int _itoa(int n, char* buf, int base, int is_unsigned, int uppercase) {
    char* digits_lower = ""0123456789abcdef"";
    char* digits_upper = ""0123456789ABCDEF"";
    char* digits;
    char tmp[34];
    int i = 0;
    int j = 0;
    int neg = 0;

    if (uppercase) { digits = digits_upper; }
    else { digits = digits_lower; }

    if (n == 0) { buf[0] = 48; buf[1] = 0; return 1; }

    if (is_unsigned == 0 && base == 10 && n < 0) {
        neg = 1;
        n = 0 - n;
    }

    // For hex/bin/unsigned: treat as unsigned via masking
    if (base == 16) {
        // Extract digits from unsigned value
        while (n != 0 && i < 32) {
            tmp[i] = digits[n & 0xF];
            // Unsigned right shift: shift then mask off sign extension
            n = (n >> 4) & 0x0FFFFFFF;
            i = i + 1;
        }
    } else if (base == 2) {
        while (n != 0 && i < 32) {
            tmp[i] = digits[n & 1];
            n = (n >> 1) & 0x7FFFFFFF;
            i = i + 1;
        }
    } else {
        // Base 10
        while (n != 0) {
            tmp[i] = digits[n % 10];
            n = n / 10;
            i = i + 1;
        }
    }

    if (neg) { buf[j] = 45; j = j + 1; }
    while (i > 0) { i = i - 1; buf[j] = tmp[i]; j = j + 1; }
    buf[j] = 0;
    return j;
}

// sprintf: format to buffer. Returns length.
int sprintf(char* out, char* fmt,
            int a0, int a1, int a2, int a3,
            int a4, int a5, int a6, int a7) {
    int args[8];
    int ai = 0;
    int fi = 0;
    int oi = 0;
    int width;
    int zero_pad;
    int left_align;
    int len;
    int pad;
    char numbuf[34];
    char* s;

    args[0] = a0; args[1] = a1; args[2] = a2; args[3] = a3;
    args[4] = a4; args[5] = a5; args[6] = a6; args[7] = a7;

    while (fmt[fi] != 0) {
        if (fmt[fi] == 37) {
            // '%' character
            fi = fi + 1;
            width = 0;
            zero_pad = 0;
            left_align = 0;

            // Parse flags
            if (fmt[fi] == 45) { left_align = 1; fi = fi + 1; }
            if (fmt[fi] == 48) { zero_pad = 1; fi = fi + 1; }

            // Parse width
            while (fmt[fi] >= 48 && fmt[fi] <= 57) {
                width = width * 10 + (fmt[fi] - 48);
                fi = fi + 1;
            }

            // Parse specifier
            if (fmt[fi] == 100) {
                // 'd' - signed decimal
                len = _itoa(args[ai], numbuf, 10, 0, 0);
                ai = ai + 1;
                // Padding
                pad = width - len;
                if (pad > 0 && left_align == 0) {
                    while (pad > 0) {
                        if (zero_pad) { out[oi] = 48; }
                        else { out[oi] = 32; }
                        oi = oi + 1; pad = pad - 1;
                    }
                }
                // Number
                len = 0;
                while (numbuf[len] != 0) { out[oi] = numbuf[len]; oi = oi + 1; len = len + 1; }
                // Left-align padding
                pad = width - len;
                if (pad > 0 && left_align) {
                    while (pad > 0) { out[oi] = 32; oi = oi + 1; pad = pad - 1; }
                }
            }
            else if (fmt[fi] == 117) {
                // 'u' - unsigned decimal
                len = _itoa(args[ai], numbuf, 10, 1, 0);
                ai = ai + 1;
                pad = width - len;
                if (pad > 0 && left_align == 0) {
                    while (pad > 0) {
                        if (zero_pad) { out[oi] = 48; } else { out[oi] = 32; }
                        oi = oi + 1; pad = pad - 1;
                    }
                }
                len = 0;
                while (numbuf[len] != 0) { out[oi] = numbuf[len]; oi = oi + 1; len = len + 1; }
                pad = width - len;
                if (pad > 0 && left_align) { while (pad > 0) { out[oi] = 32; oi = oi + 1; pad = pad - 1; } }
            }
            else if (fmt[fi] == 120) {
                // 'x' - hex lowercase
                len = _itoa(args[ai], numbuf, 16, 1, 0);
                ai = ai + 1;
                pad = width - len;
                if (pad > 0 && left_align == 0) {
                    while (pad > 0) {
                        if (zero_pad) { out[oi] = 48; } else { out[oi] = 32; }
                        oi = oi + 1; pad = pad - 1;
                    }
                }
                len = 0;
                while (numbuf[len] != 0) { out[oi] = numbuf[len]; oi = oi + 1; len = len + 1; }
                pad = width - len;
                if (pad > 0 && left_align) { while (pad > 0) { out[oi] = 32; oi = oi + 1; pad = pad - 1; } }
            }
            else if (fmt[fi] == 88) {
                // 'X' - hex uppercase
                len = _itoa(args[ai], numbuf, 16, 1, 1);
                ai = ai + 1;
                pad = width - len;
                if (pad > 0 && left_align == 0) {
                    while (pad > 0) {
                        if (zero_pad) { out[oi] = 48; } else { out[oi] = 32; }
                        oi = oi + 1; pad = pad - 1;
                    }
                }
                len = 0;
                while (numbuf[len] != 0) { out[oi] = numbuf[len]; oi = oi + 1; len = len + 1; }
                pad = width - len;
                if (pad > 0 && left_align) { while (pad > 0) { out[oi] = 32; oi = oi + 1; pad = pad - 1; } }
            }
            else if (fmt[fi] == 112) {
                // 'p' - pointer (0xHEXHEX)
                out[oi] = 48; oi = oi + 1;
                out[oi] = 120; oi = oi + 1;
                len = _itoa(args[ai], numbuf, 16, 1, 0);
                ai = ai + 1;
                // Zero-pad to 8 digits
                pad = 8 - len;
                while (pad > 0) { out[oi] = 48; oi = oi + 1; pad = pad - 1; }
                len = 0;
                while (numbuf[len] != 0) { out[oi] = numbuf[len]; oi = oi + 1; len = len + 1; }
            }
            else if (fmt[fi] == 98) {
                // 'b' - binary
                len = _itoa(args[ai], numbuf, 2, 1, 0);
                ai = ai + 1;
                pad = width - len;
                if (pad > 0 && left_align == 0) {
                    while (pad > 0) {
                        if (zero_pad) { out[oi] = 48; } else { out[oi] = 32; }
                        oi = oi + 1; pad = pad - 1;
                    }
                }
                len = 0;
                while (numbuf[len] != 0) { out[oi] = numbuf[len]; oi = oi + 1; len = len + 1; }
                pad = width - len;
                if (pad > 0 && left_align) { while (pad > 0) { out[oi] = 32; oi = oi + 1; pad = pad - 1; } }
            }
            else if (fmt[fi] == 115) {
                // 's' - string
                s = (char*)args[ai];
                ai = ai + 1;
                if (s == 0) { s = ""(null)""; }
                len = str_len(s);
                pad = width - len;
                if (pad > 0 && left_align == 0) {
                    while (pad > 0) { out[oi] = 32; oi = oi + 1; pad = pad - 1; }
                }
                len = 0;
                while (s[len] != 0) { out[oi] = s[len]; oi = oi + 1; len = len + 1; }
                pad = width - len;
                if (pad > 0 && left_align) {
                    while (pad > 0) { out[oi] = 32; oi = oi + 1; pad = pad - 1; }
                }
            }
            else if (fmt[fi] == 99) {
                // 'c' - character
                out[oi] = args[ai];
                oi = oi + 1;
                ai = ai + 1;
            }
            else if (fmt[fi] == 37) {
                // '%%' - literal percent
                out[oi] = 37;
                oi = oi + 1;
            }
            else {
                // Unknown specifier, copy as-is
                out[oi] = 37; oi = oi + 1;
                out[oi] = fmt[fi]; oi = oi + 1;
            }
        } else {
            // Regular character
            out[oi] = fmt[fi];
            oi = oi + 1;
        }
        fi = fi + 1;
    }
    out[oi] = 0;
    return oi;
}

// printf: formatted print to screen
void printf(char* fmt,
            int a0, int a1, int a2, int a3,
            int a4, int a5, int a6, int a7) {
    char buf[512];
    sprintf(buf, fmt, a0, a1, a2, a3, a4, a5, a6, a7);
    vga_puts(buf);
}

// ============================================================
// HEAP ALLOCATOR (simple bump + free list)
// ============================================================

int heap_base  = 0x100000;    // 1 MB
int heap_ptr   = 0x100000;
int heap_limit = 0x400000;    // 4 MB

char* alloc(int size) {
    size = (size + 3) & 0xFFFFFFFC;   // 4-byte align
    if (size < 8) { size = 8; }       // minimum block
    if (heap_ptr + size > heap_limit) { return (char*)0; }
    char* ptr = (char*)heap_ptr;
    heap_ptr = heap_ptr + size;
    return ptr;
}

void alloc_reset() {
    heap_ptr = heap_base;
}

int alloc_used() {
    return heap_ptr - heap_base;
}

int alloc_free() {
    return heap_limit - heap_ptr;
}

// Allocate and zero-fill
char* zalloc(int size) {
    char* ptr = alloc(size);
    if (ptr != 0) { mem_zero(ptr, size); }
    return ptr;
}

// Allocate and copy string
char* str_dup(char* src) {
    int len = str_len(src);
    char* dst = alloc(len + 1);
    if (dst != 0) { str_cpy(dst, src); }
    return dst;
}

// ============================================================
// KEYBOARD INPUT (PS/2 polling)
// ============================================================

int KB_DATA   = 0x60;
int KB_STATUS = 0x64;

char kb_map[128];
char kb_shift_map[128];
int kb_inited  = 0;
int kb_shifted = 0;   // Tracks shift key state

void kb_init() {
    int i = 0;
    while (i < 128) { kb_map[i] = 0; kb_shift_map[i] = 0; i = i + 1; }

    // Numbers row - normal
    kb_map[2] = 49; kb_map[3] = 50; kb_map[4] = 51;
    kb_map[5] = 52; kb_map[6] = 53; kb_map[7] = 54;
    kb_map[8] = 55; kb_map[9] = 56; kb_map[10] = 57;
    kb_map[11] = 48;
    kb_map[12] = 45; kb_map[13] = 61;
    kb_map[14] = 8;  kb_map[15] = 9;

    // Numbers row - shifted: !@#$%^&*()_+
    kb_shift_map[2] = 33;  kb_shift_map[3] = 64;  kb_shift_map[4] = 35;
    kb_shift_map[5] = 36;  kb_shift_map[6] = 37;  kb_shift_map[7] = 94;
    kb_shift_map[8] = 38;  kb_shift_map[9] = 42;  kb_shift_map[10] = 40;
    kb_shift_map[11] = 41;
    kb_shift_map[12] = 95; kb_shift_map[13] = 43;
    kb_shift_map[14] = 8;  kb_shift_map[15] = 9;

    // QWERTY - lowercase
    kb_map[16] = 113; kb_map[17] = 119; kb_map[18] = 101;
    kb_map[19] = 114; kb_map[20] = 116; kb_map[21] = 121;
    kb_map[22] = 117; kb_map[23] = 105; kb_map[24] = 111;
    kb_map[25] = 112;
    kb_map[26] = 91;  kb_map[27] = 93;
    kb_map[28] = 10;

    // QWERTY - uppercase
    kb_shift_map[16] = 81;  kb_shift_map[17] = 87;  kb_shift_map[18] = 69;
    kb_shift_map[19] = 82;  kb_shift_map[20] = 84;  kb_shift_map[21] = 89;
    kb_shift_map[22] = 85;  kb_shift_map[23] = 73;  kb_shift_map[24] = 79;
    kb_shift_map[25] = 80;
    kb_shift_map[26] = 123; kb_shift_map[27] = 125;
    kb_shift_map[28] = 10;

    // ASDF - lowercase
    kb_map[30] = 97;  kb_map[31] = 115; kb_map[32] = 100;
    kb_map[33] = 102; kb_map[34] = 103; kb_map[35] = 104;
    kb_map[36] = 106; kb_map[37] = 107; kb_map[38] = 108;
    kb_map[39] = 59;  kb_map[40] = 39;  kb_map[41] = 96;
    kb_map[43] = 92;

    // ASDF - uppercase
    kb_shift_map[30] = 65;  kb_shift_map[31] = 83;  kb_shift_map[32] = 68;
    kb_shift_map[33] = 70;  kb_shift_map[34] = 71;  kb_shift_map[35] = 72;
    kb_shift_map[36] = 74;  kb_shift_map[37] = 75;  kb_shift_map[38] = 76;
    kb_shift_map[39] = 58;  kb_shift_map[40] = 34;  kb_shift_map[41] = 126;
    kb_shift_map[43] = 124;

    // ZXCV - lowercase
    kb_map[44] = 122; kb_map[45] = 120; kb_map[46] = 99;
    kb_map[47] = 118; kb_map[48] = 98;  kb_map[49] = 110;
    kb_map[50] = 109;
    kb_map[51] = 44;  kb_map[52] = 46;  kb_map[53] = 47;

    // ZXCV - uppercase
    kb_shift_map[44] = 90;  kb_shift_map[45] = 88;  kb_shift_map[46] = 67;
    kb_shift_map[47] = 86;  kb_shift_map[48] = 66;  kb_shift_map[49] = 78;
    kb_shift_map[50] = 77;
    kb_shift_map[51] = 60;  kb_shift_map[52] = 62;  kb_shift_map[53] = 63;

    // Space
    kb_map[57] = 32;
    kb_shift_map[57] = 32;

    kb_inited = 1;
}

int kb_haskey() {
    return inb(KB_STATUS) & 1;
}

char kb_scancode() {
    while (kb_haskey() == 0) { }
    return inb(KB_DATA);
}

char kb_getc() {
    if (kb_inited == 0) { kb_init(); }
    int scan;
    char ascii;
    while (1) {
        scan = kb_scancode();
        // Track shift state
        if (scan == 42 || scan == 54) { kb_shifted = 1; continue; }
        if (scan == 170 || scan == 182) { kb_shifted = 0; continue; }
        if (scan & 0x80) { continue; }   // Key release
        if (kb_shifted) {
            ascii = kb_shift_map[scan & 0x7F];
        } else {
            ascii = kb_map[scan & 0x7F];
        }
        if (ascii != 0) { return ascii; }
    }
}

// Non-blocking scan - returns 0 if no key
int kb_scan() {
    if ((inb(KB_STATUS) & 1) == 0) { return 0; }
    int key = inb(KB_DATA);
    // Track shift state even in non-blocking mode
    if (key == 42 || key == 54) { kb_shifted = 1; return 0; }
    if (key == 170 || key == 182) { kb_shifted = 0; return 0; }
    if (key & 0x80) { return 0; }
    return key;
}

void kb_flush() {
    while (inb(KB_STATUS) & 1) { inb(KB_DATA); }
}

int kb_wait() {
    int key;
    kb_flush();
    delay(100);
    kb_flush();
    while (1) {
        while ((inb(KB_STATUS) & 1) == 0) { }
        key = inb(KB_DATA);
        if ((key & 0x80) == 0) { return key; }
    }
    return 0;
}

// Read a line of text with echo. Supports backspace.
void kb_getline(char* buf, int maxlen) {
    int i = 0;
    char c;
    if (kb_inited == 0) { kb_init(); }
    while (i < maxlen - 1) {
        c = kb_getc();
        if (c == 10 || c == 13) { vga_newline(); break; }
        else if (c == 8) {
            if (i > 0) { i = i - 1; vga_putc(8); }
        } else {
            buf[i] = c;
            i = i + 1;
            vga_putc(c);
        }
    }
    buf[i] = 0;
}

// Read a line with prompt
void input(char* prompt, char* buf, int maxlen) {
    vga_puts(prompt);
    kb_getline(buf, maxlen);
}

// ============================================================
// TIMING / DELAY
// ============================================================

// Busy-wait delay (safe to use alongside speaker/PIT channel 2)
// Calibrated for ~1ms per unit on typical x86 emulators (QEMU/Bochs)
void delay(int ms) {
    int i = 0;
    while (i < ms) {
        int j = 0;
        while (j < 5000) { j = j + 1; }
        i = i + 1;
    }
}

// PIT-based precise delay (do NOT use while speaker is active)
// Uses PIT channel 2 in one-shot mode for accurate 1ms ticks
void delay_pit(int ms) {
    int i = 0;
    int port61;
    while (i < ms) {
        port61 = inb(0x61);
        outb(0x61, (port61 & 0xFC) | 1);      // Gate high, speaker off
        outb(0x43, 0xB0);                       // Ch2, lobyte/hibyte, mode 0
        outb(0x42, 0xA9);                       // Low byte: 1193 (0x04A9)
        outb(0x42, 0x04);                       // High byte
        while ((inb(0x61) & 0x20) == 0) { }    // Wait for terminal count
        outb(0x61, port61);                      // Restore port state
        i = i + 1;
    }
}

void util_delay(int count) { delay(count); }

void sleep(int seconds) {
    delay(seconds * 1000);
}

// ============================================================
// RANDOM NUMBER GENERATOR (LCG)
// ============================================================

int rand_seed = 12345;

void srand(int seed) { rand_seed = seed; }
void rng_srand(int seed) { rand_seed = seed; }

int rand() {
    rand_seed = rand_seed * 1103515245 + 12345;
    return (rand_seed >> 16) & 0x7FFF;
}

int rng_rand() { return rand(); }

int randint(int min, int max) {
    int range = max - min + 1;
    return min + (rand() % range);
}

// ============================================================
// SOUND (PC Speaker via PIT Channel 2)
// ============================================================

void speaker_on() {
    int tmp = inb(0x61);
    if ((tmp & 3) != 3) { outb(0x61, tmp | 3); }
}

void speaker_off() {
    int tmp = inb(0x61);
    outb(0x61, tmp & 0xFC);
}

void play_tone(int freq, int duration) {
    int divisor;
    if (freq == 0) { delay(duration); return; }
    divisor = 1193180 / freq;
    outb(0x43, 0xB6);
    outb(0x42, divisor & 0xFF);
    outb(0x42, divisor >> 8);
    speaker_on();
    delay(duration);
    speaker_off();
}

// Game sound effects
void snd_click()   { play_tone(1000, 10); }
void snd_drop()    { play_tone(200, 30); }
void snd_error()   { play_tone(150, 100); }
void snd_success() { play_tone(880, 50); play_tone(1047, 100); }
void snd_beep()    { play_tone(440, 200); }

void snd_clear() {
    play_tone(880, 50);
    play_tone(988, 50);
    play_tone(1047, 100);
}

void snd_levelup() {
    play_tone(523, 50);
    play_tone(659, 50);
    play_tone(784, 50);
    play_tone(1047, 100);
}

void snd_gameover() {
    play_tone(392, 200);
    play_tone(330, 200);
    play_tone(262, 400);
}

// ============================================================
// UTILITY / MATH
// ============================================================

int util_abs(int n)      { if (n < 0) { return 0 - n; } return n; }
int util_min(int a, int b) { if (a < b) return a; return b; }
int util_max(int a, int b) { if (a > b) return a; return b; }
int util_clamp(int n, int lo, int hi) {
    if (n < lo) return lo;
    if (n > hi) return hi;
    return n;
}

void util_swap(int* a, int* b) {
    int tmp = *a;
    *a = *b;
    *b = tmp;
}

// ============================================================
// CHARACTER FUNCTIONS
// ============================================================

int is_digit(char c) { return c >= 48 && c <= 57; }
int is_alpha(char c) { return (c >= 65 && c <= 90) || (c >= 97 && c <= 122); }
int is_alnum(char c) { return is_digit(c) || is_alpha(c); }
int is_space(char c) { return c == 32 || c == 9 || c == 10 || c == 13; }
int is_upper(char c) { return c >= 65 && c <= 90; }
int is_lower(char c) { return c >= 97 && c <= 122; }
int is_print(char c) { return c >= 32 && c <= 126; }
int is_hex(char c)   { return is_digit(c) || (c >= 65 && c <= 70) || (c >= 97 && c <= 102); }
char to_upper(char c) { if (is_lower(c)) { return c - 32; } return c; }
char to_lower(char c) { if (is_upper(c)) { return c + 32; } return c; }

// ============================================================
// DEBUG / PANIC
// ============================================================

void panic(char* msg) {
    vga_setcolor(WHITE, RED);
    vga_clear();
    vga_setpos(0, 0);
    vga_println(""=== KERNEL PANIC ==="");
    vga_println(msg);
    vga_println("""");
    vga_println(""System halted."");
    halt();
}

void assert(int cond, char* msg) {
    if (cond == 0) { panic(msg); }
}

// Hex dump memory region to screen
void hex_dump(char* addr, int count) {
    char* hex = ""0123456789ABCDEF"";
    int i = 0;
    int j;
    char c;
    while (i < count) {
        // Address
        vga_puthex_full((int)addr + i);
        vga_puts("": "");
        // Hex bytes (16 per line)
        j = 0;
        while (j < 16 && (i + j) < count) {
            c = addr[i + j];
            vga_putc(hex[(c >> 4) & 0xF]);
            vga_putc(hex[c & 0xF]);
            vga_putc(32);
            if (j == 7) { vga_putc(32); }   // Extra space at midpoint
            j = j + 1;
        }
        // Pad if short line
        while (j < 16) {
            vga_puts(""   "");
            if (j == 7) { vga_putc(32); }
            j = j + 1;
        }
        // ASCII
        vga_puts("" |"");
        j = 0;
        while (j < 16 && (i + j) < count) {
            c = addr[i + j];
            if (c >= 32 && c <= 126) { vga_putc(c); }
            else { vga_putc(46); }
            j = j + 1;
        }
        vga_puts(""|"");
        vga_newline();
        i = i + 16;
    }
}

// ============================================================
// SIMPLE SORT (insertion sort, works on int arrays)
// ============================================================

void sort(int* arr, int count) {
    int i = 1;
    int j;
    int key;
    while (i < count) {
        key = arr[i];
        j = i - 1;
        while (j >= 0 && arr[j] > key) {
            arr[j + 1] = arr[j];
            j = j - 1;
        }
        arr[j + 1] = key;
        i = i + 1;
    }
}

#endif
";

        #region RichTextBox Helpers

        private void SetOutputText(string text)
        {
            OutputConsole.Document.Blocks.Clear();
            AppendOutput(text);
        }

        private void ClearOutput()
        {
            OutputConsole.Document.Blocks.Clear();
        }

        private void AppendOutput(string text)
        {
            var paragraph = OutputConsole.Document.Blocks.LastBlock as Paragraph;
            if (paragraph == null)
            {
                paragraph = new Paragraph();
                paragraph.Margin = new Thickness(0);
                OutputConsole.Document.Blocks.Add(paragraph);
            }

            // Parse text line by line for coloring
            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var run = new Run(i < lines.Length - 1 ? line + "\n" : line);

                // Color based on content
                string lower = line.ToLower();
                bool hasRealError = lower.Contains("✗") || lower.Contains("[!]") || lower.Contains("failed") ||
                    System.Text.RegularExpressions.Regex.IsMatch(lower, @"(?<![_a-z])error(?![_a-z])");
                bool hasRealWarning = lower.Contains("⚠") ||
                    System.Text.RegularExpressions.Regex.IsMatch(lower, @"(?<![_a-z])warning(?![_a-z])");

                if (hasRealError)
                {
                    run.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F44747")); // Red
                }
                else if (hasRealWarning)
                {
                    run.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCA700")); // Yellow
                }
                else if (lower.Contains("success") || lower.Contains("✓") || lower.Contains("[+]") || lower.Contains("complete"))
                {
                    run.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4EC9B0")); // Green
                }
                else if (line.StartsWith("━") || line.StartsWith("╔") || line.StartsWith("║") || line.StartsWith("╚"))
                {
                    run.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#569CD6")); // Blue for headers
                }
                else if (lower.Contains("[cc]") || lower.Contains("[asm]") || lower.Contains("[img]") || lower.Contains("[run]"))
                {
                    run.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9CDCFE")); // Light blue for steps
                }
                else if (line.TrimStart().StartsWith("Generated:") || line.TrimStart().StartsWith("kernel.bin:") || line.TrimStart().StartsWith("bootloader.bin:") || line.TrimStart().StartsWith("os-image.bin:") || line.TrimStart().StartsWith("Build time:"))
                {
                    run.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B5CEA8")); // Green for metrics
                }
                else
                {
                    run.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCCCCC")); // Default
                }

                paragraph.Inlines.Add(run);
            }
        }

        #endregion

        public MainWindow()
        {
            InitializeComponent();
            LoadSyntaxHighlighting();

            CodeEditor.Text = @"// Welcome to OS Dev IDE
// Create or open a project to get started

// Keyboard Shortcuts:
// Ctrl+F         - Find
// Ctrl+H         - Replace  
// Ctrl+G         - Go to Line
// Ctrl+/         - Comment/Uncomment
// Ctrl+D         - Duplicate Line
// Ctrl+Scroll    - Zoom In/Out
// Ctrl+S         - Save File
";
            ClearOutput();

            // Initialize IntelliSense for SubsetC
            _completion = new SubsetCCompletion(CodeEditor);

            // Install struct type syntax highlighter
            _structHighlighter = new StructTypeHighlighter(_completion);
            CodeEditor.TextArea.TextView.LineTransformers.Add(_structHighlighter);

            // Add zoom functionality with Ctrl+Scroll
            CodeEditor.PreviewMouseWheel += CodeEditor_MouseWheel;

            // Add Find/Replace with Ctrl+F and Ctrl+H
            CodeEditor.PreviewKeyDown += CodeEditor_KeyDown;

            // Update status bar with line/column position
            CodeEditor.TextArea.Caret.PositionChanged += (s, e) => UpdateLineColumnStatus();

            // Add code folding with chevrons
            InitializeCodeFolding();

            // Fix inverted horizontal scrollbar in AvalonEdit
            CodeEditor.Loaded += (s, ev) => FixHorizontalScrollbar(CodeEditor);
        }

        /// <summary>
        /// Fix AvalonEdit horizontal scrollbar being inverted.
        /// The AvalonEditScrollBar style sets IsDirectionReversed=True on the Track,
        /// which is correct for vertical but inverts horizontal. This walks the visual 
        /// tree and fixes it.
        /// </summary>
        private void FixHorizontalScrollbar(System.Windows.DependencyObject root)
        {
            try
            {
                // Find all ScrollBars in the visual tree
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
                {
                    var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);

                    if (child is System.Windows.Controls.Primitives.ScrollBar sb &&
                        sb.Orientation == Orientation.Horizontal)
                    {
                        // Find the Track inside this ScrollBar and fix IsDirectionReversed
                        FixTrackDirection(sb);
                    }

                    // Recurse
                    FixHorizontalScrollbar(child);
                }
            }
            catch { }
        }

        private void FixTrackDirection(System.Windows.DependencyObject root)
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is System.Windows.Controls.Primitives.Track track)
                {
                    track.IsDirectionReversed = false;
                    return;
                }
                FixTrackDirection(child);
            }
        }

        private ICSharpCode.AvalonEdit.Folding.FoldingManager foldingManager;
        private System.Windows.Threading.DispatcherTimer foldingUpdateTimer;
        private SubsetCCompletion _completion;
        private StructTypeHighlighter _structHighlighter;

        private void InitializeCodeFolding()
        {
            // Install folding manager FIRST - this adds a default FoldingMargin
            foldingManager = ICSharpCode.AvalonEdit.Folding.FoldingManager.Install(CodeEditor.TextArea);

            // NOW remove the default box-style margin that Install() just added
            var defaultMargin = CodeEditor.TextArea.LeftMargins.OfType<ICSharpCode.AvalonEdit.Folding.FoldingMargin>().FirstOrDefault();
            if (defaultMargin != null)
            {
                CodeEditor.TextArea.LeftMargins.Remove(defaultMargin);
            }

            // Add our custom chevron-only margin (NO BOXES)
            var chevronMargin = new ChevronFoldingMargin(foldingManager);
            CodeEditor.TextArea.LeftMargins.Insert(0, chevronMargin);

            // CRITICAL: Redraw chevrons when scrolling
            CodeEditor.TextArea.TextView.ScrollOffsetChanged += (s, e) =>
            {
                chevronMargin.InvalidateVisual();
            };

            // Update foldings periodically
            foldingUpdateTimer = new System.Windows.Threading.DispatcherTimer();
            foldingUpdateTimer.Interval = TimeSpan.FromMilliseconds(500);
            foldingUpdateTimer.Tick += (s, e) =>
            {
                foldingUpdateTimer.Stop();
                if (foldingManager != null && CodeEditor.Document != null)
                {
                    var strategy = new BraceFoldingStrategy();
                    strategy.UpdateFoldings(foldingManager, CodeEditor.Document);
                    chevronMargin.InvalidateVisual();

                    // Re-parse user code for IntelliSense
                    string ext = !string.IsNullOrEmpty(currentFile) ? Path.GetExtension(currentFile).ToLower() : "";
                    if (ext == ".c" || ext == ".h")
                    {
                        _completion?.ParseUserCode(CodeEditor.Text);
                        // Redraw to pick up new struct type highlighting
                        CodeEditor.TextArea.TextView.Redraw();
                    }
                }
            };

            CodeEditor.TextChanged += (s, e) =>
            {
                foldingUpdateTimer.Stop();
                foldingUpdateTimer.Start();
            };

            // Trigger initial folding update
            foldingUpdateTimer.Start();
        }

        private void UpdateLineColumnStatus()
        {
            var line = CodeEditor.TextArea.Caret.Line;
            var column = CodeEditor.TextArea.Caret.Column;
            StatusText.Text = $"Ln {line}, Col {column}";
        }

        private void CodeEditor_KeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+F for Find
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                ShowCustomSearchPanel();
                e.Handled = true;
            }
            // Ctrl+H for Replace
            else if (e.Key == Key.H && Keyboard.Modifiers == ModifierKeys.Control)
            {
                ShowCustomSearchPanel(true);
                e.Handled = true;
            }
            // Ctrl+G for Go to Line
            else if (e.Key == Key.G && Keyboard.Modifiers == ModifierKeys.Control)
            {
                ShowGoToLineDialog();
                e.Handled = true;
            }
            // Ctrl+/ or Ctrl+K,Ctrl+C for Comment/Uncomment
            else if ((e.Key == Key.OemQuestion && Keyboard.Modifiers == ModifierKeys.Control) ||
                     (e.Key == Key.Divide && Keyboard.Modifiers == ModifierKeys.Control))
            {
                ToggleComment();
                e.Handled = true;
            }
            // Ctrl+D for Duplicate Line
            else if (e.Key == Key.D && Keyboard.Modifiers == ModifierKeys.Control)
            {
                DuplicateLine();
                e.Handled = true;
            }
            // F5 to Refresh Explorer
            else if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None)
            {
                RefreshFileTree();
                StatusText.Text = "Explorer refreshed";
                e.Handled = true;
            }
        }

        private void ToggleComment()
        {
            if (CodeEditor.SelectionLength > 0)
            {
                // Comment/uncomment selection
                var start = CodeEditor.SelectionStart;
                var end = CodeEditor.SelectionStart + CodeEditor.SelectionLength;
                var startLine = CodeEditor.Document.GetLineByOffset(start);
                var endLine = CodeEditor.Document.GetLineByOffset(end);

                using (CodeEditor.Document.RunUpdate())
                {
                    for (int i = startLine.LineNumber; i <= endLine.LineNumber; i++)
                    {
                        var line = CodeEditor.Document.GetLineByNumber(i);
                        var lineText = CodeEditor.Document.GetText(line.Offset, line.Length);
                        var trimmed = lineText.TrimStart();

                        if (trimmed.StartsWith("//"))
                        {
                            // Uncomment
                            var index = lineText.IndexOf("//");
                            CodeEditor.Document.Remove(line.Offset + index, 2);
                        }
                        else
                        {
                            // Comment
                            var firstNonWhitespace = lineText.Length - trimmed.Length;
                            CodeEditor.Document.Insert(line.Offset + firstNonWhitespace, "//");
                        }
                    }
                }
            }
            else
            {
                // Comment/uncomment current line
                var line = CodeEditor.Document.GetLineByOffset(CodeEditor.CaretOffset);
                var lineText = CodeEditor.Document.GetText(line.Offset, line.Length);
                var trimmed = lineText.TrimStart();

                if (trimmed.StartsWith("//"))
                {
                    var index = lineText.IndexOf("//");
                    CodeEditor.Document.Remove(line.Offset + index, 2);
                }
                else
                {
                    var firstNonWhitespace = lineText.Length - trimmed.Length;
                    CodeEditor.Document.Insert(line.Offset + firstNonWhitespace, "//");
                }
            }
        }

        private void DuplicateLine()
        {
            var line = CodeEditor.Document.GetLineByOffset(CodeEditor.CaretOffset);
            var lineText = CodeEditor.Document.GetText(line.Offset, line.Length);
            CodeEditor.Document.Insert(line.EndOffset, "\n" + lineText);
        }

        private Window searchWindow;

        private void ShowCustomSearchPanel(bool showReplace = false)
        {
            if (searchWindow != null && searchWindow.IsVisible)
            {
                searchWindow.Focus();
                return;
            }

            searchWindow = new Window
            {
                Title = showReplace ? "Find and Replace" : "Find",
                Width = 500,
                Height = showReplace ? 180 : 120,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D2D30")),
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                Topmost = true
            };

            var grid = new Grid { Margin = new Thickness(12, 12, 12, 12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (showReplace) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Find textbox
            var findBox = new TextBox
            {
                Height = 28,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3C3C3C")),
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCCCCC")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#555555")),
                FontSize = 13,
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(findBox, 0);

            TextBox replaceBox = null;
            if (showReplace)
            {
                replaceBox = new TextBox
                {
                    Height = 28,
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3C3C3C")),
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCCCCC")),
                    BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#555555")),
                    FontSize = 13,
                    Padding = new Thickness(6, 4, 6, 4),
                    Margin = new Thickness(0, 0, 0, 8)
                };
                Grid.SetRow(replaceBox, 1);
            }

            // Buttons
            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

            var findNextBtn = new Button
            {
                Content = "Find Next",
                Width = 100,
                Height = 28,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0E639C")),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0, 0, 0, 0),
                FontSize = 12,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 8, 0)
            };

            var closeBtn = new Button
            {
                Content = "Close",
                Width = 80,
                Height = 28,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3E3E42")),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0, 0, 0, 0),
                FontSize = 12,
                Cursor = Cursors.Hand
            };

            findNextBtn.Click += (s, e) =>
            {
                if (!string.IsNullOrEmpty(findBox.Text))
                {
                    int startPos = CodeEditor.SelectionStart + CodeEditor.SelectionLength;
                    int pos = CodeEditor.Text.IndexOf(findBox.Text, startPos, StringComparison.OrdinalIgnoreCase);

                    if (pos < 0 && startPos > 0)
                    {
                        pos = CodeEditor.Text.IndexOf(findBox.Text, 0, StringComparison.OrdinalIgnoreCase);
                    }

                    if (pos >= 0)
                    {
                        CodeEditor.Select(pos, findBox.Text.Length);
                        CodeEditor.ScrollTo(CodeEditor.Document.GetLineByOffset(pos).LineNumber, 0);
                    }
                }
            };

            closeBtn.Click += (s, e) => searchWindow.Close();

            buttonPanel.Children.Add(findNextBtn);

            if (showReplace && replaceBox != null)
            {
                var replaceBtn = new Button
                {
                    Content = "Replace",
                    Width = 80,
                    Height = 28,
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0E639C")),
                    Foreground = Brushes.White,
                    BorderThickness = new Thickness(0, 0, 0, 0),
                    FontSize = 12,
                    Cursor = Cursors.Hand,
                    Margin = new Thickness(0, 0, 8, 0)
                };

                replaceBtn.Click += (s, e) =>
                {
                    if (CodeEditor.SelectionLength > 0 && CodeEditor.SelectedText == findBox.Text)
                    {
                        CodeEditor.Document.Replace(CodeEditor.SelectionStart, CodeEditor.SelectionLength, replaceBox.Text);
                    }
                };

                buttonPanel.Children.Add(replaceBtn);
            }

            buttonPanel.Children.Add(closeBtn);
            Grid.SetRow(buttonPanel, showReplace ? 2 : 1);

            grid.Children.Add(findBox);
            if (showReplace && replaceBox != null) grid.Children.Add(replaceBox);
            grid.Children.Add(buttonPanel);

            searchWindow.Content = grid;
            searchWindow.Closed += (s, e) => searchWindow = null;
            searchWindow.Show();
            findBox.Focus();
        }

        private void ShowGoToLineDialog()
        {
            var dialog = CreateDarkInputDialog("Go to Line", "Enter line number:");
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Tag as string))
            {
                if (int.TryParse(dialog.Tag as string, out int lineNumber))
                {
                    if (lineNumber > 0 && lineNumber <= CodeEditor.Document.LineCount)
                    {
                        var line = CodeEditor.Document.GetLineByNumber(lineNumber);
                        CodeEditor.Select(line.Offset, line.Length);
                        CodeEditor.ScrollToLine(lineNumber);
                    }
                    else
                    {
                        ShowDarkMessageBox($"Line number must be between 1 and {CodeEditor.Document.LineCount}", "Invalid Line Number");
                    }
                }
            }
        }

        private void CodeEditor_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (e.Delta > 0)
                {
                    // Zoom in
                    if (CodeEditor.FontSize < 72)
                        CodeEditor.FontSize += 1;
                }
                else
                {
                    // Zoom out
                    if (CodeEditor.FontSize > 8)
                        CodeEditor.FontSize -= 1;
                }
                e.Handled = true;
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
                MaximizeWindow_Click(sender, null);
            else
                DragMove();
        }

        private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void MaximizeWindow_Click(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
                MaxWidth = double.PositiveInfinity;
                MaxHeight = double.PositiveInfinity;
            }
            else
            {
                // Set max dimensions to work area to prevent taskbar overlap
                MaxWidth = SystemParameters.MaximizedPrimaryScreenWidth;
                MaxHeight = SystemParameters.MaximizedPrimaryScreenHeight;
                WindowState = WindowState.Maximized;
            }
        }

        private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

        private void Tools_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Window
            {
                Title = "Tools",
                Width = 400,
                Height = 320,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D2D30")),
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None
            };

            var grid = new Grid { Margin = new Thickness(20, 20, 20, 20) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var title = new TextBlock
            {
                Text = "Tools",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 20)
            };
            Grid.SetRow(title, 0);

            var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

            var hexEditorBtn = new Button
            {
                Content = "📝 Hex Editor",
                Height = 40,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0E639C")),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0, 0, 0, 0),
                FontSize = 14,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 0, 10)
            };
            hexEditorBtn.Click += (s, args) => { dialog.Close(); OpenHexEditor(); };

            var disassemblerBtn = new Button
            {
                Content = "🔧 x86 Disassembler",
                Height = 40,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0E639C")),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0, 0, 0, 0),
                FontSize = 14,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 0, 10)
            };
            disassemblerBtn.Click += (s, args) => { dialog.Close(); OpenDisassembler(); };

            stack.Children.Add(hexEditorBtn);
            stack.Children.Add(disassemblerBtn);
            Grid.SetRow(stack, 1);

            var closeBtn = new Button
            {
                Content = "Close",
                Width = 100,
                Height = 32,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3E3E42")),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0, 0, 0, 0),
                FontSize = 13,
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            closeBtn.Click += (s, args) => dialog.Close();
            Grid.SetRow(closeBtn, 2);

            grid.Children.Add(title);
            grid.Children.Add(stack);
            grid.Children.Add(closeBtn);
            dialog.Content = grid;
            dialog.ShowDialog();
        }

        private void Extensions_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Window
            {
                Title = "Extensions",
                Width = 450,
                Height = 450,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D2D30")),
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None
            };

            var grid = new Grid { Margin = new Thickness(20, 20, 20, 20) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var title = new TextBlock
            {
                Text = "Extensions",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 20)
            };
            Grid.SetRow(title, 0);

            var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

            // Ollama AI - WORKING
            var ollamaBtn = new Button
            {
                Content = "✨ Ollama AI Assistant",
                Height = 50,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#007ACC")),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 0, 10),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(15, 0, 0, 0)
            };
            ollamaBtn.Click += (s, args) => { dialog.Close(); OpenOllamaWindow(); };

            // Bootloader Generator
            var bootGenBtn = new Button
            {
                Content = "⚙️ Bootloader Generator",
                Height = 45,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3E3E42")),
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCCCCC")),
                BorderThickness = new Thickness(0),
                FontSize = 14,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 0, 10),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(15, 0, 0, 0)
            };
            bootGenBtn.Click += (s, args) => {
                dialog.Close();
                var bootGen = new BootloaderGenerator(projectPath, (filepath) => {
                    RefreshExplorer();
                    OpenFile(filepath);
                });
                bootGen.Owner = this;
                bootGen.Show();
            };

            // Memory Viewer (placeholder)
            var memViewBtn = new Button
            {
                Content = "💾 Memory Viewer",
                Height = 45,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3E3E42")),
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#858585")),
                BorderThickness = new Thickness(0),
                FontSize = 14,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 0, 10),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(15, 0, 0, 0)
            };
            memViewBtn.Click += (s, args) => { ShowDarkMessageBox("Memory Viewer - Coming Soon!", "OS Dev IDE"); };

            var infoText = new TextBlock
            {
                Text = "Ollama AI knows everything about x86, bootloaders, your compiler, debugging...",
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#888888")),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 15, 0, 0)
            };

            stack.Children.Add(ollamaBtn);
            stack.Children.Add(bootGenBtn);
            stack.Children.Add(memViewBtn);
            stack.Children.Add(infoText);
            Grid.SetRow(stack, 1);

            var closeBtn = new Button
            {
                Content = "Close",
                Width = 100,
                Height = 32,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3E3E42")),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                FontSize = 13,
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            closeBtn.Click += (s, ev) => dialog.Close();
            Grid.SetRow(closeBtn, 2);

            grid.Children.Add(title);
            grid.Children.Add(stack);
            grid.Children.Add(closeBtn);
            dialog.Content = grid;
            dialog.ShowDialog();
        }

        private void LoadSyntaxHighlighting()
        {
            string cSyntax = @"<?xml version=""1.0""?>
<SyntaxDefinition name=""C"" xmlns=""http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008"">
    <Color name=""Comment"" foreground=""#6A9955"" />
    <Color name=""String"" foreground=""#CE9178"" />
    <Color name=""Char"" foreground=""#CE9178"" />
    <Color name=""Keyword"" foreground=""#569CD6"" fontWeight=""bold"" />
    <Color name=""ControlFlow"" foreground=""#C586C0"" fontWeight=""bold"" />
    <Color name=""Type"" foreground=""#4EC9B0"" />
    <Color name=""Number"" foreground=""#B5CEA8"" />
    <Color name=""Preprocessor"" foreground=""#9B9B9B"" />
    <Color name=""PreprocessorKeyword"" foreground=""#C586C0"" />
    <Color name=""Function"" foreground=""#DCDCAA"" />
    <Color name=""Operator"" foreground=""#D4D4D4"" />
    <Color name=""Punctuation"" foreground=""#D4D4D4"" />
    <Color name=""Member"" foreground=""#9CDCFE"" />
    <Color name=""Macro"" foreground=""#4EC9B0"" />
    <Color name=""Constant"" foreground=""#4FC1FF"" />
    
    <RuleSet>
        <!-- Comments -->
        <Span color=""Comment"" begin=""//"" />
        <Span color=""Comment"" multiline=""true"" begin=""/\*"" end=""\*/"" />
        
        <!-- Strings -->
        <Span color=""String"" multiline=""true"">
            <Begin>""</Begin>
            <End>""</End>
            <RuleSet>
                <Span begin=""\\"" end=""."" />
            </RuleSet>
        </Span>
        
        <!-- Character literals -->
        <Span color=""Char"">
            <Begin>'</Begin>
            <End>'</End>
            <RuleSet>
                <Span begin=""\\"" end=""."" />
            </RuleSet>
        </Span>
        
        <!-- Preprocessor directives -->
        <Span color=""Preprocessor"" begin=""^\s*#"" end=""$"">
            <RuleSet>
                <Keywords color=""PreprocessorKeyword"">
                    <Word>include</Word>
                    <Word>define</Word>
                    <Word>ifdef</Word>
                    <Word>ifndef</Word>
                    <Word>endif</Word>
                    <Word>if</Word>
                    <Word>else</Word>
                    <Word>elif</Word>
                    <Word>undef</Word>
                    <Word>pragma</Word>
                </Keywords>
                <Span color=""String"">
                    <Begin>""</Begin>
                    <End>""</End>
                </Span>
            </RuleSet>
        </Span>
        
        <!-- Control flow keywords (purple — VS Code style) -->
        <Keywords color=""ControlFlow"">
            <Word>if</Word>
            <Word>else</Word>
            <Word>while</Word>
            <Word>for</Word>
            <Word>do</Word>
            <Word>switch</Word>
            <Word>case</Word>
            <Word>default</Word>
            <Word>break</Word>
            <Word>continue</Word>
            <Word>return</Word>
            <Word>goto</Word>
        </Keywords>
        
        <!-- Storage and modifier keywords (blue) -->
        <Keywords color=""Keyword"">
            <Word>inline</Word>
            <Word>static</Word>
            <Word>extern</Word>
            <Word>volatile</Word>
            <Word>const</Word>
            <Word>register</Word>
            <Word>typedef</Word>
            <Word>sizeof</Word>
            <Word>asm</Word>
            <Word>__asm__</Word>
            <Word>__packed</Word>
            <Word>__attribute__</Word>
        </Keywords>
        
        <!-- Built-in type keywords (blue like VS Code) -->
        <Keywords color=""Keyword"">
            <Word>void</Word>
            <Word>char</Word>
            <Word>short</Word>
            <Word>int</Word>
            <Word>long</Word>
            <Word>float</Word>
            <Word>double</Word>
            <Word>signed</Word>
            <Word>unsigned</Word>
        </Keywords>

        <!-- struct/union/enum keywords (teal) -->
        <Keywords color=""Type"">
            <Word>struct</Word>
            <Word>union</Word>
            <Word>enum</Word>
        </Keywords>

        <!-- Common C constants (bright blue) -->
        <Keywords color=""Constant"">
            <Word>NULL</Word>
            <Word>true</Word>
            <Word>false</Word>
            <Word>TRUE</Word>
            <Word>FALSE</Word>
        </Keywords>

        <!-- ALL_CAPS identifiers = macros/constants (teal) -->
        <Rule color=""Macro"">
            \b[A-Z][A-Z0-9_]{2,}\b
        </Rule>
        
        <!-- Member access: thing->member or thing.member (light blue) -->
        <Rule color=""Member"">
            (?&lt;=[\.\-]&gt;?)[a-zA-Z_]\w*
        </Rule>

        <!-- Numbers (hex, binary, decimal) -->
        <Rule color=""Number"">
            \b0[xX][0-9a-fA-F]+\b|\b0[bB][01]+\b|\b\d+\b
        </Rule>
        
        <!-- Function calls (yellow) -->
        <Rule color=""Function"">
            \b[a-zA-Z_][a-zA-Z0-9_]*(?=\s*\()
        </Rule>
    </RuleSet>
</SyntaxDefinition>";

            using (XmlReader reader = XmlReader.Create(new StringReader(cSyntax)))
            {
                CodeEditor.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            }
        }

        private void ShowDarkMessageBox(string message, string title)
        {
            var dialog = new Window
            {
                Title = title,
                Width = 400,
                Height = 180,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D2D30")),
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None
            };

            var grid = new Grid { Margin = new Thickness(20) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var msgText = new TextBlock
            {
                Text = message,
                FontSize = 14,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCCCCC")),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(msgText, 0);

            var okBtn = new Button
            {
                Content = "OK",
                Width = 100,
                Height = 32,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0E639C")),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                FontSize = 13,
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            okBtn.Click += (s, e) => dialog.Close();
            Grid.SetRow(okBtn, 1);

            grid.Children.Add(msgText);
            grid.Children.Add(okBtn);
            dialog.Content = grid;
            dialog.ShowDialog();
        }

        private void NewProject_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Window
            {
                Title = "New Project",
                Width = 550,
                Height = 240,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D2D30")),
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None
            };

            var grid = new Grid { Margin = new Thickness(20) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var title = new TextBlock
            {
                Text = "Choose Project Template",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 20)
            };
            Grid.SetRow(title, 0);

            var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

            var radio1 = new RadioButton
            {
                Content = "Empty Project (no bootloader)",
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCCCCC")),
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 12),
                GroupName = "template"
            };

            var radio2 = new RadioButton
            {
                Content = "Empty Kernel (with bootloader at 0x1000)",
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCCCCC")),
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 12),
                IsChecked = true,
                GroupName = "template"
            };

            stack.Children.Add(radio1);
            stack.Children.Add(radio2);
            Grid.SetRow(stack, 1);

            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

            var okBtn = new Button
            {
                Content = "Create",
                Width = 100,
                Height = 32,
                Margin = new Thickness(0, 0, 10, 0),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0E639C")),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                FontSize = 13,
                Cursor = Cursors.Hand
            };

            var cancelBtn = new Button
            {
                Content = "Cancel",
                Width = 100,
                Height = 32,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3E3E42")),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                FontSize = 13,
                Cursor = Cursors.Hand
            };

            okBtn.Click += (s, args) => { dialog.DialogResult = true; dialog.Close(); };
            cancelBtn.Click += (s, args) => { dialog.DialogResult = false; dialog.Close(); };

            buttonPanel.Children.Add(okBtn);
            buttonPanel.Children.Add(cancelBtn);
            Grid.SetRow(buttonPanel, 2);

            grid.Children.Add(title);
            grid.Children.Add(stack);
            grid.Children.Add(buttonPanel);
            dialog.Content = grid;

            if (dialog.ShowDialog() == true)
            {
                if (radio2.IsChecked == true)
                {
                    selectedTemplate = "Empty Kernel";
                    projectType = "OS";
                }
                else
                {
                    selectedTemplate = "Empty Project";
                    projectType = "OS";
                }

                var folderDialog = new System.Windows.Forms.FolderBrowserDialog();
                folderDialog.Description = "Select folder for new project";

                if (folderDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    string projName = "MyOSProject";
                    projectPath = Path.Combine(folderDialog.SelectedPath, projName);
                    Directory.CreateDirectory(projectPath);

                    CreateOSProject();

                    RefreshFileTree();
                    SetOutputText($"✓ Project created at {projectPath}\n✓ Template: {selectedTemplate}\n");
                    StatusText.Text = "Project created successfully";
                }
            }
        }

        private void CreateOSProject()
        {
            Directory.CreateDirectory(Path.Combine(projectPath, "Kernel"));
            Directory.CreateDirectory(Path.Combine(projectPath, "Bootloader"));
            Directory.CreateDirectory(Path.Combine(projectPath, "build"));

            // Write stdlib.c
            File.WriteAllText(Path.Combine(projectPath, "Kernel", "stdlib.c"), StdlibC, new System.Text.UTF8Encoding(false));

            // Write kernel.c with include
            File.WriteAllText(Path.Combine(projectPath, "Kernel", "kernel.c"),
                @"// kernel.c - OS Dev IDE Template
// Entry point must be kernel_main()

#include ""stdlib.c""

void kernel_main() {
    // Clear screen with blue background
    vga_setcolor(COLOR_WHITE, COLOR_BLUE);
    vga_clear();
    
    // Print welcome message
    vga_println(""==================================="");
    vga_println(""  Welcome to My Operating System!"");
    vga_println(""==================================="");
    vga_newline();
    
    vga_puts(""Type something: "");
    
    // Read user input
    char input[64];
    kb_getline(input, 64);
    
    vga_puts(""You typed: "");
    vga_println(input);
    
    vga_newline();
    vga_println(""System halted."");
    
    // Halt
    while (1) {
        asm(""hlt"");
    }
}");

            if (selectedTemplate == "Empty Kernel")
            {
                File.WriteAllText(Path.Combine(projectPath, "Bootloader", "bootloader.asm"), BootloaderAsm);
            }

            LoadFile(Path.Combine(projectPath, "Kernel", "kernel.c"));
        }

        private void OpenProject_Click(object sender, RoutedEventArgs e)
        {
            var folderDialog = new System.Windows.Forms.FolderBrowserDialog();
            folderDialog.Description = "Select existing project folder";

            if (folderDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                string selectedPath = folderDialog.SelectedPath;

                string kernelFile = Path.Combine(selectedPath, "Kernel", "kernel.c");
                if (!File.Exists(kernelFile))
                {
                    ShowDarkMessageBox("This doesn't look like a valid project.\n\nExpected:\n- OS Project: Kernel/kernel.c", "Invalid Project");
                    return;
                }

                projectType = "OS";
                projectPath = selectedPath;
                RefreshFileTree();
                LoadFile(kernelFile);

                SetOutputText($"✓ OS project opened: {projectPath}\n");
                StatusText.Text = "OS project opened";
            }
        }

        private void AddFile_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(projectPath))
            {
                ShowDarkMessageBox("Please create or open a project first", "No Project");
                return;
            }

            var dialog = CreateDarkInputDialog("New File", "Enter file name (e.g., main.c or boot.asm):");
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Tag as string))
            {
                string fileName = dialog.Tag as string;
                string targetDir = fileName.EndsWith(".asm") ? Path.Combine(projectPath, "Bootloader") : Path.Combine(projectPath, "Kernel");
                string newFile = Path.Combine(targetDir, fileName);

                if (File.Exists(newFile))
                {
                    ShowDarkMessageBox("File already exists!", "Error");
                    return;
                }

                File.WriteAllText(newFile, "", new System.Text.UTF8Encoding(false));
                RefreshFileTree();
                LoadFile(newFile);
                StatusText.Text = $"Created: {fileName}";
            }
        }

        private void AddFolder_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(projectPath))
            {
                ShowDarkMessageBox("Please create or open a project first", "No Project");
                return;
            }

            var dialog = CreateDarkInputDialog("New Folder", "Enter folder name:");
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Tag as string))
            {
                string folderName = dialog.Tag as string;
                string newFolder = Path.Combine(projectPath, folderName);

                if (Directory.Exists(newFolder))
                {
                    ShowDarkMessageBox("Folder already exists!", "Error");
                    return;
                }

                Directory.CreateDirectory(newFolder);
                RefreshFileTree();
                StatusText.Text = $"Created folder: {folderName}";
            }
        }

        private Window CreateDarkInputDialog(string title, string prompt)
        {
            var dialog = new Window
            {
                Title = title,
                Width = 450,
                Height = 180,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D2D30")),
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None
            };

            var grid = new Grid { Margin = new Thickness(20) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var promptText = new TextBlock
            {
                Text = prompt,
                FontSize = 14,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCCCCC")),
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(promptText, 0);

            var input = new TextBox
            {
                Height = 32,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3C3C3C")),
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCCCCC")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#555555")),
                FontSize = 13,
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 15)
            };
            Grid.SetRow(input, 1);

            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

            var okBtn = new Button
            {
                Content = "OK",
                Width = 100,
                Height = 32,
                Margin = new Thickness(0, 0, 10, 0),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0E639C")),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                FontSize = 13,
                Cursor = Cursors.Hand
            };

            var cancelBtn = new Button
            {
                Content = "Cancel",
                Width = 100,
                Height = 32,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3E3E42")),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                FontSize = 13,
                Cursor = Cursors.Hand
            };

            okBtn.Click += (s, e) => { dialog.Tag = input.Text; dialog.DialogResult = true; dialog.Close(); };
            cancelBtn.Click += (s, e) => { dialog.DialogResult = false; dialog.Close(); };

            // Handle Enter key to submit
            input.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    dialog.Tag = input.Text;
                    dialog.DialogResult = true;
                    dialog.Close();
                }
                else if (e.Key == Key.Escape)
                {
                    dialog.DialogResult = false;
                    dialog.Close();
                }
            };

            buttonPanel.Children.Add(okBtn);
            buttonPanel.Children.Add(cancelBtn);
            Grid.SetRow(buttonPanel, 2);

            grid.Children.Add(promptText);
            grid.Children.Add(input);
            grid.Children.Add(buttonPanel);
            dialog.Content = grid;

            input.Focus();
            return dialog;
        }

        private Window CreateDarkConfirmDialog(string title, string message)
        {
            var dialog = new Window
            {
                Title = title,
                Width = 450,
                Height = 180,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D2D30")),
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None
            };

            var grid = new Grid { Margin = new Thickness(20) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var msgText = new TextBlock
            {
                Text = message,
                FontSize = 14,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCCCCC")),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(msgText, 0);

            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

            var yesBtn = new Button
            {
                Content = "Yes",
                Width = 100,
                Height = 32,
                Margin = new Thickness(0, 0, 10, 0),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C42B1C")),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                FontSize = 13,
                Cursor = Cursors.Hand
            };

            var noBtn = new Button
            {
                Content = "No",
                Width = 100,
                Height = 32,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3E3E42")),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                FontSize = 13,
                Cursor = Cursors.Hand
            };

            yesBtn.Click += (s, e) => { dialog.DialogResult = true; dialog.Close(); };
            noBtn.Click += (s, e) => { dialog.DialogResult = false; dialog.Close(); };

            buttonPanel.Children.Add(yesBtn);
            buttonPanel.Children.Add(noBtn);
            Grid.SetRow(buttonPanel, 1);

            grid.Children.Add(msgText);
            grid.Children.Add(buttonPanel);
            dialog.Content = grid;

            return dialog;
        }

        private bool? ShowBinaryFileDialog(string filePath)
        {
            var dialog = new Window
            {
                Title = "Binary File Detected",
                Width = 450,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D2D30")),
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None
            };

            var grid = new Grid { Margin = new Thickness(20, 20, 20, 20) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var msgText = new TextBlock
            {
                Text = $"'{Path.GetFileName(filePath)}' is a binary file.\n\nWould you like to open it in the Hex Editor?",
                FontSize = 14,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCCCCC")),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(msgText, 0);

            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

            var yesBtn = new Button
            {
                Content = "Open in Hex Editor",
                Width = 140,
                Height = 32,
                Margin = new Thickness(0, 0, 10, 0),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0E639C")),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0, 0, 0, 0),
                FontSize = 13,
                Cursor = Cursors.Hand
            };

            var noBtn = new Button
            {
                Content = "Cancel",
                Width = 100,
                Height = 32,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3E3E42")),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0, 0, 0, 0),
                FontSize = 13,
                Cursor = Cursors.Hand
            };

            yesBtn.Click += (s, e) => { dialog.DialogResult = true; dialog.Close(); };
            noBtn.Click += (s, e) => { dialog.DialogResult = false; dialog.Close(); };

            buttonPanel.Children.Add(yesBtn);
            buttonPanel.Children.Add(noBtn);
            Grid.SetRow(buttonPanel, 1);

            grid.Children.Add(msgText);
            grid.Children.Add(buttonPanel);
            dialog.Content = grid;

            return dialog.ShowDialog();
        }

        private void OpenHexEditorWithFile(string filePath)
        {
            var hexWindow = new Window
            {
                Title = $"Hex Editor - {Path.GetFileName(filePath)}",
                Width = 900,
                Height = 600,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E1E1E")),
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.CanResizeWithGrip
            };

            var mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Dark title bar
            var titleBar = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D2D30")),
                Height = 30,
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3F3F46"))
            };

            var titleGrid = new Grid();
            var titleText = new TextBlock
            {
                Text = $"Hex Editor - {Path.GetFileName(filePath)}",
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCCCCC")),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };

            var closeBtnTitle = new Button
            {
                Content = "✕",
                Width = 46,
                Height = 30,
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCCCCC")),
                BorderThickness = new Thickness(0, 0, 0, 0),
                FontSize = 14,
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            closeBtnTitle.Click += (s, e) => hexWindow.Close();

            // Make title bar draggable
            titleBar.MouseLeftButtonDown += (s, e) => hexWindow.DragMove();

            titleGrid.Children.Add(titleText);
            titleGrid.Children.Add(closeBtnTitle);
            titleBar.Child = titleGrid;
            Grid.SetRow(titleBar, 0);

            var toolbar = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D2D30")),
                Height = 40,
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3F3F46"))
            };

            var toolbarStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };

            var disasmBtn = new Button
            {
                Content = "Open in Disassembler",
                Height = 28,
                Padding = new Thickness(12, 0, 12, 0),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0E639C")),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0, 0, 0, 0),
                FontSize = 12,
                Cursor = Cursors.Hand
            };
            disasmBtn.Click += (s, e) => { hexWindow.Close(); OpenDisassemblerWithFile(filePath); };

            toolbarStack.Children.Add(disasmBtn);
            toolbar.Child = toolbarStack;
            Grid.SetRow(toolbar, 1);

            var hexScroll = new ScrollViewer
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E1E1E")),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            var hexText = new TextBox
            {
                IsReadOnly = true,
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D4D4D4")),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                Padding = new Thickness(12, 12, 12, 12),
                BorderThickness = new Thickness(0, 0, 0, 0),
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            try
            {
                byte[] fileBytes = File.ReadAllBytes(filePath);
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"File: {Path.GetFileName(filePath)}");
                sb.AppendLine($"Size: {fileBytes.Length:N0} bytes (0x{fileBytes.Length:X})");
                sb.AppendLine($"Path: {filePath}");
                sb.AppendLine();
                sb.AppendLine("Offset(h) 00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F  Decoded text");
                sb.AppendLine("═══════════════════════════════════════════════════════════════════════════════");

                for (int i = 0; i < fileBytes.Length; i += 16)
                {
                    sb.Append($"{i:X8}  ");

                    int rowLen = Math.Min(16, fileBytes.Length - i);
                    for (int j = 0; j < 16; j++)
                    {
                        if (j < rowLen)
                            sb.Append($"{fileBytes[i + j]:X2} ");
                        else
                            sb.Append("   ");
                    }

                    sb.Append(" ");
                    for (int j = 0; j < rowLen; j++)
                    {
                        byte b = fileBytes[i + j];
                        sb.Append(b >= 32 && b < 127 ? (char)b : '.');
                    }
                    sb.AppendLine();
                }

                hexText.Text = sb.ToString();
            }
            catch (Exception ex)
            {
                hexText.Text = $"Error reading file: {ex.Message}";
            }

            hexScroll.Content = hexText;
            Grid.SetRow(hexScroll, 2);

            mainGrid.Children.Add(titleBar);
            mainGrid.Children.Add(toolbar);
            mainGrid.Children.Add(hexScroll);
            hexWindow.Content = mainGrid;
            hexWindow.ShowDialog();
        }

        private void OpenHexEditor()
        {
            var fileDialog = new System.Windows.Forms.OpenFileDialog();
            fileDialog.Title = "Select file to view in Hex Editor";

            if (fileDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                OpenHexEditorWithFile(fileDialog.FileName);
            }
        }

        private void OpenDisassembler()
        {
            var fileDialog = new System.Windows.Forms.OpenFileDialog();
            fileDialog.Title = "Select binary file to disassemble";
            fileDialog.Filter = "Binary Files (*.bin)|*.bin|All Files (*.*)|*.*";

            if (fileDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                OpenDisassemblerWithFile(fileDialog.FileName);
            }
        }

        private void OpenDisassemblerWithFile(string filePath)
        {
            var disasmWindow = new Window
            {
                Title = $"x86 Disassembler - {Path.GetFileName(filePath)}",
                Width = 1200,
                Height = 700,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E1E1E")),
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.CanResizeWithGrip
            };

            var mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Custom title bar
            var titleBar = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D2D30")),
                Height = 30,
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3F3F46"))
            };

            var titleGrid = new Grid();
            var titleText = new TextBlock
            {
                Text = $"x86 Disassembler - {Path.GetFileName(filePath)}",
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCCCCC")),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };

            var titleCloseBtn = new Button
            {
                Content = "✕",
                Width = 46,
                Height = 30,
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCCCCC")),
                BorderThickness = new Thickness(0, 0, 0, 0),
                FontSize = 14,
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            titleCloseBtn.Click += (s, e) => disasmWindow.Close();

            // Make title bar draggable
            titleBar.MouseLeftButtonDown += (s, e) => disasmWindow.DragMove();

            titleGrid.Children.Add(titleText);
            titleGrid.Children.Add(titleCloseBtn);
            titleBar.Child = titleGrid;
            Grid.SetRow(titleBar, 0);

            // Toolbar
            var toolbar = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D2D30")),
                Height = 40,
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3F3F46"))
            };

            var toolbarStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };

            var offsetLabel = new TextBlock
            {
                Text = "Start Offset (hex):",
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCCCCC")),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };

            var offsetInput = new TextBox
            {
                Text = "0",
                Width = 100,
                Height = 24,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3C3C3C")),
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCCCCC")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#555555")),
                FontSize = 12,
                Padding = new Thickness(4, 2, 4, 2),
                VerticalAlignment = VerticalAlignment.Center
            };

            var disasmBtn = new Button
            {
                Content = "Disassemble",
                Height = 28,
                Padding = new Thickness(12, 0, 12, 0),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0E639C")),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0, 0, 0, 0),
                FontSize = 12,
                Cursor = Cursors.Hand,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            toolbarStack.Children.Add(offsetLabel);
            toolbarStack.Children.Add(offsetInput);
            toolbarStack.Children.Add(disasmBtn);
            toolbar.Child = toolbarStack;
            Grid.SetRow(toolbar, 1);

            // Split view - Assembly on left, Pseudo-code on right
            var splitGrid = new Grid { Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E1E1E")) };
            splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
            splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Left panel - Assembly instructions
            var leftPanel = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E1E1E")),
                BorderThickness = new Thickness(0, 0, 1, 0),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3F3F46"))
            };

            var leftScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            var asmText = new TextBox
            {
                IsReadOnly = true,
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D4D4D4")),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                Padding = new Thickness(12, 12, 12, 12),
                BorderThickness = new Thickness(0, 0, 0, 0),
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            leftScroll.Content = asmText;
            leftPanel.Child = leftScroll;
            Grid.SetColumn(leftPanel, 0);

            // Splitter
            var splitter = new Border
            {
                Width = 1,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3F3F46"))
            };
            Grid.SetColumn(splitter, 1);

            // Right panel - Pseudo-code / Decompiled view
            var rightPanel = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E1E1E"))
            };

            var rightScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            var pseudoText = new TextBox
            {
                IsReadOnly = true,
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D4D4D4")),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                Padding = new Thickness(12, 12, 12, 12),
                BorderThickness = new Thickness(0, 0, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            rightScroll.Content = pseudoText;
            rightPanel.Child = rightScroll;
            Grid.SetColumn(rightPanel, 2);

            splitGrid.Children.Add(leftPanel);
            splitGrid.Children.Add(splitter);
            splitGrid.Children.Add(rightPanel);
            Grid.SetRow(splitGrid, 2);

            disasmBtn.Click += (s, e) =>
            {
                try
                {
                    int offset = Convert.ToInt32(offsetInput.Text, 16);
                    byte[] fileBytes = File.ReadAllBytes(filePath);

                    // Use the real x86 disassembler
                    var disasm = new x86Disassembler(fileBytes, 0);
                    var lines = disasm.DisassembleRange(offset, Math.Min(fileBytes.Length - offset, 2048));

                    // Assembly view — clean monospace columns
                    var asmBuilder = new System.Text.StringBuilder();
                    foreach (var line in lines)
                    {
                        asmBuilder.AppendLine(line.Format());
                    }
                    asmText.Text = asmBuilder.ToString();

                    // Pseudo-code view
                    var pseudoBuilder = new System.Text.StringBuilder();
                    pseudoBuilder.AppendLine($"// Disassembly of {Path.GetFileName(filePath)} at offset 0x{offset:X}");
                    pseudoBuilder.AppendLine($"// {lines.Count} instructions decoded");
                    pseudoBuilder.AppendLine();
                    pseudoBuilder.AppendLine($"void sub_{offset:X8}() {{");

                    GeneratePseudoCode(lines, pseudoBuilder);

                    pseudoBuilder.AppendLine("}");
                    pseudoText.Text = pseudoBuilder.ToString();
                }
                catch (Exception ex)
                {
                    asmText.Text = $"// Error: {ex.Message}";
                    pseudoText.Text = $"// Error: {ex.Message}";
                }
            };

            mainGrid.Children.Add(titleBar);
            mainGrid.Children.Add(toolbar);
            mainGrid.Children.Add(splitGrid);
            disasmWindow.Content = mainGrid;

            // Auto-disassemble on open
            disasmBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            disasmWindow.ShowDialog();
        }

        private void GeneratePseudoCode(List<DisasmLine> instructions, System.Text.StringBuilder sb)
        {
            string ind = "    ";
            string[] regs32 = { "eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi" };

            foreach (var instr in instructions)
            {
                string m = instr.Mnemonic.ToLower().Trim();
                string ops = instr.Operands.Trim();

                // Skip noise
                if (m == "nop" || m == "push" || m == "pop" || m == "db" || string.IsNullOrEmpty(m))
                    continue;

                if (m == "mov")
                {
                    var parts = ops.Split(new[] { ", " }, 2, StringSplitOptions.None);
                    if (parts.Length == 2)
                        sb.AppendLine($"{ind}{parts[0].Trim()} = {parts[1].Trim()};");
                }
                else if (m == "add")
                {
                    var parts = ops.Split(new[] { ", " }, 2, StringSplitOptions.None);
                    if (parts.Length == 2)
                        sb.AppendLine($"{ind}{parts[0].Trim()} += {parts[1].Trim()};");
                }
                else if (m == "sub")
                {
                    var parts = ops.Split(new[] { ", " }, 2, StringSplitOptions.None);
                    if (parts.Length == 2)
                        sb.AppendLine($"{ind}{parts[0].Trim()} -= {parts[1].Trim()};");
                }
                else if (m == "xor")
                {
                    var parts = ops.Split(new[] { ", " }, 2, StringSplitOptions.None);
                    if (parts.Length == 2)
                    {
                        if (parts[0].Trim() == parts[1].Trim())
                            sb.AppendLine($"{ind}{parts[0].Trim()} = 0;");
                        else
                            sb.AppendLine($"{ind}{parts[0].Trim()} ^= {parts[1].Trim()};");
                    }
                }
                else if (m == "and")
                {
                    var parts = ops.Split(new[] { ", " }, 2, StringSplitOptions.None);
                    if (parts.Length == 2)
                        sb.AppendLine($"{ind}{parts[0].Trim()} &= {parts[1].Trim()};");
                }
                else if (m == "or")
                {
                    var parts = ops.Split(new[] { ", " }, 2, StringSplitOptions.None);
                    if (parts.Length == 2)
                        sb.AppendLine($"{ind}{parts[0].Trim()} |= {parts[1].Trim()};");
                }
                else if (m == "shl" || m == "sal")
                {
                    var parts = ops.Split(new[] { ", " }, 2, StringSplitOptions.None);
                    if (parts.Length == 2)
                        sb.AppendLine($"{ind}{parts[0].Trim()} <<= {parts[1].Trim()};");
                }
                else if (m == "shr" || m == "sar")
                {
                    var parts = ops.Split(new[] { ", " }, 2, StringSplitOptions.None);
                    if (parts.Length == 2)
                        sb.AppendLine($"{ind}{parts[0].Trim()} >>= {parts[1].Trim()};");
                }
                else if (m == "not")
                {
                    sb.AppendLine($"{ind}{ops} = ~{ops};");
                }
                else if (m == "neg")
                {
                    sb.AppendLine($"{ind}{ops} = -{ops};");
                }
                else if (m == "inc")
                {
                    sb.AppendLine($"{ind}{ops}++;");
                }
                else if (m == "dec")
                {
                    sb.AppendLine($"{ind}{ops}--;");
                }
                else if (m == "lea")
                {
                    var parts = ops.Split(new[] { ", " }, 2, StringSplitOptions.None);
                    if (parts.Length == 2)
                        sb.AppendLine($"{ind}{parts[0].Trim()} = &{parts[1].Trim()};");
                }
                else if (m == "cmp" || m == "test")
                {
                    var parts = ops.Split(new[] { ", " }, 2, StringSplitOptions.None);
                    if (parts.Length == 2)
                        sb.AppendLine($"{ind}// {m} {parts[0].Trim()}, {parts[1].Trim()}");
                }
                else if (m == "call")
                {
                    sb.AppendLine($"{ind}{ops}();");
                }
                else if (m == "ret")
                {
                    sb.AppendLine($"{ind}return;");
                }
                else if (m == "jmp")
                {
                    sb.AppendLine($"{ind}goto {ops};");
                }
                else if (m.StartsWith("j"))
                {
                    // Conditional jumps
                    string cond = m switch
                    {
                        "je" or "jz" => "==",
                        "jne" or "jnz" => "!=",
                        "jl" or "jnge" => "<",
                        "jle" or "jng" => "<=",
                        "jg" or "jnle" => ">",
                        "jge" or "jnl" => ">=",
                        "jb" or "jnae" => "< (unsigned)",
                        "jbe" or "jna" => "<= (unsigned)",
                        "ja" or "jnbe" => "> (unsigned)",
                        "jae" or "jnb" => ">= (unsigned)",
                        "js" => "sign",
                        "jns" => "!sign",
                        _ => m.Substring(1)
                    };
                    sb.AppendLine($"{ind}if ({cond}) goto {ops};");
                }
                else if (m == "int")
                {
                    sb.AppendLine($"{ind}interrupt({ops});");
                }
                else if (m == "cli")
                {
                    sb.AppendLine($"{ind}disable_interrupts();");
                }
                else if (m == "sti")
                {
                    sb.AppendLine($"{ind}enable_interrupts();");
                }
                else if (m == "hlt")
                {
                    sb.AppendLine($"{ind}halt();");
                }
                else if (m == "out")
                {
                    var parts = ops.Split(new[] { ", " }, 2, StringSplitOptions.None);
                    if (parts.Length == 2)
                        sb.AppendLine($"{ind}outb({parts[0].Trim()}, {parts[1].Trim()});");
                }
                else if (m == "in")
                {
                    var parts = ops.Split(new[] { ", " }, 2, StringSplitOptions.None);
                    if (parts.Length == 2)
                        sb.AppendLine($"{ind}{parts[0].Trim()} = inb({parts[1].Trim()});");
                }
                else if (m == "movzx")
                {
                    var parts = ops.Split(new[] { ", " }, 2, StringSplitOptions.None);
                    if (parts.Length == 2)
                        sb.AppendLine($"{ind}{parts[0].Trim()} = (unsigned){parts[1].Trim()};");
                }
                else if (m == "imul" || m == "mul")
                {
                    sb.AppendLine($"{ind}eax *= {ops};");
                }
                else if (m == "idiv" || m == "div")
                {
                    sb.AppendLine($"{ind}eax /= {ops}; edx = eax % {ops};");
                }
                else if (m == "cdq")
                {
                    sb.AppendLine($"{ind}edx = (eax < 0) ? -1 : 0; // sign-extend");
                }
                else
                {
                    // Anything we don't know — show as asm comment
                    if (!string.IsNullOrEmpty(ops))
                        sb.AppendLine($"{ind}asm(\"{m} {ops}\");");
                    else
                        sb.AppendLine($"{ind}asm(\"{m}\");");
                }
            }
        }

        private void RefreshFileTree()
        {
            // Save current expansion state
            SaveExpansionState(FileTree.Items);

            FileTree.Items.Clear();

            if (string.IsNullOrEmpty(projectPath) || !Directory.Exists(projectPath))
                return;

            TreeViewItem root = new TreeViewItem
            {
                Header = CreateHeader("FolderOpen", Path.GetFileName(projectPath), "#D7BA7D", true),
                Tag = projectPath,
                IsExpanded = true
            };

            root.Expanded += Folder_Expanded;
            root.Collapsed += Folder_Collapsed;

            AddDirectoryToTree(root, projectPath);

            // Always keep root expanded
            expandedFolders.Add(projectPath);
            root.IsExpanded = true;
            FileTree.Items.Add(root);

            // Restore expansion state for all folders
            RestoreExpansionState(FileTree.Items);
        }

        private void SaveExpansionState(ItemCollection items)
        {
            foreach (var item in items)
            {
                if (item is TreeViewItem treeItem && treeItem.Tag is string path)
                {
                    if (Directory.Exists(path))
                    {
                        if (treeItem.IsExpanded)
                        {
                            expandedFolders.Add(path);
                        }
                        else
                        {
                            expandedFolders.Remove(path);
                        }

                        if (treeItem.Items.Count > 0)
                        {
                            SaveExpansionState(treeItem.Items);
                        }
                    }
                }
            }
        }

        private void RestoreExpansionState(ItemCollection items)
        {
            foreach (var item in items)
            {
                if (item is TreeViewItem treeItem && treeItem.Tag is string path)
                {
                    if (Directory.Exists(path) && expandedFolders.Contains(path))
                    {
                        treeItem.IsExpanded = true;
                    }

                    if (treeItem.Items.Count > 0)
                    {
                        RestoreExpansionState(treeItem.Items);
                    }
                }
            }
        }

        private void Folder_Expanded(object sender, RoutedEventArgs e)
        {
            if (sender is TreeViewItem item && item.Tag is string path)
            {
                if (Directory.Exists(path))
                {
                    expandedFolders.Add(path);
                }
            }
        }

        private void Folder_Collapsed(object sender, RoutedEventArgs e)
        {
            if (sender is TreeViewItem item && item.Tag is string path)
            {
                if (Directory.Exists(path))
                {
                    expandedFolders.Remove(path);
                }
            }
        }

        private void AddDirectoryToTree(TreeViewItem parentItem, string dirPath)
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(dirPath).OrderBy(d => d))
                {
                    TreeViewItem folderItem = new TreeViewItem
                    {
                        Header = CreateHeader("Folder", Path.GetFileName(dir), "#D7BA7D", false),
                        Tag = dir,
                        IsExpanded = expandedFolders.Contains(dir)
                    };

                    folderItem.MouseRightButtonDown += TreeViewItem_RightClick;
                    folderItem.Expanded += Folder_Expanded;
                    folderItem.Collapsed += Folder_Collapsed;

                    AddDirectoryToTree(folderItem, dir);
                    parentItem.Items.Add(folderItem);
                }

                foreach (var file in Directory.GetFiles(dirPath).OrderBy(f => f))
                {
                    string ext = Path.GetExtension(file).ToLower();
                    string icon = "FileCode";
                    string color = "#858585";

                    if (ext == ".c")
                    {
                        icon = "LanguageC";
                        color = "#519ABA";
                    }
                    else if (ext == ".h")
                    {
                        icon = "AlphaHCircle";
                        color = "#A277FF";
                    }
                    else if (ext == ".asm")
                    {
                        icon = "TEXT:x86";
                        color = "#FF9800";
                    }
                    else if (ext == ".bin" || ext == ".img" || ext == ".iso")
                    {
                        icon = "TEXT:01";
                        color = "#4EC9B0";
                    }

                    TreeViewItem fileItem = new TreeViewItem
                    {
                        Header = CreateHeader(icon, Path.GetFileName(file), color, false),
                        Tag = file
                    };
                    fileItem.MouseRightButtonDown += TreeViewItem_RightClick;
                    parentItem.Items.Add(fileItem);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error adding directory to tree: {ex.Message}");
            }
        }

        private void TreeViewItem_RightClick(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (sender is TreeViewItem item)
                {
                    string path = item.Tag as string;
                    if (string.IsNullOrEmpty(path))
                        return;

                    if (path == projectPath)
                        return;

                    item.IsSelected = true;

                    var menu = new ContextMenu
                    {
                        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#252526")),
                        BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3F3F46")),
                        BorderThickness = new Thickness(1),
                        PlacementTarget = item,
                        Placement = System.Windows.Controls.Primitives.PlacementMode.Right,
                        HorizontalOffset = 0,
                        VerticalOffset = -5,
                        HasDropShadow = false,
                        Padding = new Thickness(0)
                    };

                    // Force the entire menu background to be dark
                    var contextMenuStyle = new Style(typeof(ContextMenu));
                    var contextMenuTemplate = new ControlTemplate(typeof(ContextMenu));
                    var contextMenuBorder = new FrameworkElementFactory(typeof(Border));
                    contextMenuBorder.SetValue(Border.BackgroundProperty, new SolidColorBrush((Color)ColorConverter.ConvertFromString("#252526")));
                    contextMenuBorder.SetValue(Border.BorderBrushProperty, new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3F3F46")));
                    contextMenuBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1, 1, 1, 1));
                    contextMenuBorder.SetValue(Border.PaddingProperty, new Thickness(0, 0, 0, 0));

                    var itemsPresenter = new FrameworkElementFactory(typeof(ItemsPresenter));
                    itemsPresenter.SetValue(ItemsPresenter.MarginProperty, new Thickness(0));

                    contextMenuBorder.AppendChild(itemsPresenter);
                    contextMenuTemplate.VisualTree = contextMenuBorder;
                    contextMenuStyle.Setters.Add(new Setter(ContextMenu.TemplateProperty, contextMenuTemplate));
                    menu.Style = contextMenuStyle;

                    var menuItemStyle = new Style(typeof(MenuItem));
                    menuItemStyle.Setters.Add(new Setter(MenuItem.BackgroundProperty, new SolidColorBrush((Color)ColorConverter.ConvertFromString("#252526"))));
                    menuItemStyle.Setters.Add(new Setter(MenuItem.ForegroundProperty, new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCCCCC"))));
                    menuItemStyle.Setters.Add(new Setter(MenuItem.BorderThicknessProperty, new Thickness(0)));
                    menuItemStyle.Setters.Add(new Setter(MenuItem.FontSizeProperty, 13.0));
                    menuItemStyle.Setters.Add(new Setter(MenuItem.HeightProperty, 32.0));

                    // Custom template that removes the icon glyph column entirely
                    var menuTemplate = new ControlTemplate(typeof(MenuItem));
                    var border = new FrameworkElementFactory(typeof(Border));
                    border.Name = "Bd";
                    border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(MenuItem.BackgroundProperty));
                    border.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 0));
                    border.SetValue(Border.PaddingProperty, new Thickness(12, 6, 12, 6));
                    border.SetValue(Border.SnapsToDevicePixelsProperty, true);

                    var stack = new FrameworkElementFactory(typeof(StackPanel));
                    stack.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

                    var icon = new FrameworkElementFactory(typeof(ContentPresenter));
                    icon.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(MenuItem.IconProperty));
                    icon.SetValue(ContentPresenter.MarginProperty, new Thickness(0, 0, 8, 0));
                    icon.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);

                    var header = new FrameworkElementFactory(typeof(ContentPresenter));
                    header.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(MenuItem.HeaderProperty));
                    header.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);

                    stack.AppendChild(icon);
                    stack.AppendChild(header);
                    border.AppendChild(stack);
                    menuTemplate.VisualTree = border;

                    var trigger = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
                    trigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3E3E42")), "Bd"));
                    menuTemplate.Triggers.Add(trigger);

                    menuItemStyle.Setters.Add(new Setter(MenuItem.TemplateProperty, menuTemplate));

                    // Separator style to ensure it's dark
                    var separatorStyle = new Style(typeof(Separator));
                    separatorStyle.Setters.Add(new Setter(Separator.BackgroundProperty, new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3F3F46"))));
                    separatorStyle.Setters.Add(new Setter(Separator.HeightProperty, 1.0));
                    separatorStyle.Setters.Add(new Setter(Separator.MarginProperty, new Thickness(0, 2, 0, 2)));

                    bool isDirectory = Directory.Exists(path);

                    if (isDirectory)
                    {
                        var newFileItem = new MenuItem
                        {
                            Header = "New File",
                            Style = menuItemStyle,
                            Icon = new PackIcon { Kind = PackIconKind.FilePlus, Width = 14, Height = 14, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4EC9B0")) }
                        };
                        newFileItem.Click += (s, args) =>
                        {
                            menu.IsOpen = false;
                            var dialog = CreateDarkInputDialog("New File", "Enter file name:");
                            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Tag as string))
                            {
                                string fileName = dialog.Tag as string;
                                string newFile = Path.Combine(path, fileName);
                                if (!File.Exists(newFile))
                                {
                                    File.WriteAllText(newFile, "", new System.Text.UTF8Encoding(false));
                                    RefreshFileTree();
                                    LoadFile(newFile);
                                    StatusText.Text = $"Created: {fileName}";
                                }
                                else
                                {
                                    ShowDarkMessageBox("File already exists!", "Error");
                                }
                            }
                        };
                        menu.Items.Add(newFileItem);

                        var newFolderItem = new MenuItem
                        {
                            Header = "New Folder",
                            Style = menuItemStyle,
                            Icon = new PackIcon { Kind = PackIconKind.FolderPlus, Width = 14, Height = 14, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D7BA7D")) }
                        };
                        newFolderItem.Click += (s, args) =>
                        {
                            menu.IsOpen = false;
                            var dialog = CreateDarkInputDialog("New Folder", "Enter folder name:");
                            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Tag as string))
                            {
                                string folderName = dialog.Tag as string;
                                string newFolder = Path.Combine(path, folderName);
                                if (!Directory.Exists(newFolder))
                                {
                                    Directory.CreateDirectory(newFolder);
                                    RefreshFileTree();
                                    StatusText.Text = $"Created folder: {folderName}";
                                }
                                else
                                {
                                    ShowDarkMessageBox("Folder already exists!", "Error");
                                }
                            }
                        };
                        menu.Items.Add(newFolderItem);

                        var separator1 = new Separator();
                        separator1.Style = separatorStyle;
                        menu.Items.Add(separator1);
                    }

                    // Add binary file options if it's a binary file
                    if (!isDirectory)
                    {
                        string ext = Path.GetExtension(path).ToLower();
                        string[] binaryExtensions = { ".bin", ".exe", ".dll", ".obj", ".o", ".img", ".iso" };

                        if (binaryExtensions.Contains(ext))
                        {
                            var hexEditorItem = new MenuItem
                            {
                                Header = "Open in Hex Editor",
                                Style = menuItemStyle,
                                Icon = new PackIcon { Kind = PackIconKind.Hexadecimal, Width = 14, Height = 14, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4EC9B0")) }
                            };
                            hexEditorItem.Click += (s, args) =>
                            {
                                menu.IsOpen = false;
                                OpenHexEditorWithFile(path);
                            };
                            menu.Items.Add(hexEditorItem);

                            var disasmItem = new MenuItem
                            {
                                Header = "Open in Disassembler",
                                Style = menuItemStyle,
                                Icon = new PackIcon { Kind = PackIconKind.Memory, Width = 14, Height = 14, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF9800")) }
                            };
                            disasmItem.Click += (s, args) =>
                            {
                                menu.IsOpen = false;
                                OpenDisassemblerWithFile(path);
                            };
                            menu.Items.Add(disasmItem);

                            var separatorBinary = new Separator();
                            separatorBinary.Style = separatorStyle;
                            menu.Items.Add(separatorBinary);
                        }
                    }

                    var renameItem = new MenuItem
                    {
                        Header = "Rename",
                        Style = menuItemStyle,
                        Icon = new PackIcon { Kind = PackIconKind.RenameBox, Width = 14, Height = 14, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#569CD6")) }
                    };
                    renameItem.Click += (s, args) =>
                    {
                        menu.IsOpen = false;
                        var dialog = CreateDarkInputDialog("Rename", "Enter new name:");
                        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Tag as string))
                        {
                            string newName = dialog.Tag as string;
                            string newPath = Path.Combine(Path.GetDirectoryName(path), newName);
                            try
                            {
                                if (File.Exists(path))
                                    File.Move(path, newPath);
                                else if (Directory.Exists(path))
                                    Directory.Move(path, newPath);
                                RefreshFileTree();
                                StatusText.Text = "Renamed successfully";
                            }
                            catch (Exception ex)
                            {
                                ShowDarkMessageBox($"Failed to rename: {ex.Message}", "Error");
                            }
                        }
                    };
                    menu.Items.Add(renameItem);

                    var separator2 = new Separator();
                    separator2.Style = separatorStyle;
                    menu.Items.Add(separator2);

                    var deleteItem = new MenuItem
                    {
                        Header = "Delete",
                        Style = menuItemStyle,
                        Icon = new PackIcon { Kind = PackIconKind.Delete, Width = 14, Height = 14, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F48771")) }
                    };
                    deleteItem.Click += (s, args) =>
                    {
                        menu.IsOpen = false;
                        try
                        {
                            if (File.Exists(path))
                            {
                                var confirm = CreateDarkConfirmDialog("Delete File", $"Are you sure you want to delete {Path.GetFileName(path)}?");
                                if (confirm.ShowDialog() == true)
                                {
                                    File.Delete(path);
                                    CloseTab(path);
                                    RefreshFileTree();
                                    StatusText.Text = "File deleted";
                                }
                            }
                            else if (Directory.Exists(path))
                            {
                                var confirm = CreateDarkConfirmDialog("Delete Folder", $"Are you sure you want to delete {Path.GetFileName(path)} and all its contents?");
                                if (confirm.ShowDialog() == true)
                                {
                                    Directory.Delete(path, true);
                                    RefreshFileTree();
                                    StatusText.Text = "Folder deleted";
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Dispatcher.Invoke(() => ShowDarkMessageBox($"Failed to delete: {ex.Message}", "Error"));
                        }
                    };
                    menu.Items.Add(deleteItem);

                    e.Handled = true;

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        menu.IsOpen = true;
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Right-click error: {ex.Message}");
            }
        }

        private StackPanel CreateHeader(string iconKind, string text, string color, bool bold)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            var fgBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));

            // Support text-based icons with "TEXT:xx" prefix
            if (iconKind.StartsWith("TEXT:"))
            {
                string iconText = iconKind.Substring(5);
                var textIcon = new TextBlock
                {
                    Text = iconText,
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    Foreground = fgBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 5, 0),
                    FontFamily = new FontFamily("Consolas")
                };
                panel.Children.Add(textIcon);
            }
            else
            {
                var icon = new PackIcon
                {
                    Width = 16,
                    Height = 16,
                    Margin = new Thickness(0, 0, 6, 0),
                    Foreground = fgBrush
                };
                var kindType = typeof(PackIconKind);
                icon.Kind = (PackIconKind)Enum.Parse(kindType, iconKind);
                panel.Children.Add(icon);
            }

            var textBlock = new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (bold)
                textBlock.FontWeight = FontWeights.SemiBold;

            panel.Children.Add(textBlock);

            return panel;
        }

        private void FileTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (Mouse.RightButton == MouseButtonState.Pressed)
                return;

            if (e.NewValue is TreeViewItem item && item.Tag is string path)
            {
                if (File.Exists(path))
                {
                    LoadFile(path);
                }
            }
        }

        private void LoadFile(string filePath)
        {
            if (!File.Exists(filePath))
                return;

            string ext = Path.GetExtension(filePath).ToLower();

            string[] binaryExtensions = { ".bin", ".exe", ".dll", ".obj", ".o", ".img", ".iso" };
            if (binaryExtensions.Contains(ext))
            {
                currentFile = "";

                // Show dialog to open in hex editor
                var result = ShowBinaryFileDialog(filePath);
                if (result == true)
                {
                    OpenHexEditorWithFile(filePath);
                }
                else
                {
                    CodeEditor.Text = $@"
╔════════════════════════════════════════════════════════════╗
║                                                            ║
║                    BINARY FILE DETECTED                    ║
║                                                            ║
╚════════════════════════════════════════════════════════════╝

File: {Path.GetFileName(filePath)}
Type: {ext.ToUpper().TrimStart('.')} Binary File
Size: {new FileInfo(filePath).Length:N0} bytes
Path: {filePath}

────────────────────────────────────────────────────────────

This is a binary file and cannot be displayed as text.
Binary files contain compiled machine code or data.

To view this file:
  • Click the file again to open in Hex Editor
  • Use Extensions → Hex Editor from the menu

────────────────────────────────────────────────────────────
";
                    FileTypeText.Text = "Binary";
                    StatusText.Text = $"Binary file: {Path.GetFileName(filePath)}";
                }
                return;
            }

            if (!string.IsNullOrEmpty(currentFile) && currentFile != filePath)
                CodeEditor.Save(currentFile);

            currentFile = filePath;

            try
            {
                CodeEditor.Load(filePath);
                FileTypeText.Text = ext == ".c" ? "C" : ext == ".asm" ? "Assembly" : "Text";
                StatusText.Text = $"Opened: {Path.GetFileName(filePath)}";
                AddOrActivateTab(filePath);

                // Parse for IntelliSense if it's a C file
                if (ext == ".c" || ext == ".h")
                    _completion?.ParseUserCode(CodeEditor.Text);
            }
            catch (Exception ex)
            {
                CodeEditor.Text = $@"
╔════════════════════════════════════════════════════════════╗
║                         ERROR                              ║
╔════════════════════════════════════════════════════════════╗

Failed to load file: {Path.GetFileName(filePath)}

Error: {ex.Message}

This file may be corrupted, locked by another process,
or in an unsupported format.
";
                StatusText.Text = "Failed to load file";
            }
        }

        private void AddOrActivateTab(string filePath)
        {
            string ext = Path.GetExtension(filePath).ToLower();
            string[] binaryExtensions = { ".bin", ".exe", ".dll", ".obj", ".o", ".img", ".iso" };
            if (binaryExtensions.Contains(ext))
                return;

            var existing = openTabs.FirstOrDefault(t => t.FilePath == filePath);
            if (existing != null)
            {
                foreach (var tab in openTabs)
                    tab.IsActive = false;
                existing.IsActive = true;
            }
            else
            {
                foreach (var tab in openTabs)
                    tab.IsActive = false;

                openTabs.Add(new TabItem
                {
                    FilePath = filePath,
                    FileName = Path.GetFileName(filePath),
                    IsPinned = false,
                    IsActive = true
                });
            }

            RenderTabs();
        }

        private void RenderTabs()
        {
            TabPanel.Children.Clear();

            var sortedTabs = openTabs.OrderByDescending(t => t.IsPinned).ThenBy(t => openTabs.IndexOf(t)).ToList();

            foreach (var tab in sortedTabs)
            {
                var tabBorder = new Border
                {
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(tab.IsActive ? "#1E1E1E" : "#2D2D30")),
                    BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3F3F46")),
                    BorderThickness = new Thickness(0, 0, 1, 0),
                    Padding = new Thickness(12, 6, 8, 6),
                    Cursor = Cursors.Hand,
                    AllowDrop = true,
                    Height = 35
                };

                var panel = new StackPanel { Orientation = Orientation.Horizontal };

                if (tab.IsPinned)
                {
                    var pinIcon = new PackIcon
                    {
                        Kind = PackIconKind.Pin,
                        Width = 12,
                        Height = 12,
                        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4EC9B0")),
                        Margin = new Thickness(0, 0, 6, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    panel.Children.Add(pinIcon);
                }

                var fileName = new TextBlock
                {
                    Text = tab.FileName,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCCCCC")),
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                panel.Children.Add(fileName);

                var closeBtn = new Button
                {
                    Content = "✕",
                    Background = Brushes.Transparent,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#858585")),
                    BorderThickness = new Thickness(0, 0, 0, 0),
                    FontSize = 12,
                    Width = 16,
                    Height = 16,
                    Padding = new Thickness(0, 0, 0, 0),
                    Cursor = Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center
                };

                closeBtn.Click += (s, e) =>
                {
                    e.Handled = true;
                    CloseTab(tab.FilePath);
                };

                panel.Children.Add(closeBtn);
                tabBorder.Child = panel;

                tabBorder.MouseLeftButtonDown += (s, e) =>
                {
                    if (e.ClickCount == 1)
                    {
                        LoadFile(tab.FilePath);
                    }
                };

                tabBorder.MouseMove += (s, e) =>
                {
                    if (e.LeftButton == MouseButtonState.Pressed && draggedTab == null)
                    {
                        draggedTab = tab;
                        draggedTabBorder = tabBorder;
                        DragDrop.DoDragDrop(tabBorder, tab, DragDropEffects.Move);
                        draggedTab = null;
                        draggedTabBorder = null;
                    }
                };

                tabBorder.DragOver += (s, e) =>
                {
                    if (draggedTab != null && draggedTab != tab)
                    {
                        e.Effects = DragDropEffects.Move;
                        e.Handled = true;
                    }
                };

                tabBorder.Drop += (s, e) =>
                {
                    if (draggedTab != null && draggedTab != tab)
                    {
                        int oldIndex = openTabs.IndexOf(draggedTab);
                        int newIndex = openTabs.IndexOf(tab);

                        if (oldIndex != -1 && newIndex != -1)
                        {
                            openTabs.Move(oldIndex, newIndex);
                            RenderTabs();
                        }
                        e.Handled = true;
                    }
                };

                tabBorder.MouseRightButtonDown += (s, e) =>
                {
                    var menu = new ContextMenu
                    {
                        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#252526")),
                        BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3F3F46"))
                    };

                    var pinItem = new MenuItem
                    {
                        Header = tab.IsPinned ? "Unpin" : "Pin",
                        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCCCCC"))
                    };
                    pinItem.Click += (sender, args) =>
                    {
                        tab.IsPinned = !tab.IsPinned;
                        RenderTabs();
                    };

                    var closeItem = new MenuItem
                    {
                        Header = "Close",
                        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCCCCC"))
                    };
                    closeItem.Click += (sender, args) => CloseTab(tab.FilePath);

                    var closeOthersItem = new MenuItem
                    {
                        Header = "Close Others",
                        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCCCCC"))
                    };
                    closeOthersItem.Click += (sender, args) =>
                    {
                        var toRemove = openTabs.Where(t => t.FilePath != tab.FilePath && !t.IsPinned).ToList();
                        foreach (var t in toRemove)
                            openTabs.Remove(t);
                        RenderTabs();
                    };

                    var closeAllItem = new MenuItem
                    {
                        Header = "Close All",
                        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCCCCC"))
                    };
                    closeAllItem.Click += (sender, args) =>
                    {
                        var toRemove = openTabs.Where(t => !t.IsPinned).ToList();
                        foreach (var t in toRemove)
                            openTabs.Remove(t);
                        if (openTabs.Count == 0)
                        {
                            currentFile = "";
                            CodeEditor.Text = "// Welcome to OS Dev IDE\n// Create or open a project to get started\n";
                            StatusText.Text = "Ready";
                        }
                        RenderTabs();
                    };

                    menu.Items.Add(pinItem);
                    menu.Items.Add(new Separator());
                    menu.Items.Add(closeItem);
                    menu.Items.Add(closeOthersItem);
                    menu.Items.Add(closeAllItem);

                    menu.IsOpen = true;
                };

                TabPanel.Children.Add(tabBorder);
            }
        }

        private void CloseTab(string filePath)
        {
            var tab = openTabs.FirstOrDefault(t => t.FilePath == filePath);
            if (tab != null)
            {
                openTabs.Remove(tab);

                if (currentFile == filePath)
                {
                    currentFile = "";
                    if (openTabs.Count > 0)
                    {
                        LoadFile(openTabs.Last().FilePath);
                    }
                    else
                    {
                        CodeEditor.Text = "// Welcome to OS Dev IDE\n// Create or open a project to get started\n";
                        StatusText.Text = "Ready";
                    }
                }

                RenderTabs();
            }
        }

        private void OutputConsole_TextChanged(object sender, TextChangedEventArgs e)
        {
            OutputScroller.ScrollToEnd();
        }

        private async void CompileOnly_Click(object sender, RoutedEventArgs e) => await DoBuild(false);
        private async void BuildRun_Click(object sender, RoutedEventArgs e) => await DoBuild(true);

        private async System.Threading.Tasks.Task DoBuild(bool runQemu)
        {
            if (string.IsNullOrEmpty(projectPath))
            {
                SetOutputText("⚠ Create a project first!");
                StatusText.Text = "No project loaded";
                return;
            }

            if (!string.IsNullOrEmpty(currentFile))
                CodeEditor.Save(currentFile);

            ClearOutput();
            await DoBuildOS(runQemu);
        }

        private async System.Threading.Tasks.Task DoBuildOS(bool runQemu)
        {
            var buildStart = DateTime.Now;

            AppendOutput("━━━ SubsetC Build ━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n");
            AppendOutput($"  {buildStart:HH:mm:ss}  {Path.GetFileName(projectPath)}\n\n");

            string kernelDir = Path.Combine(projectPath, "Kernel");
            string bootDir = Path.Combine(projectPath, "Bootloader");

            string compilerExe = "Compiler-x86_32.exe";
            string compilerPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, compilerExe);

            if (!File.Exists(compilerPath))
            {
                AppendOutput($"[!] ERROR: Compiler not found\n");
                AppendOutput($"    Expected: {compilerPath}\n");
                StatusText.Text = "Build failed — compiler missing";
                return;
            }

            // ── Step 1: Compile C → Assembly ──
            string kernelAsm = Path.Combine(kernelDir, "kernel.asm");

            // Delete stale .asm so we don't show misleading stats on failure
            if (File.Exists(kernelAsm))
                File.Delete(kernelAsm);

            AppendOutput("[CC]  kernel.c → kernel.asm\n");

            // Strip BOM from all .c files before compilation
            string[] cFiles = Directory.GetFiles(kernelDir, "*.c");
            foreach (string cFile in cFiles)
            {
                try
                {
                    string content = File.ReadAllText(cFile);
                    if (content.Length > 0 && (content[0] == '\uFEFF' || content[0] == '\ufeff'))
                        content = content.Substring(1);
                    File.WriteAllText(cFile, content, new System.Text.UTF8Encoding(false));
                }
                catch { }
            }

            Process compilerProc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = compilerPath,
                    Arguments = $"\"{Path.Combine(kernelDir, "kernel.c")}\" -o \"{kernelAsm}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            compilerProc.Start();
            string compOut = await compilerProc.StandardOutput.ReadToEndAsync();
            string compErr = await compilerProc.StandardError.ReadToEndAsync();
            await compilerProc.WaitForExitAsync();

            // Filter compiler output — only show REAL errors/warnings, not function names containing 'error'
            bool IsRealError(string line)
            {
                string lower = line.ToLower().Trim();
                // Real errors: "error:", "error at", "Error token:", "error on line", "COMPILATION FAILED"
                // NOT: "snd_error", "error_handler", "FUNCTION void snd_error"
                if (lower.Contains("error:") || lower.Contains("error at") || lower.Contains("error token") ||
                    lower.StartsWith("error") || lower.Contains("failed") ||
                    System.Text.RegularExpressions.Regex.IsMatch(lower, @"(?<![_a-z])error(?![_a-z])"))
                    return true;
                return false;
            }

            bool IsRealWarning(string line)
            {
                string lower = line.ToLower().Trim();
                if (lower.Contains("warning:") || lower.StartsWith("warning") ||
                    System.Text.RegularExpressions.Regex.IsMatch(lower, @"(?<![_a-z])warning(?![_a-z])"))
                    return true;
                return false;
            }

            if (!string.IsNullOrWhiteSpace(compErr))
            {
                foreach (var line in compErr.Split('\n'))
                {
                    string trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed)) continue;
                    if (IsRealError(trimmed))
                        AppendOutput($"      ✗ {trimmed}\n");
                    else if (IsRealWarning(trimmed))
                        AppendOutput($"      ⚠ {trimmed}\n");
                    // Skip everything else (FUNCTION, labels, verbose dumps)
                }
            }

            // Show only real errors/warnings from stdout
            if (!string.IsNullOrWhiteSpace(compOut))
            {
                foreach (var line in compOut.Split('\n'))
                {
                    string trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed)) continue;
                    if (IsRealError(trimmed))
                        AppendOutput($"      ✗ {trimmed}\n");
                    else if (IsRealWarning(trimmed))
                        AppendOutput($"      ⚠ {trimmed}\n");
                }
            }

            if (compilerProc.ExitCode != 0 || !File.Exists(kernelAsm))
            {
                AppendOutput("\n[!] COMPILATION FAILED\n");
                StatusText.Text = "Build failed — compiler errors";
                return;
            }

            // Show generated stats on success
            if (File.Exists(kernelAsm))
            {
                long asmSize = new FileInfo(kernelAsm).Length;
                int asmLines = File.ReadAllLines(kernelAsm).Length;
                AppendOutput($"      Generated: {asmLines:N0} lines, {asmSize:N0} bytes\n");
            }
            AppendOutput("[+]  kernel.asm generated ✓\n\n");

            // ── Step 2: Assemble with NASM ──
            AppendOutput("[ASM] Assembling with NASM...\n");
            StatusText.Text = "Assembling...";

            string driveLetter = projectPath[0].ToString().ToLower();
            string wslPath = "/mnt/" + driveLetter + projectPath.Substring(2).Replace("\\", "/");

            string wslScript = $"cd '{wslPath}' && ";

            bool hasBootloader = File.Exists(Path.Combine(bootDir, "bootloader.asm"));
            if (hasBootloader)
            {
                AppendOutput("      bootloader.asm → bootloader.bin\n");
                wslScript += $"cd Bootloader && nasm -f bin bootloader.asm -o bootloader.bin && cd .. && ";
            }
            else
            {
                AppendOutput("      ⚠ No bootloader.asm found, skipping\n");
            }

            AppendOutput("      kernel.asm → kernel.bin\n");
            wslScript += $"cd Kernel && nasm -f bin kernel.asm -o kernel.bin && cd .. && ";

            // ── Step 3: Create disk image ──
            AppendOutput("[IMG] Creating os-image.bin...\n");
            wslScript += "cp Bootloader/bootloader.bin os-image.bin 2>/dev/null || true && ";
            wslScript += "dd if=Kernel/kernel.bin of=os-image.bin bs=512 seek=1 conv=notrunc 2>/dev/null || true && ";
            wslScript += "dd if=/dev/zero bs=1 count=0 seek=1474560 of=os-image.bin 2>/dev/null";

            if (runQemu)
            {
                AppendOutput("[RUN] Launching QEMU...\n");
                wslScript += " && qemu-system-i386 -drive file=os-image.bin,format=raw -serial stdio -no-reboot";
            }

            Process wslProc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "wsl.exe",
                    Arguments = $"-e bash -c \"{wslScript.Replace("\"", "\\\"")}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            wslProc.OutputDataReceived += (s, a) =>
            {
                // Only show NASM errors, QEMU output, or genuinely useful info
                if (a.Data != null && !string.IsNullOrWhiteSpace(a.Data))
                    Dispatcher.Invoke(() => AppendOutput("      " + a.Data + "\n"));
            };
            wslProc.ErrorDataReceived += (s, a) =>
            {
                // Only show real errors from NASM/WSL, filter noise
                if (a.Data != null && !string.IsNullOrWhiteSpace(a.Data))
                {
                    string line = a.Data.Trim();
                    // Show NASM errors (file:line: error:), skip dd/cp noise
                    if (line.Contains("error") || line.Contains("warning") || line.Contains("fatal"))
                        Dispatcher.Invoke(() => AppendOutput("      ✗ " + line + "\n"));
                }
            };

            wslProc.Start();
            wslProc.BeginOutputReadLine();
            wslProc.BeginErrorReadLine();
            await wslProc.WaitForExitAsync();

            var elapsed = DateTime.Now - buildStart;

            if (wslProc.ExitCode == 0)
            {
                // Show file sizes
                string imgPath = Path.Combine(projectPath, "os-image.bin");
                string kernelBin = Path.Combine(kernelDir, "kernel.bin");
                string bootBin = Path.Combine(bootDir, "bootloader.bin");

                AppendOutput("\n━━━ ✓ BUILD SUCCESSFUL ━━━━━━━━━━━━━━━━━━━━━━━\n");

                if (File.Exists(kernelBin))
                    AppendOutput($"  kernel.bin:     {new FileInfo(kernelBin).Length:N0} bytes ({new FileInfo(kernelBin).Length / 512} sectors)\n");
                if (File.Exists(bootBin))
                    AppendOutput($"  bootloader.bin: {new FileInfo(bootBin).Length:N0} bytes\n");
                if (File.Exists(imgPath))
                    AppendOutput($"  os-image.bin:   {new FileInfo(imgPath).Length:N0} bytes\n");
                AppendOutput($"  Build time:     {elapsed.TotalSeconds:F1}s\n");

                StatusText.Text = runQemu ? $"Running in QEMU ({elapsed.TotalSeconds:F1}s)" : $"Build successful ({elapsed.TotalSeconds:F1}s)";
                RefreshFileTree(); // Auto-refresh to show new .asm, .bin, .img files
            }
            else
            {
                AppendOutput($"\n[!] BUILD FAILED ({elapsed.TotalSeconds:F1}s)\n");
                AppendOutput("    Check errors above. Common fixes:\n");
                AppendOutput("    • NASM error → check bootloader.asm labels\n");
                AppendOutput("    • WSL error  → ensure WSL + nasm installed\n");
                StatusText.Text = "Build failed";
            }
        }

        #region Terminal Feature

        private void OutputTab_Checked(object sender, RoutedEventArgs e)
        {
            if (OutputScroller != null) OutputScroller.Visibility = Visibility.Visible;
            if (TerminalPanel != null) TerminalPanel.Visibility = Visibility.Collapsed;
            if (OllamaPanel != null) OllamaPanel.Visibility = Visibility.Collapsed;
        }

        private void TerminalTab_Checked(object sender, RoutedEventArgs e)
        {
            if (OutputScroller != null) OutputScroller.Visibility = Visibility.Collapsed;
            if (TerminalPanel != null) TerminalPanel.Visibility = Visibility.Visible;
            if (OllamaPanel != null) OllamaPanel.Visibility = Visibility.Collapsed;
            if (TerminalOutput.Text == "") TerminalOutput.Text = "PowerShell Terminal Ready\nType a command and press Enter...\n\n";
            TerminalInput.Focus();
        }

        private async void TerminalInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return)
            {
                string command = TerminalInput.Text.Trim();
                if (string.IsNullOrEmpty(command)) return;

                TerminalOutput.AppendText($"PS> {command}\n");
                TerminalInput.Clear();

                if (command.ToLower() == "clear" || command.ToLower() == "cls")
                {
                    TerminalOutput.Clear();
                    return;
                }

                await ExecuteTerminalCommand(command);
            }
        }

        private async Task ExecuteTerminalCommand(string command)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -Command \"{command.Replace("\"", "\\\"")}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = string.IsNullOrEmpty(projectPath) ? Environment.CurrentDirectory : projectPath
                };

                using (var process = new Process { StartInfo = psi })
                {
                    process.OutputDataReceived += (s, e) => { if (e.Data != null) Dispatcher.Invoke(() => { TerminalOutput.AppendText(e.Data + "\n"); TerminalOutput.ScrollToEnd(); }); };
                    process.ErrorDataReceived += (s, e) => { if (e.Data != null) Dispatcher.Invoke(() => { TerminalOutput.AppendText(e.Data + "\n"); TerminalOutput.ScrollToEnd(); }); };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    await process.WaitForExitAsync();

                    if (process.ExitCode != 0) Dispatcher.Invoke(() => TerminalOutput.AppendText($"Exit code: {process.ExitCode}\n"));
                    Dispatcher.Invoke(() => TerminalOutput.AppendText("\n"));
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => TerminalOutput.AppendText($"Error: {ex.Message}\n\n"));
            }
        }

        private void ClearTerminal_Click(object sender, RoutedEventArgs e)
        {
            TerminalOutput.Clear();
            TerminalOutput.Text = "PowerShell Terminal\n\n";
        }

        #endregion

        #region Ollama AI (Opens Floating Window)

        private OllamaWindow _ollamaWindow = null;

        /// <summary>
        /// Opens the Ollama AI window from Extensions menu
        /// </summary>
        private void OpenOllama_Click(object sender, RoutedEventArgs e)
        {
            OpenOllamaWindow();
        }

        private void OllamaTab_Checked(object sender, RoutedEventArgs e)
        {
            if (OutputScroller != null) OutputScroller.Visibility = Visibility.Collapsed;
            if (TerminalPanel != null) TerminalPanel.Visibility = Visibility.Collapsed;
            if (OllamaPanel != null) OllamaPanel.Visibility = Visibility.Visible;
        }

        private void OllamaOpenWindow_Click(object sender, RoutedEventArgs e)
        {
            OpenOllamaWindow();
        }

        private void OllamaQuick_Game(object sender, RoutedEventArgs e)
        {
            OpenOllamaWindow();
            // The window will handle the rest
        }

        private void OllamaQuick_OS(object sender, RoutedEventArgs e)
        {
            OpenOllamaWindow();
        }

        private void OllamaQuick_Fix(object sender, RoutedEventArgs e)
        {
            OpenOllamaWindow();
        }

        private void OllamaQuick_Explain(object sender, RoutedEventArgs e)
        {
            OpenOllamaWindow();
        }

        private void OpenOllamaWindow()
        {
            if (_ollamaWindow != null && _ollamaWindow.IsLoaded)
            {
                _ollamaWindow.Activate();
                _ollamaWindow.Focus();
                return;
            }

            _ollamaWindow = new OllamaWindow(
                getEditorCode: () => CodeEditor?.Text,
                getEditorFile: () => currentFile,
                getBuildOutput: () => new TextRange(OutputConsole.Document.ContentStart, OutputConsole.Document.ContentEnd).Text,
                writeFile: (filename, code) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (!string.IsNullOrEmpty(projectPath))
                        {
                            string filepath = System.IO.Path.Combine(projectPath, filename);
                            try
                            {
                                // Write UTF-8 WITHOUT BOM to avoid tokenization errors
                                System.IO.File.WriteAllText(filepath, code, new System.Text.UTF8Encoding(false));
                                RefreshExplorer();
                                OpenFile(filepath);
                            }
                            catch (Exception ex)
                            {
                                MessageBox.Show($"Error writing file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            }
                        }
                        else
                        {
                            CodeEditor.Text = code;
                        }
                    });
                },
                setEditorCode: (code) => Dispatcher.Invoke(() => CodeEditor.Text = code)
            );

            _ollamaWindow.Closed += (s, args) => _ollamaWindow = null;
            _ollamaWindow.Show();
        }

        #endregion

        #region Wrapper Methods for Compatibility

        /// <summary>
        /// Wrapper for RefreshFileTree - used by extensions
        /// </summary>
        private void RefreshExplorer()
        {
            RefreshFileTree();
        }

        /// <summary>
        /// Wrapper for LoadFile - used by extensions
        /// </summary>
        private void OpenFile(string filepath)
        {
            LoadFile(filepath);
        }

        /// <summary>
        /// Shows the AI/Ollama panel
        /// </summary>
        private void AIShow_Click(object sender, RoutedEventArgs e)
        {
            OpenOllamaWindow();
        }

        #endregion
    }

    // Brace-based code folding strategy
    public class BraceFoldingStrategy
    {
        public void UpdateFoldings(ICSharpCode.AvalonEdit.Folding.FoldingManager manager, ICSharpCode.AvalonEdit.Document.TextDocument document)
        {
            int firstErrorOffset;
            var newFoldings = CreateNewFoldings(document, out firstErrorOffset);
            manager.UpdateFoldings(newFoldings, firstErrorOffset);
        }

        public IEnumerable<ICSharpCode.AvalonEdit.Folding.NewFolding> CreateNewFoldings(ICSharpCode.AvalonEdit.Document.TextDocument document, out int firstErrorOffset)
        {
            firstErrorOffset = -1;
            var newFoldings = new List<ICSharpCode.AvalonEdit.Folding.NewFolding>();

            try
            {
                Stack<int> startOffsets = new Stack<int>();
                Stack<string> startNames = new Stack<string>();
                int lastNewLineOffset = 0;

                for (int i = 0; i < document.TextLength; i++)
                {
                    char c = document.GetCharAt(i);

                    if (c == '\n')
                    {
                        lastNewLineOffset = i + 1;
                    }
                    else if (c == '{')
                    {
                        // Try to get the function/block name
                        string name = GetBlockName(document, i);
                        startOffsets.Push(i);
                        startNames.Push(name);
                    }
                    else if (c == '}' && startOffsets.Count > 0)
                    {
                        int startOffset = startOffsets.Pop();
                        string name = startNames.Pop();

                        // Only create folding if it spans multiple lines
                        if (document.GetLineByOffset(startOffset).LineNumber < document.GetLineByOffset(i).LineNumber)
                        {
                            newFoldings.Add(new ICSharpCode.AvalonEdit.Folding.NewFolding(startOffset, i + 1)
                            {
                                Name = name,
                                DefaultClosed = false
                            });
                        }
                    }
                }
            }
            catch { }

            newFoldings.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
            return newFoldings;
        }

        private string GetBlockName(ICSharpCode.AvalonEdit.Document.TextDocument document, int braceOffset)
        {
            try
            {
                int searchStart = Math.Max(0, braceOffset - 200);
                string text = document.GetText(searchStart, braceOffset - searchStart).TrimEnd();

                // Look for function pattern: type name(...)
                var funcMatch = System.Text.RegularExpressions.Regex.Match(text, @"(\w+)\s*\([^)]*\)\s*$");
                if (funcMatch.Success)
                {
                    string name = funcMatch.Groups[1].Value;
                    // Don't label control flow as functions
                    if (name != "if" && name != "else" && name != "while" && name != "for" && name != "switch" && name != "do")
                        return name + "()";
                }

                // Look for control structures with word boundaries (last keyword before brace)
                var ctrlMatch = System.Text.RegularExpressions.Regex.Match(text, @"\b(switch|while|for|if|else\s*if|else|do|struct|enum)\b[^{}]*$");
                if (ctrlMatch.Success)
                    return ctrlMatch.Groups[1].Value.Trim();

                return "{ }";
            }
            catch
            {
                return "{ }";
            }
        }
    }

    // Custom margin that draws ONLY chevrons (NO BOXES)
    public class ChevronFoldingMargin : ICSharpCode.AvalonEdit.Editing.AbstractMargin
    {
        private ICSharpCode.AvalonEdit.Folding.FoldingManager manager;
        private const double MARGIN_WIDTH = 16;

        public ChevronFoldingMargin(ICSharpCode.AvalonEdit.Folding.FoldingManager foldingManager)
        {
            this.manager = foldingManager;
            this.Width = MARGIN_WIDTH;
            this.Cursor = System.Windows.Input.Cursors.Hand;
        }

        protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize)
        {
            return new System.Windows.Size(MARGIN_WIDTH, 0);
        }

        protected override void OnRender(System.Windows.Media.DrawingContext dc)
        {
            if (manager == null) return;
            var textView = this.TextView;
            if (textView == null || !textView.VisualLinesValid) return;

            // Track visual Y positions we've drawn to prevent overlapping chevrons
            var drawnPositions = new System.Collections.Generic.HashSet<int>();

            // Build list of folded ranges to skip nested foldings inside collapsed parents
            var foldedRanges = new System.Collections.Generic.List<(int start, int end)>();
            foreach (var f in manager.AllFoldings)
            {
                if (f.IsFolded)
                    foldedRanges.Add((f.StartOffset, f.EndOffset));
            }

            foreach (var folding in manager.AllFoldings)
            {
                // Skip foldings that are inside another folded region
                bool insideFolded = false;
                foreach (var range in foldedRanges)
                {
                    if (folding.StartOffset > range.start && folding.StartOffset < range.end)
                    {
                        insideFolded = true;
                        break;
                    }
                }
                if (insideFolded) continue;

                var startLine = textView.Document.GetLineByOffset(folding.StartOffset);
                var visualLine = textView.GetVisualLine(startLine.LineNumber);
                if (visualLine == null) continue;

                double lineTop = visualLine.VisualTop - textView.ScrollOffset.Y;
                double lineHeight = visualLine.Height;

                // Skip if not visible
                if (lineTop + lineHeight < 0 || lineTop > this.ActualHeight) continue;

                // Round to nearest pixel to deduplicate overlapping visual positions
                int visualKey = (int)(lineTop * 10);
                if (drawnPositions.Contains(visualKey)) continue;
                drawnPositions.Add(visualKey);

                double centerY = lineTop + (lineHeight / 2);
                double centerX = MARGIN_WIDTH / 2;

                var pen = new System.Windows.Media.Pen(
                    new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(180, 180, 180)), 1.5);
                pen.Freeze();

                if (folding.IsFolded)
                {
                    // ▶ Right-pointing chevron (collapsed)
                    var geo = new System.Windows.Media.StreamGeometry();
                    using (var ctx = geo.Open())
                    {
                        ctx.BeginFigure(new System.Windows.Point(centerX - 3, centerY - 5), false, false);
                        ctx.LineTo(new System.Windows.Point(centerX + 3, centerY), true, true);
                        ctx.LineTo(new System.Windows.Point(centerX - 3, centerY + 5), true, true);
                    }
                    geo.Freeze();
                    dc.DrawGeometry(null, pen, geo);
                }
                else
                {
                    // ▼ Down-pointing chevron (expanded)
                    var geo = new System.Windows.Media.StreamGeometry();
                    using (var ctx = geo.Open())
                    {
                        ctx.BeginFigure(new System.Windows.Point(centerX - 5, centerY - 3), false, false);
                        ctx.LineTo(new System.Windows.Point(centerX, centerY + 3), true, true);
                        ctx.LineTo(new System.Windows.Point(centerX + 5, centerY - 3), true, true);
                    }
                    geo.Freeze();
                    dc.DrawGeometry(null, pen, geo);
                }
            }
        }

        protected override void OnMouseDown(System.Windows.Input.MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);

            var pos = e.GetPosition(this);
            var textView = this.TextView;
            if (textView == null || !textView.VisualLinesValid) return;

            // Find the OUTERMOST folding at this visual position (prevents toggling hidden inner folds)
            ICSharpCode.AvalonEdit.Folding.FoldingSection bestFolding = null;
            double bestLineTop = -1;

            foreach (var folding in manager.AllFoldings)
            {
                // Skip foldings nested inside folded parents
                bool insideFolded = false;
                foreach (var other in manager.AllFoldings)
                {
                    if (other != folding && other.IsFolded &&
                        folding.StartOffset > other.StartOffset && folding.StartOffset < other.EndOffset)
                    {
                        insideFolded = true;
                        break;
                    }
                }
                if (insideFolded) continue;

                var startLine = textView.Document.GetLineByOffset(folding.StartOffset);
                var visualLine = textView.GetVisualLine(startLine.LineNumber);
                if (visualLine == null) continue;

                double lineTop = visualLine.VisualTop - textView.ScrollOffset.Y;
                double lineHeight = visualLine.Height;

                if (pos.Y >= lineTop && pos.Y <= lineTop + lineHeight &&
                    pos.X >= 0 && pos.X <= MARGIN_WIDTH)
                {
                    // Pick the outermost (largest span) folding at this position
                    if (bestFolding == null || (folding.EndOffset - folding.StartOffset) > (bestFolding.EndOffset - bestFolding.StartOffset))
                    {
                        bestFolding = folding;
                        bestLineTop = lineTop;
                    }
                }
            }

            if (bestFolding != null)
            {
                bestFolding.IsFolded = !bestFolding.IsFolded;
                textView.Redraw();
                this.InvalidateVisual();
                e.Handled = true;
            }
        }
    }
}