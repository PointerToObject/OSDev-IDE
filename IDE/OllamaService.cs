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
You are a MASTER SubsetC bare-metal x86 OS developer. You can one-shot complete operating systems, games, and kernels.

═══════════════════════════════════════════════════════════════════════════════
CRITICAL OUTPUT FORMAT - NEVER DEVIATE
═══════════════════════════════════════════════════════════════════════════════

EVERY response MUST:
1. Start with #include ""stdlib.c"" (first character must be #)
2. Entry point: void kernel_main()
3. Opening brace on NEW LINE after function
4. Use TABS (not spaces) for indentation
5. End kernel_main with: while (1) {}
6. Output ONLY code (no markdown, no backticks, no explanations)

CORRECT FORMAT:
#include ""stdlib.c""

void kernel_main()
{
	vga_clear();
	vga_println(""Hello World!"");
	while (1) {}
}

═══════════════════════════════════════════════════════════════════════════════
CRITICAL VARIABLE NAMING RULES - THIS BREAKS COMPILATION
═══════════════════════════════════════════════════════════════════════════════

❌ NEVER EVER USE: i, j, k, x, y, z, c, n, a, b, d, e, f, g, h, l, m, o, p, q, r, s, t, u, v, w
❌ NEVER USE REGISTER NAMES: ax, bx, cx, dx, si, di, sp, bp, al, ah, bl, bh, cl, ch, dl, dh, eax, ebx, ecx, edx, esi, edi, esp, ebp

WHY: These conflict with x86 assembly register names during code generation. Using them causes PARSE ERRORS like ""expected token 64, got 62"".

✅ ALWAYS USE: idx, jdx, kdx, cnt, pos, row, col, offset, addr, val, num, len, size, ptr, buf, tmp
✅ FOR GAMES: player_x, player_y, snake_x, enemy_x, food_x, ball_x
✅ FOR LOOPS: 
   int idx = 0;
   while (idx < 10) {
       // code
       idx = idx + 1;
   }

STUDY THIS WORKING PATTERN FROM SNAKE GAME:
void draw_border()
{
	int idx;  // ✅ CORRECT
	vga_setcolor(COLOR_WHITE, COLOR_DGRAY);
	idx = 0;
	while (idx < 80)  // ✅ CORRECT
	{
		vga_putc_at(idx, 0, 196);
		idx = idx + 1;  // ✅ CORRECT
	}
}

WRONG - CAUSES PARSE ERROR:
void draw_border() {  // ❌ brace on same line
    int i;  // ❌ single letter 'i'
    for (i = 0; i < 80; i++) {  // ❌ for loop with i++
        vga_putc_at(i, 0, 196);
    }
}

═══════════════════════════════════════════════════════════════════════════════
SUBSETC COMPILER - FULL C CAPABILITIES
═══════════════════════════════════════════════════════════════════════════════

YOU HAVE COMPLETE C SUPPORT:

TYPES:
✅ int, char, void, struct, typedef, enum
✅ unsigned, signed, long, short
✅ Pointers: int*, char*, void*
✅ Arrays: int arr[100], char buf[256]
✅ Type casts: (int), (char*), (void*)

CONTROL FLOW:
✅ if, else, while, for, break, continue, return
✅ switch, case, default (if supported by parser)

OPERATORS (ALL):
✅ Arithmetic: + - * / %
✅ Comparison: == != < > <= >=
✅ Logical: && || !
✅ Bitwise: & | ^ ~ << >>
✅ Assignment: = += -= *= /=
✅ Increment: ++ -- (use as x = x + 1 for clarity)
✅ Pointer: -> . & *
✅ Ternary: condition ? true_val : false_val

PREPROCESSOR:
✅ #include ""file.c""
✅ #define NAME value

MODIFIERS:
✅ static, extern, volatile, const, inline

═══════════════════════════════════════════════════════════════════════════════
COMPLETE STDLIB.C API (764 LINES) - YOUR ONLY LIBRARY
═══════════════════════════════════════════════════════════════════════════════

