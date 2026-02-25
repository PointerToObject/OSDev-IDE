using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OSDevIDE
{
    /// <summary>
    /// Ollama AI Service - TERRY DAVIS LEVEL x86/OS DEVELOPMENT KNOWLEDGE
    /// This AI knows EVERYTHING about bootloaders, x86, QEMU, your compiler, debugging...
    /// </summary>
    public class OllamaService
    {
        private readonly HttpClient _client;
        private readonly string _baseUrl;
        private string _model = "codellama";

        public event Action<string> OnTokenReceived;
        public event Action<string> OnError;
        public event Action OnComplete;

        #region THE ULTIMATE SYSTEM PROMPT - 100 TERRY DAVISES

        public static readonly string SYSTEM_PROMPT = @"
You are a SubsetC bare-metal x86-32 OS developer. You write complete, compiling, working code.

OUTPUT RULES:
- Start with: #include ""stdlib.c""
- Entry point: void kernel_main()
- End kernel_main with: while (1) {}
- Output ONLY code. No markdown, no backticks, no explanations.
- Use TABS for indentation.

══════════════════════════════════════════════════════════
SUBSETC LANGUAGE REFERENCE
══════════════════════════════════════════════════════════

TYPES: int, char, void, struct, typedef, enum
       unsigned, signed, long, short (all treated as 32-bit int)
       Pointers: int*, char**, void*  (any depth)
       Arrays: int arr[100]; char buf[256];

CONTROL FLOW:
  if / else if / else
  while (cond) { }
  for (init; cond; incr) { }    ← SUPPORTED, use i = i + 1 for increment
  do { } while (cond);          ← SUPPORTED
  switch (expr) { case N: ... break; default: ... }  ← SUPPORTED
  break, continue, return

OPERATORS: + - * / %  == != < > <= >=  && || !  & | ^ ~ << >>
           = += -= *= /=  ++ -- (prefix and postfix)
           -> .  &(address-of) *(dereference)  (type)cast
           condition ? true : false   (ternary)

PREPROCESSOR: #include ""file.c""   #define NAME value
              #ifdef NAME  #ifndef NAME  #endif

INLINE ASM: asm(""instruction"");
            asm(""line1\nline2\nline3"");
            asm volatile(""hlt"");

IMPORTANT NOTES:
- sizeof only works on bare types: sizeof(int), sizeof(char)
- Local arrays ARE supported: char buf[64]; inside functions
- Global arrays ARE supported: int board[200]; at file scope
- Single-letter variables (i, j, x, y) work fine, use them freely
- Strings use double quotes: ""hello""
- Char literals use single quotes: 'A'
- No float/double support

══════════════════════════════════════════════════════════
STDLIB v3 API — #include ""stdlib.c""
══════════════════════════════════════════════════════════

██ PRINTING — USE printf() AND sprintf() ██
  printf(""text"")                           Print a string
  printf(""Score: %d\n"", score)             Print formatted (%d=int)
  printf(""Addr: 0x%08x\n"", ptr)           Print formatted (%x=hex)
  printf(""Hello %s\n"", name)              Print formatted (%s=string)
  printf(""%c"", ch)                         Print formatted (%c=char)
  sprintf(buf, ""Level %d"", level)          Format into buffer

  NEVER USE: vga_printf, vga_sprintf, kprintf, printk — THESE DO NOT EXIST
  The ONLY print function with format strings is: printf()
  For simple strings without formatting: vga_puts(""hello"") or vga_println(""hello"")
  
  Format specifiers: %d %u %x %X %p %s %c %b %%
  Width: %8d %-8s %08x (right-pad, left-pad, zero-pad)

VGA TEXT (80x25, memory at 0xB8000):
  vga_clear()                    Clear screen
  vga_setcolor(fg, bg)           Set colors (0-15)
  vga_putc(ch)                   Print char at cursor
  vga_putc_at(x, y, ch)         Print at position
  vga_puts(str)                  Print string
  vga_println(str)               Print string + newline
  vga_putint(n)                  Print integer
  vga_puthex(n)                  Print hex (auto-trim zeros)
  vga_puthex_full(n)             Print full 8-digit hex
  vga_putbin(n, bits)            Print binary
  vga_printat(x, y, str)        Print at position
  vga_printat_color(x,y,str,fg,bg)  Print with color
  vga_center(y, str)             Center on row
  vga_setpos(x, y)               Move cursor
  vga_newline()                  Newline
  vga_put(x, y, code)           Put CP437 char
  vga_put_color(x,y,code,fg,bg) Put with color
  vga_int_at(x, y, n)           Number at position
  vga_fill(x,y,w,h,ch)          Fill region
  vga_hline(x,y,len,ch)         Horizontal line
  vga_vline(x,y,len,ch)         Vertical line
  vga_repeat(ch, count)          Repeat char at cursor
  vga_peek(x, y)                 Read char from screen
  cur_hide() / cur_show()        Cursor visibility
  cur_move(x, y)                 Move hardware cursor

COLORS: BLACK=0 BLUE=1 GREEN=2 CYAN=3 RED=4 MAGENTA=5 BROWN=6
        LGRAY=7 DGRAY=8 LBLUE=9 LGREEN=10 LCYAN=11 LRED=12
        LMAGENTA=13 YELLOW=14 WHITE=15

DRAWING:
  draw_box(x,y,w,h)             Single-line box
  draw_dbox(x,y,w,h)            Double-line box
  draw_fill(x,y,w,h,ch)         Fill rect
  draw_fill_color(x,y,w,h,ch,fg,bg)  Fill with color
  draw_shadow(x,y,w,h)          Drop shadow
  draw_progress(x,y,w,val,max)  Progress bar

BOX CHARS: BOX_H=196 BOX_V=179 BOX_TL=218 BOX_TR=191 BOX_BL=192 BOX_BR=217
           DBOX_H=205 DBOX_V=186 DBOX_TL=201 DBOX_TR=187 DBOX_BL=200 DBOX_BR=188
           CH_FULL=219 CH_SHADE1=176 CH_SHADE2=177 CH_SHADE3=178

KEYBOARD (PS/2):
  kb_init()                      CALL FIRST before any kb_ function
  kb_getc()                      Blocking: wait + return ASCII (shift-aware!)
  kb_scan()                      Non-blocking: return scancode or 0
  kb_haskey()                    1 if key waiting
  kb_wait()                      Blocking: return raw scancode
  kb_flush()                     Clear buffer
  kb_getline(buf, maxlen)        Read line with echo + backspace
  input(prompt, buf, maxlen)     Prompted input (prints prompt, reads line)
  Shift keys tracked automatically — uppercase + symbols work

SCANCODES: KEY_ESC=1  KEY_ENTER=28  KEY_SPACE=57  KEY_BKSP=14
  KEY_UP=72 KEY_DOWN=80 KEY_LEFT=75 KEY_RIGHT=77
  KEY_W=17 KEY_A=30 KEY_S=31 KEY_D=32  KEY_Q=16
  KEY_1=2 KEY_2=3 ... KEY_0=11  KEY_F1=59...KEY_F10=68

STRINGS:
  str_len(s)  str_cmp(s1,s2)  str_eq(s1,s2)  str_ncmp(s1,s2,n)
  str_cpy(dst,src)  str_ncpy(dst,src,n)  str_cat(dst,src)
  str_chr(s,ch)     str_rchr(s,ch)    str_str(hay,needle)
  str_starts(s,pfx) str_ends(s,sfx)   str_rev(s)
  str_trim(s)       str_upper(s)      str_lower(s)
  str_count(s,ch)   str_dup(s)        → allocates copy on heap

CONVERSION:
  str_to_int(s)     int_to_str(n,buf)
  hex_to_int(s)     int_to_hex(n,buf)  int_to_hex_full(n,buf)

MEMORY:
  mem_set(ptr,val,cnt)  mem_cpy(dst,src,cnt)  mem_cmp(a,b,cnt)  mem_zero(dst,cnt)
  os_malloc(size) → char*     Heap allocator with free-list (2MB heap)
  os_free(ptr)                Free allocated block
  os_heap_compact()           Coalesce free blocks
  zalloc(size) → char*        Allocate + zero-fill
  alloc(size) → char*         Simple bump allocator (stdlib heap, 1-4MB)
  alloc_reset()               Reset bump allocator

TIMING:
  delay(ms)              Busy-wait (~1ms per unit)
  delay_pit(ms)          PIT-calibrated precise delay
  sleep(seconds)         Sleep N seconds

RANDOM:
  srand(seed)  rand()  randint(min, max)

SOUND (PC speaker):
  play_tone(freq, ms)    speaker_on()  speaker_off()
  snd_click()  snd_drop()  snd_clear()  snd_levelup()  snd_gameover()
  snd_beep()   snd_error()  snd_success()
  Notes: C4=262 D4=294 E4=330 F4=349 G4=392 A4=440 B4=494 C5=523

UTILITY:
  util_abs(n) util_min(a,b) util_max(a,b) util_clamp(n,lo,hi) util_swap(&a,&b)
  sort(arr, count)           Insertion sort on int array

CHAR TESTING:
  is_digit(c) is_alpha(c) is_alnum(c) is_space(c) is_upper(c) is_lower(c) is_print(c) is_hex(c)
  to_upper(c) to_lower(c)

DEBUG:
  panic(msg)              Red screen + halt
  assert(cond, msg)       Panic if false
  hex_dump(addr, count)   Formatted memory dump to screen

PORT I/O (runtime intrinsics):
  inb(port)  outb(port,val)  inw(port)  outw(port,val)  inl(port)  outl(port,val)
  disable_interrupts()  enable_interrupts()  halt()
  read_cr0() write_cr0(val) read_cr3() write_cr3(val)

══════════════════════════════════════════════════════════
CRITICAL: EXACT FUNCTION NAMES — DO NOT INVENT FUNCTIONS
══════════════════════════════════════════════════════════

These are the ONLY functions that exist. If a function is not listed above, it DOES NOT EXIST.

COMMON MISTAKES TO AVOID:
  ❌ vga_printf    → ✅ printf          (printf IS the formatted print)
  ❌ vga_sprintf   → ✅ sprintf
  ❌ kprintf       → ✅ printf
  ❌ printk        → ✅ printf
  ❌ util_randint  → ✅ randint         (NO util_ prefix!)
  ❌ util_rand     → ✅ rand
  ❌ util_srand    → ✅ srand
  ❌ util_delay    → ✅ delay           (for milliseconds; util_delay is raw loops)
  ❌ memset        → ✅ mem_set         (underscore!)
  ❌ memcpy        → ✅ mem_cpy
  ❌ strcmp         → ✅ str_cmp
  ❌ strlen         → ✅ str_len
  ❌ strcpy        → ✅ str_cpy
  ❌ malloc        → ✅ os_malloc
  ❌ free          → ✅ os_free
  ❌ abs           → ✅ util_abs
  ❌ min/max       → ✅ util_min / util_max

NAMING RULE: ALL stdlib functions use the EXACT names shown in the API above.
If you are unsure, use the basic versions: printf, vga_puts, vga_println.

══════════════════════════════════════════════════════════
CODE PATTERNS
══════════════════════════════════════════════════════════

FOR LOOP:
  for (int i = 0; i < 10; i = i + 1) {
      printf(""%d\n"", i);
  }

WHILE LOOP:
  while (running) {
      int key = kb_scan();
      if (key == KEY_ESC) { running = 0; }
      delay(16);
  }

DO-WHILE:
  do {
      key = kb_wait();
  } while (key != KEY_ENTER);

SWITCH:
  switch (cmd) {
      case 1: printf(""One\n""); break;
      case 2: printf(""Two\n""); break;
      default: printf(""Other\n""); break;
  }

COMMAND SHELL:
  char buf[80];
  while (1) {
      printf(""> "");
      kb_getline(buf, 80);
      if (str_eq(buf, ""help"")) { printf(""Commands: help, clear\n""); }
      else if (str_eq(buf, ""clear"")) { vga_clear(); }
      else { printf(""Unknown: %s\n"", buf); }
  }

INLINE ASM (for OS dev):
  asm(""cli"");
  asm(""mov eax, cr0\nand eax, 0xFFFFFFFE\nmov cr0, eax"");

MEMORY ALLOCATION:
  char* buf = os_malloc(256);
  mem_zero(buf, 256);
  str_cpy(buf, ""hello"");
  os_free(buf);

GAME INPUT PATTERN:
  int key = kb_scan();
  if (key == KEY_W || key == KEY_UP) { player_y = player_y - 1; }
  if (key == KEY_S || key == KEY_DOWN) { player_y = player_y + 1; }
  if (key == KEY_A || key == KEY_LEFT) { player_x = player_x - 1; }
  if (key == KEY_D || key == KEY_RIGHT) { player_x = player_x + 1; }

RANDOM NUMBER:
  srand(12345);                      // Seed once at start
  int val = randint(1, 100);         // Random 1-100
  int food_x = randint(2, 77);      // Random within playfield

══════════════════════════════════════════════════════════
COMPLETE OS EXAMPLE — STUDY THIS PATTERN
══════════════════════════════════════════════════════════

This is a COMPLETE working operating system with a shell. Use this as your template
for any OS request. It compiles and runs perfectly.

#include ""stdlib.c""

// ─── Global state ───
char cmd_buf[80];
int cmd_len = 0;
int running = 1;

// ─── UI Drawing ───
void draw_header()
{
	vga_setcolor(COLOR_WHITE, COLOR_BLUE);
	vga_fill(0, 0, 80, 1, ' ');
	vga_printat(2, 0, ""MyOS v1.0"");
	vga_printat(60, 0, ""Type 'help'"");
	vga_setcolor(COLOR_LGRAY, COLOR_BLACK);
}

void draw_prompt()
{
	vga_setcolor(COLOR_LGREEN, COLOR_BLACK);
	vga_puts(""$ "");
	vga_setcolor(COLOR_WHITE, COLOR_BLACK);
}

// ─── Commands ───
void cmd_help()
{
	printf(""Commands:\n"");
	printf(""  help     - Show this help\n"");
	printf(""  clear    - Clear screen\n"");
	printf(""  info     - System information\n"");
	printf(""  echo     - Echo text\n"");
	printf(""  color    - Color test\n"");
	printf(""  reboot   - Restart system\n"");
}

void cmd_info()
{
	printf(""MyOS v1.0\n"");
	printf(""Compiler: SubsetC\n"");
	printf(""Video:    VGA 80x25 text\n"");
	printf(""Heap:     %d bytes used, %d bytes free\n"", os_heap_used(), os_heap_free());
}

void cmd_color_test()
{
	int i;
	for (i = 0; i < 16; i = i + 1) {
		vga_setcolor(i, COLOR_BLACK);
		printf(""Color %d  "", i);
	}
	printf(""\n"");
	vga_setcolor(COLOR_LGRAY, COLOR_BLACK);
}

void cmd_echo(char* args)
{
	printf(""%s\n"", args);
}

void process_command()
{
	if (cmd_len == 0) return;

	if (str_eq(cmd_buf, ""help"")) { cmd_help(); }
	else if (str_eq(cmd_buf, ""clear"")) { vga_clear(); draw_header(); }
	else if (str_eq(cmd_buf, ""info"")) { cmd_info(); }
	else if (str_eq(cmd_buf, ""color"")) { cmd_color_test(); }
	else if (str_eq(cmd_buf, ""reboot"")) { asm(""jmp 0xFFFF0""); }
	else if (str_starts(cmd_buf, ""echo "")) { cmd_echo(cmd_buf + 5); }
	else { printf(""Unknown command: %s\n"", cmd_buf); }
}

// ─── Main ───
void kernel_main()
{
	kb_init();
	srand(54321);
	vga_clear();
	draw_header();
	vga_setpos(0, 2);
	printf(""Welcome to MyOS!\n"");
	printf(""Type 'help' for commands.\n\n"");
	draw_prompt();

	while (running) {
		char ch = kb_getc();
		if (ch == '\n') {
			printf(""\n"");
			process_command();
			cmd_len = 0;
			mem_zero(cmd_buf, 80);
			draw_prompt();
		} else if (ch == '\b') {
			if (cmd_len > 0) {
				cmd_len = cmd_len - 1;
				cmd_buf[cmd_len] = 0;
				// Erase char on screen
				int cx = vga_getx() - 1;
				int cy = vga_gety();
				vga_putc_at(cx, cy, ' ');
				vga_setpos(cx, cy);
			}
		} else if (cmd_len < 78) {
			cmd_buf[cmd_len] = ch;
			cmd_len = cmd_len + 1;
			cmd_buf[cmd_len] = 0;
			vga_putc(ch);
		}
	}

	while (1) {}
}

══════════════════════════════════════════════════════════
GAME TEMPLATE — USE FOR ANY GAME REQUEST
══════════════════════════════════════════════════════════

#include ""stdlib.c""

int player_x = 40;
int player_y = 12;
int score = 0;
int game_over = 0;

void draw_player()
{
	vga_setcolor(COLOR_YELLOW, COLOR_BLACK);
	vga_putc_at(player_x, player_y, '@');
}

void game_loop()
{
	while (game_over == 0) {
		// Input
		int key = kb_scan();
		if (key == KEY_ESC) { game_over = 1; }
		if (key == KEY_W) { if (player_y > 1) player_y = player_y - 1; }
		if (key == KEY_S) { if (player_y < 23) player_y = player_y + 1; }
		if (key == KEY_A) { if (player_x > 1) player_x = player_x - 1; }
		if (key == KEY_D) { if (player_x < 78) player_x = player_x + 1; }

		// Render
		vga_clear();
		draw_box(0, 0, 80, 25);
		draw_player();
		vga_setcolor(COLOR_WHITE, COLOR_BLACK);
		vga_printat(2, 0, "" Score: "");
		vga_int_at(10, 0, score);

		delay(50);
	}
}

void kernel_main()
{
	kb_init();
	srand(12345);
	vga_clear();
	cur_hide();
	game_loop();
	vga_clear();
	vga_center(12, ""GAME OVER"");
	printf(""\nFinal Score: %d\n"", score);
	while (1) {}
}

OUTPUT ONLY CODE. No explanations. First character must be #.
";

        #endregion

        public OllamaService(string baseUrl = "http://localhost:11434")
        {
            _baseUrl = baseUrl;
            _client = new HttpClient();
            _client.Timeout = TimeSpan.FromMinutes(10);
        }

        public void SetModel(string model) => _model = model;
        public string CurrentModel => _model;

        public async Task<bool> IsAvailableAsync()
        {
            try
            {
                var response = await _client.GetAsync($"{_baseUrl}/api/tags");
                return response.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        public async Task<List<string>> GetModelsAsync()
        {
            var models = new List<string>();
            try
            {
                var response = await _client.GetAsync($"{_baseUrl}/api/tags");
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("models", out var arr))
                    {
                        foreach (var m in arr.EnumerateArray())
                        {
                            if (m.TryGetProperty("name", out var name))
                                models.Add(name.GetString());
                        }
                    }
                }
            }
            catch (Exception ex) { OnError?.Invoke($"Failed to get models: {ex.Message}"); }
            return models;
        }

        public async Task GenerateStreamAsync(string prompt, CancellationToken ct = default)
        {
            try
            {
                var request = new Dictionary<string, object>
                {
                    { "model", _model },
                    { "prompt", prompt },
                    { "system", SYSTEM_PROMPT },
                    { "stream", true },
                    { "options", new Dictionary<string, object> {
                        { "temperature", 0.7 },
                        { "num_ctx", 16384 },  // Large context for big code
                        { "num_predict", 4096 } // Allow long responses
                    }}
                };

                var json = JsonSerializer.Serialize(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var httpReq = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/generate") { Content = content };

                using var response = await _client.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var err = await response.Content.ReadAsStringAsync();
                    OnError?.Invoke($"API Error {response.StatusCode}: {err}");
                    return;
                }

                using var stream = await response.Content.ReadAsStreamAsync();
                using var reader = new System.IO.StreamReader(stream);

                while (!reader.EndOfStream && !ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync();
                    if (string.IsNullOrEmpty(line)) continue;

                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        if (doc.RootElement.TryGetProperty("response", out var token))
                        {
                            var text = token.GetString();
                            if (!string.IsNullOrEmpty(text))
                                OnTokenReceived?.Invoke(text);
                        }
                        if (doc.RootElement.TryGetProperty("done", out var done) && done.GetBoolean())
                            break;
                    }
                    catch (JsonException) { }
                }
                OnComplete?.Invoke();
            }
            catch (OperationCanceledException) { OnComplete?.Invoke(); }
            catch (Exception ex) { OnError?.Invoke(ex.Message); }
        }

        #region Prompt Templates

        public static string Prompt_Explain(string code, string filename) =>
            $"Explain this SubsetC code. What does it do? Any issues?\n\nFile: {filename}\n```c\n{code}\n```";

        public static string Prompt_FixErrors(string code, string errors, string filename) =>
            $@"Fix these compilation errors in SubsetC code. 

ERRORS:
{errors}

CODE ({filename}):
```c
{code}
```

RULES:
- Use printf() for formatted output, NOT vga_printf (doesn't exist)
- Use vga_puts/vga_println for simple strings
- switch/case/default is supported
- for loops are supported
- do-while is supported
- Single-letter variables (i, j, x, y) are fine
- Use asm(""instruction""); for inline assembly

Show the COMPLETE corrected code.";

        public static string Prompt_Optimize(string code, string filename) =>
            $"Analyze this SubsetC code for optimizations. Consider:\n- Performance\n- Memory usage\n- Code clarity\n- Potential bugs\n\nFile: {filename}\n```c\n{code}\n```";

        public static string Prompt_AddFeature(string code, string feature, string filename) =>
            $"Add this feature to the code: {feature}\n\nCurrent code ({filename}):\n```c\n{code}\n```\n\nReturn the COMPLETE updated code with the feature integrated properly.";

        public static string Prompt_MakeGame(string gameType) =>
            $@"Create a complete {gameType} game for SubsetC OS.

Requirements:
- Start with #include ""stdlib.c""
- Entry point: void kernel_main()
- Call kb_init() and srand(12345) first
- Game loop: input → update → render → delay(50)
- Use randint(min, max) for random numbers (NOT util_randint)
- Use printf() for formatted text (NOT vga_printf)
- Use delay(ms) for timing
- Show score, controls, game over screen
- Sound: snd_click(), snd_levelup(), snd_gameover()
- End with while (1) {{}}

Output ONLY code, no explanations.";

        public static string Prompt_MakeOS(string description) =>
            $@"Create a SubsetC operating system with: {description}

Follow the COMPLETE OS EXAMPLE from your training exactly.

Requirements:
- Start with #include ""stdlib.c""
- Entry point: void kernel_main()
- Call kb_init() first
- Shell with command parsing using kb_getc() character-by-character
- Built-in commands: help, clear, echo, info
- Use printf() for formatted output (NOT vga_printf, NOT kprintf)
- Use str_eq() to compare commands (NOT strcmp)  
- Use str_starts() for commands with arguments
- Use os_heap_used()/os_heap_free() for memory info
- Professional header bar with vga_fill + vga_printat
- End with while (1) {{}}

Output ONLY code, no explanations.";

        public static string Prompt_Debug(string code, string issue, string filename) =>
            $@"Debug this SubsetC code. 

ISSUE: {issue}

Common problems to check:
1. Variable names conflicting with x86 registers
2. Kernel size exceeding sector limit (~63KB)
3. Stack overflow from deep recursion or large local arrays
4. Bootloader org not matching kernel load address
5. Missing kb_init() before keyboard functions
6. Missing srand() before random functions

CODE ({filename}):
```c
{code}
```

Identify the problem and show the fix.";

        #endregion

        #region Example Prompts

        public static readonly string[] ExamplePrompts = new[]
        {
            // Games
            "Create a Snake game with score and sound",
            "Make Pong with AI opponent",
            "Create Space Invaders",
            "Make Breakout/Arkanoid",
            "Create Tetris clone",
            "Make a simple RPG with stats",
            
            // OS Features
            "Create an OS with a shell",
            "Make a text editor",
            "Create a file manager UI",
            "Build a calculator app",
            "Make a system monitor showing memory",
            
            // Learning
            "Explain how the bootloader works",
            "Show me the memory map",
            "How do I use the keyboard?",
            "Explain VGA text mode",
            "How does the PC speaker work?",
            
            // Debugging
            "Why won't my code compile?",
            "My game runs too fast",
            "Keyboard not responding",
            "Screen is garbled",
            "QEMU hangs at boot"
        };

        #endregion
    }
}