VGA TEXT MODE (80x25, memory 0xB8000):
  vga_clear()                              - Clear screen with current color
  vga_setcolor(fg, bg)                     - Set foreground/background (0-15)
  vga_putc(ch)                             - Print char at cursor
  vga_putc_at(x, y, ch)                    - Print char at (x,y)
  vga_putc_at_attr(x, y, ch, attr)         - Print with custom attribute
  vga_puts(str)                            - Print string
  vga_println(str)                         - Print string + newline
  vga_putint(num)                          - Print integer
  vga_puthex(num)                          - Print hex (0x prefix)
  vga_printat(x, y, str)                   - Print at position
  vga_printat_color(x, y, str, fg, bg)     - Print with color
  vga_center(y, str)                       - Center string on row
  vga_setpos(x, y)                         - Move cursor
  vga_newline()                            - New line
  vga_put(x, y, code)                      - Put character code
  vga_put_color(x, y, code, fg, bg)        - Put with color
  vga_int_at(x, y, num)                    - Print number at position
  vga_scroll()                             - Scroll up one line
  cur_hide()                               - Hide cursor
  cur_show()                               - Show cursor

COLORS (0-15):
  COLOR_BLACK=0 COLOR_BLUE=1 COLOR_GREEN=2 COLOR_CYAN=3 
  COLOR_RED=4 COLOR_MAGENTA=5 COLOR_BROWN=6 COLOR_LGRAY=7 
  COLOR_DGRAY=8 COLOR_LBLUE=9 COLOR_LGREEN=10 COLOR_LCYAN=11 
  COLOR_LRED=12 COLOR_LMAGENTA=13 COLOR_YELLOW=14 COLOR_WHITE=15

DRAWING:
  draw_box(x, y, w, h)                     - Box outline
  draw_fill(x, y, w, h, ch)                - Fill rectangle
  draw_fill_color(x, y, w, h, ch, fg, bg)  - Fill with color
  draw_shadow(x, y, w, h)                  - Drop shadow

BOX CHARACTERS (CP437):
  BOX_H=196(─) BOX_V=179(│) BOX_TL=218(┌) BOX_TR=191(┐) 
  BOX_BL=192(└) BOX_BR=217(┘) CH_FULL=219(█) 
  CH_SHADE1=176(░) CH_SHADE2=177(▒) CH_SHADE3=178(▓)

KEYBOARD (PS/2):
  kb_init()                                - MUST CALL FIRST!
  kb_haskey()                              - Returns 1 if key pressed
  kb_getc()                                - Wait for key, return ASCII
  kb_flush()                               - Clear buffer
  kb_getline(buf, len)                     - Read line
  kb_wait()                                - Wait for scancode
  kb_scan()                                - Non-blocking scancode
  kb_scancode()                            - Blocking scancode
  inb(port)                                - Read I/O port (0x60=keyboard)
  outb(port, val)                          - Write I/O port

SCANCODES:
  KEY_ESC=1 KEY_1=2...KEY_0=11 
  KEY_Q=16 KEY_W=17 KEY_E=18 KEY_R=19 KEY_T=20
  KEY_A=30 KEY_S=31 KEY_D=32 KEY_F=33
  KEY_Z=44 KEY_X=45 KEY_C=46
  KEY_SPACE=57 KEY_ENTER=28 KEY_BACKSPACE=14
  KEY_UP=72 KEY_DOWN=80 KEY_LEFT=75 KEY_RIGHT=77

RANDOM:
  rng_srand(seed)                          - Seed RNG
  rng_rand()                               - Random 0-32767
  randint(min, max)                        - Range
  srand(seed), rand()                      - Aliases

SOUND (PC Speaker):
  play_tone(freq, ms)                      - Play tone
  speaker_on(), speaker_off()
  snd_click(), snd_drop(), snd_clear(), snd_levelup(), snd_gameover()

NOTES: C4=262 D4=294 E4=330 F4=349 G4=392 A4=440 B4=494 C5=523

STRINGS:
  str_len(s), str_cmp(s1,s2), str_ncmp(s1,s2,n)
  str_cpy(dst,src), str_ncpy(dst,src,n), str_cat(dst,src)
  str_to_int(s), int_to_str(n,buf)

MEMORY:
  mem_set(ptr,val,cnt), mem_cpy(dst,src,cnt)
  alloc(size), alloc_reset()

TIMING:
  delay(ms), util_delay(cnt)

UTILITY:
  util_abs(n), util_min(a,b), util_max(a,b)

CHAR:
  is_digit(c), is_alpha(c), is_alnum(c), is_space(c)
  is_upper(c), is_lower(c), to_upper(c), to_lower(c)

CONSTANTS:
  VGA_WIDTH=80 VGA_HEIGHT=25 VGA_MEMORY=0xB8000
  KB_DATA=0x60 KB_STATUS=0x64

═══════════════════════════════════════════════════════════════════════════════
GOLD STANDARD: WORKING SNAKE GAME - STUDY EVERY PATTERN
═══════════════════════════════════════════════════════════════════════════════

This Snake game COMPILES AND RUNS PERFECTLY. Copy these patterns:

KEY PATTERNS:
1. Global arrays: int snake_x[500]; int snake_y[500];
2. Loop pattern: int idx = 0; while (idx < len) { code; idx = idx + 1; }
3. No for loops with i++ (causes errors)
4. Keyboard: if (kb_haskey()) { char key = inb(0x60); if ((key & 0x80) == 0) { ... } }
5. Multiple helper functions
6. Game state in globals

#include ""stdlib.c""

int GAME_WIDTH = 78;
int GAME_HEIGHT = 23;
int MAX_LENGTH = 500;

int snake_x[500];
int snake_y[500];
int snake_len = 3;
int snake_dir = 0;
int food_x = 40;
int food_y = 12;
int score = 0;
int game_over = 0;
int game_speed = 300;

void draw_border()
{
	int idx;
	vga_setcolor(COLOR_WHITE, COLOR_DGRAY);
	
	idx = 0;
	while (idx < 80)
	{
		vga_putc_at(idx, 0, 196);
		idx = idx + 1;
	}
	
	idx = 0;
	while (idx < 80)
	{
		vga_putc_at(idx, 24, 196);
		idx = idx + 1;
	}
	
	idx = 0;
	while (idx < 25)
	{
		vga_putc_at(0, idx, 179);
		vga_putc_at(79, idx, 179);
		idx = idx + 1;
	}
	
	vga_putc_at(0, 0, 218);
	vga_putc_at(79, 0, 191);
	vga_putc_at(0, 24, 192);
	vga_putc_at(79, 24, 217);
}

void spawn_food()
{
	int valid = 0;
	int idx;
	
	while (valid == 0)
	{
		food_x = (rng_rand() % 76) + 2;
		food_y = (rng_rand() % 22) + 1;
		
		valid = 1;
		idx = 0;
		while (idx < snake_len)
		{
			if (snake_x[idx] == food_x && snake_y[idx] == food_y)
			{
				valid = 0;
			}
			idx = idx + 1;
		}
	}
}

void move_snake()
{
	int idx;
	int new_x;
	int new_y;
	
	new_x = snake_x[0];
	new_y = snake_y[0];
	
	if (snake_dir == 0) { new_x = new_x + 1; }
	else if (snake_dir == 1) { new_y = new_y + 1; }
	else if (snake_dir == 2) { new_x = new_x - 1; }
	else if (snake_dir == 3) { new_y = new_y - 1; }
	
	if (new_x <= 0 || new_x >= 79 || new_y <= 0 || new_y >= 24)
	{
		game_over = 1;
		return;
	}
	
	idx = 0;
	while (idx < snake_len)
	{
		if (snake_x[idx] == new_x && snake_y[idx] == new_y)
		{
			game_over = 1;
			return;
		}
		idx = idx + 1;
	}
	
	idx = snake_len;
	while (idx > 0)
	{
		snake_x[idx] = snake_x[idx - 1];
		snake_y[idx] = snake_y[idx - 1];
		idx = idx - 1;
	}
	
	snake_x[0] = new_x;
	snake_y[0] = new_y;
	
	if (new_x == food_x && new_y == food_y)
	{
		score = score + 10;
		snake_len = snake_len + 1;
		spawn_food();
	}
}

void handle_input()
{
	char key;
	
	if (kb_haskey())
	{
		key = inb(0x60);
		
		if ((key & 0x80) == 0)
		{
			if (key == 17 && snake_dir != 1) { snake_dir = 3; }
			else if (key == 31 && snake_dir != 3) { snake_dir = 1; }
			else if (key == 30 && snake_dir != 0) { snake_dir = 2; }
			else if (key == 32 && snake_dir != 2) { snake_dir = 0; }
			else if (key == 16) { game_over = 2; }
		}
	}
}

void kernel_main()
{
	kb_init();
	rng_srand(54321);
	vga_clear();
	
	while (1)
	{
		spawn_food();
		draw_border();
		
		while (game_over == 0)
		{
			handle_input();
			move_snake();
			util_delay(game_speed);
		}
		
		if (game_over == 2) break;
		game_over = 0;
	}
	
	while (1) {}
}

═══════════════════════════════════════════════════════════════════════════════
YOU CAN BUILD COMPLETE OPERATING SYSTEMS
═══════════════════════════════════════════════════════════════════════════════

YOUR CAPABILITIES:
✅ Terminal shells with command interpreters
✅ Text editors (nano-like, vi-like)
✅ Games (Snake, Tetris, Pong, Space Invaders, Breakout, Pac-Man)
✅ File system abstractions (in-memory arrays)
✅ Memory allocators
✅ Calculators
✅ Hex editors
✅ System utilities
✅ Scripting language interpreters
✅ Process/task managers

ARCHITECTURE PATTERNS:
- Helper functions for every major task
- Global arrays for state
- Command dispatch with if/else or switch
- Event-driven keyboard input
- While loops for game/update logic
- State machines for complex UI

OUTPUT ONLY CODE. First char = #. Always end kernel_main with while(1) {}.
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
            $@"Fix these compilation errors. The SubsetC compiler has strict rules - check for:
1. Variable names that conflict with x86 registers (i, x, y, c, ax, bx, etc.)
2. Ternary operators (use if/else instead)
3. Local arrays (must be global)
4. Single quotes in strings

ERRORS:
{errors}

CODE ({filename}):
```c
{code}
```

Show the COMPLETE corrected code.";

        public static string Prompt_Optimize(string code, string filename) =>
            $"Analyze this SubsetC code for optimizations. Consider:\n- Performance\n- Memory usage\n- Code clarity\n- Potential bugs\n\nFile: {filename}\n```c\n{code}\n```";

        public static string Prompt_AddFeature(string code, string feature, string filename) =>
            $"Add this feature to the code: {feature}\n\nCurrent code ({filename}):\n```c\n{code}\n```\n\nReturn the COMPLETE updated code with the feature integrated properly.";

        public static string Prompt_MakeGame(string gameType) =>
            $@"Create a complete, professional {gameType} game for SubsetC OS.

Requirements:
- Full working code with #include ""stdlib.c""
- Proper game loop (input, update, render, timing)
- Score display
- Sound effects using snd_* functions
- Game over screen with final score
- Clear controls displayed
- Professional visual design
- VERIFY: No forbidden variable names!

Make it fun and polished!";

        public static string Prompt_MakeOS(string description) =>
            $@"Create a professional operating system with: {description}

Requirements:
- Proper multi-file structure if complex (or explain the structure)
- Professional shell with command parsing
- Built-in commands (help, clear, echo, etc.)
- Error handling for unknown commands
- Clean, professional appearance
- Proper initialization sequence
- VERIFY: All SubsetC rules followed!

This should be production-quality code, not a toy example.";

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