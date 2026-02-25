using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using System.Windows.Media;

namespace OSDevIDE
{
    /// <summary>
    /// SubsetC IntelliSense provider. Shows completions for:
    /// - All stdlib v3 functions with signatures
    /// - Constants/defines
    /// - Keywords + control flow
    /// - User-defined functions, globals, structs, enums from current file
    /// - Struct member access via -> and .
    ///
    /// INTEGRATION:
    ///   In MainWindow constructor after InitializeComponent():
    ///     _completion = new SubsetCCompletion(CodeEditor);
    ///   When opening a file:
    ///     _completion.ParseUserCode(File.ReadAllText(filePath));
    /// </summary>
    public class SubsetCCompletion
    {
        private TextEditor _editor;
        private CompletionWindow _window;
        private List<CompletionEntry> _stdlibEntries;
        private List<CompletionEntry> _keywordEntries;
        private List<CompletionEntry> _userEntries = new List<CompletionEntry>();
        private Dictionary<string, List<CompletionEntry>> _structMembers = new Dictionary<string, List<CompletionEntry>>();
        private List<string> _structTypeNames = new List<string>();

        /// <summary>
        /// Get list of user-defined struct/typedef names for syntax highlighting.
        /// </summary>
        public IReadOnlyList<string> StructTypeNames => _structTypeNames;

        public SubsetCCompletion(TextEditor editor)
        {
            _editor = editor;
            _editor.TextArea.TextEntering += OnTextEntering;
            _editor.TextArea.TextEntered += OnTextEntered;
            BuildStdlibEntries();
            BuildKeywordEntries();
        }

        public void Detach()
        {
            _editor.TextArea.TextEntering -= OnTextEntering;
            _editor.TextArea.TextEntered -= OnTextEntered;
        }

        #region Event Handlers

        private void OnTextEntering(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            if (e.Text.Length > 0 && _window != null)
            {
                if (!char.IsLetterOrDigit(e.Text[0]) && e.Text[0] != '_')
                {
                    _window.CompletionList.RequestInsertion(e);
                }
            }
        }

        private void OnTextEntered(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            // Trigger on letter, underscore, or after -> / .
            if (e.Text == ">" || e.Text == ".")
            {
                TryShowMemberCompletion();
                return;
            }

            if (e.Text.Length == 1 && (char.IsLetter(e.Text[0]) || e.Text[0] == '_'))
            {
                string prefix = GetCurrentWord();
                if (prefix.Length >= 2)
                    ShowCompletion(prefix);
            }
        }

        #endregion

        #region Completion Logic

        private string GetCurrentWord()
        {
            int offset = _editor.CaretOffset;
            var doc = _editor.Document;
            int start = offset;
            while (start > 0)
            {
                char c = doc.GetCharAt(start - 1);
                if (char.IsLetterOrDigit(c) || c == '_')
                    start--;
                else
                    break;
            }
            return doc.GetText(start, offset - start);
        }

        private void ShowCompletion(string prefix)
        {
            if (_window != null) return;

            var matches = new List<CompletionEntry>();
            string lower = prefix.ToLower();

            // Search all sources
            foreach (var entry in _stdlibEntries.Concat(_keywordEntries).Concat(_userEntries))
            {
                if (entry.Text.ToLower().StartsWith(lower) ||
                    entry.Text.ToLower().Contains(lower))
                    matches.Add(entry);
            }

            // Sort: exact prefix first, then starts-with, then contains
            matches = matches
                .OrderByDescending(m => m.Text.ToLower().StartsWith(lower))
                .ThenBy(m => m.Text)
                .Distinct(new CompletionEntryComparer())
                .Take(30)
                .ToList();

            if (matches.Count == 0) return;

            _window = new CompletionWindow(_editor.TextArea);
            _window.StartOffset -= prefix.Length;
            ApplyDarkTheme(_window);

            var data = _window.CompletionList.CompletionData;
            foreach (var m in matches)
                data.Add(new SubsetCCompletionData(m));

            _window.Show();
            _window.Closed += (s, ev) => _window = null;
        }

        /// <summary>
        /// Style the CompletionWindow to match dark IDE theme — no white borders anywhere
        /// </summary>
        private void ApplyDarkTheme(CompletionWindow window)
        {
            var bg = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x1E, 0x1E));
            var bgSlightly = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x25, 0x25, 0x26));
            var fg = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xCC, 0xCC));
            var borderColor = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3F, 0x3F, 0x46));
            var transparent = System.Windows.Media.Brushes.Transparent;

            // Window itself
            window.Background = bg;
            window.Foreground = fg;
            window.BorderBrush = borderColor;
            window.BorderThickness = new System.Windows.Thickness(1);
            window.WindowStyle = System.Windows.WindowStyle.None;
            window.AllowsTransparency = false;
            window.ResizeMode = System.Windows.ResizeMode.NoResize;

            // Kill the outer border glow/chrome
            try { window.Effect = null; } catch { }

            // Style the CompletionList and its inner ListBox
            var list = window.CompletionList;
            if (list != null)
            {
                list.Background = bg;
                list.Foreground = fg;
                list.BorderBrush = transparent;
                list.BorderThickness = new System.Windows.Thickness(0);

                if (list.ListBox != null)
                {
                    list.ListBox.Background = bg;
                    list.ListBox.Foreground = fg;
                    list.ListBox.BorderBrush = transparent;
                    list.ListBox.BorderThickness = new System.Windows.Thickness(0);
                    list.ListBox.Padding = new System.Windows.Thickness(0);

                    // Item container style
                    var itemStyle = new System.Windows.Style(typeof(System.Windows.Controls.ListBoxItem));
                    itemStyle.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.BackgroundProperty, bg));
                    itemStyle.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.ForegroundProperty, fg));
                    itemStyle.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.BorderThicknessProperty, new System.Windows.Thickness(0)));
                    itemStyle.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.BorderBrushProperty, transparent));
                    itemStyle.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.PaddingProperty, new System.Windows.Thickness(4, 2, 4, 2)));
                    itemStyle.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.MarginProperty, new System.Windows.Thickness(0)));

                    // Selected
                    var selTrigger = new System.Windows.Trigger { Property = System.Windows.Controls.ListBoxItem.IsSelectedProperty, Value = true };
                    selTrigger.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.BackgroundProperty,
                        new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x04, 0x39, 0x5E))));
                    selTrigger.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.ForegroundProperty,
                        new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF))));
                    selTrigger.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.BorderBrushProperty, transparent));
                    itemStyle.Triggers.Add(selTrigger);

                    // Hover
                    var hoverTrigger = new System.Windows.Trigger { Property = System.Windows.UIElement.IsMouseOverProperty, Value = true };
                    hoverTrigger.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.BackgroundProperty,
                        new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x2D, 0x2E))));
                    hoverTrigger.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.BorderBrushProperty, transparent));
                    itemStyle.Triggers.Add(hoverTrigger);

                    list.ListBox.ItemContainerStyle = itemStyle;
                }
            }

            // Kill ToolTip white borders globally on this window
            window.Loaded += (s, ev) =>
            {
                KillWhiteBorders(window);
            };
        }

        /// <summary>
        /// Walk the visual tree and nuke any white/light borders
        /// </summary>
        private void KillWhiteBorders(System.Windows.DependencyObject root)
        {
            try
            {
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
                {
                    var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);

                    if (child is System.Windows.Controls.Border border)
                    {
                        if (border.BorderBrush is SolidColorBrush brush)
                        {
                            var c = brush.Color;
                            // Kill anything that looks white or light gray
                            if (c.R > 0x80 && c.G > 0x80 && c.B > 0x80)
                            {
                                border.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3F, 0x3F, 0x46));
                            }
                        }
                        // Also fix background if it's white
                        if (border.Background is SolidColorBrush bgBrush)
                        {
                            var c = bgBrush.Color;
                            if (c.R > 0xE0 && c.G > 0xE0 && c.B > 0xE0)
                            {
                                border.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x1E, 0x1E));
                            }
                        }
                    }

                    if (child is System.Windows.Controls.Control ctrl)
                    {
                        if (ctrl.BorderBrush is SolidColorBrush ctrlBrush)
                        {
                            var c = ctrlBrush.Color;
                            if (c.R > 0x80 && c.G > 0x80 && c.B > 0x80)
                                ctrl.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3F, 0x3F, 0x46));
                        }
                    }

                    KillWhiteBorders(child);
                }
            }
            catch { }
        }

        private void TryShowMemberCompletion()
        {
            // Check if we just typed -> or .
            int offset = _editor.CaretOffset;
            var doc = _editor.Document;

            bool isArrow = false;
            if (offset >= 2 && doc.GetCharAt(offset - 2) == '-' && doc.GetCharAt(offset - 1) == '>')
                isArrow = true;
            else if (offset >= 1 && doc.GetCharAt(offset - 1) == '.')
                isArrow = false;
            else
                return;

            // Get the variable name before -> or .
            int nameEnd = isArrow ? offset - 2 : offset - 1;
            int nameStart = nameEnd;
            while (nameStart > 0 && (char.IsLetterOrDigit(doc.GetCharAt(nameStart - 1)) || doc.GetCharAt(nameStart - 1) == '_'))
                nameStart--;
            string varName = doc.GetText(nameStart, nameEnd - nameStart);

            // Find the type of this variable
            string typeName = FindVariableType(varName);
            if (typeName != null && _structMembers.ContainsKey(typeName))
            {
                var members = _structMembers[typeName];
                if (members.Count == 0) return;

                _window = new CompletionWindow(_editor.TextArea);
                ApplyDarkTheme(_window);
                var data = _window.CompletionList.CompletionData;
                foreach (var m in members)
                    data.Add(new SubsetCCompletionData(m));
                _window.Show();
                _window.Closed += (s, ev) => _window = null;
            }
        }

        private string FindVariableType(string varName)
        {
            string text = _editor.Text;

            // Pattern 1: "TypeName varName" or "TypeName *varName" (local or global)
            var match = Regex.Match(text, @"(\w+)\s*\*?\s+\*?" + Regex.Escape(varName) + @"\s*[;=\[\(,]");
            if (match.Success)
            {
                string typeName = match.Groups[1].Value;
                // Skip C keywords that aren't types
                if (typeName != "return" && typeName != "if" && typeName != "while" && typeName != "for" && typeName != "switch")
                {
                    // Check if it's a known struct type
                    if (_structMembers.ContainsKey(typeName))
                        return typeName;
                }
            }

            // Pattern 2: "struct TypeName varName" or "struct TypeName *varName"
            var structMatch = Regex.Match(text, @"struct\s+(\w+)\s*\*?\s+" + Regex.Escape(varName) + @"\s*[;=\[\(,]");
            if (structMatch.Success)
                return structMatch.Groups[1].Value;

            return null;
        }

        #endregion

        #region Parse User Code

        /// <summary>
        /// Parse the current source file to extract user-defined symbols.
        /// Call this when opening a file or after significant edits.
        /// </summary>
        public void ParseUserCode(string code)
        {
            _userEntries.Clear();
            _structMembers.Clear();
            _structTypeNames.Clear();

            if (string.IsNullOrEmpty(code)) return;

            // Extract function declarations: type name(params)
            var funcMatches = Regex.Matches(code,
                @"^(?:static\s+)?(?:inline\s+)?(\w+\*?\s+\*?)(\w+)\s*\(([^)]*)\)\s*\{",
                RegexOptions.Multiline);
            foreach (Match m in funcMatches)
            {
                string retType = m.Groups[1].Value.Trim();
                string name = m.Groups[2].Value.Trim();
                string parms = m.Groups[3].Value.Trim();
                if (name == "if" || name == "while" || name == "for" || name == "switch") continue;
                _userEntries.Add(new CompletionEntry
                {
                    Text = name,
                    Description = $"{retType} {name}({parms})",
                    Category = "function",
                    InsertText = name + "("
                });
            }

            // Extract global variables: type name = ...; or type name;
            var globalMatches = Regex.Matches(code,
                @"^((?:static\s+)?(?:const\s+)?(?:volatile\s+)?(?:unsigned\s+)?(?:int|char|void|long|short)\s*\*?\s*)(\w+)\s*(?:=\s*[^;]+)?;",
                RegexOptions.Multiline);
            foreach (Match m in globalMatches)
            {
                string type = m.Groups[1].Value.Trim();
                string name = m.Groups[2].Value.Trim();
                // Skip if inside a function (rough check: preceded by indentation)
                if (m.Index > 0 && (code[m.Index - 1] == '\t' || code[m.Index - 1] == ' '))
                    continue;
                _userEntries.Add(new CompletionEntry
                {
                    Text = name,
                    Description = $"{type} {name}",
                    Category = "variable"
                });
            }

            // Extract global arrays: type name[size];
            var arrayMatches = Regex.Matches(code,
                @"^((?:int|char)\s*\*?\s*)(\w+)\s*\[(\d+)\]\s*;",
                RegexOptions.Multiline);
            foreach (Match m in arrayMatches)
            {
                string type = m.Groups[1].Value.Trim();
                string name = m.Groups[2].Value.Trim();
                string size = m.Groups[3].Value;
                _userEntries.Add(new CompletionEntry
                {
                    Text = name,
                    Description = $"{type} {name}[{size}]",
                    Category = "variable"
                });
            }

            // Extract #define constants
            var defineMatches = Regex.Matches(code, @"^#define\s+(\w+)\s+(.+)$", RegexOptions.Multiline);
            foreach (Match m in defineMatches)
            {
                _userEntries.Add(new CompletionEntry
                {
                    Text = m.Groups[1].Value,
                    Description = $"#define {m.Groups[1].Value} {m.Groups[2].Value.Trim()}",
                    Category = "constant"
                });
            }

            // Extract struct definitions and their members
            // Pattern 1: struct Name { ... }
            // Pattern 2: typedef struct { ... } Name;
            // Pattern 3: typedef struct Name { ... } Alias;
            var structMatches = Regex.Matches(code,
                @"(?:typedef\s+)?struct\s+(\w+)?\s*\{([^}]+)\}\s*(\w+)?\s*;",
                RegexOptions.Singleline);
            foreach (Match m in structMatches)
            {
                string nameBefore = m.Groups[1].Value.Trim();  // struct Name { }
                string members = m.Groups[2].Value;
                string nameAfter = m.Groups[3].Value.Trim();   // } Name;

                // Determine the usable struct name(s)
                var names = new List<string>();
                if (!string.IsNullOrEmpty(nameBefore)) names.Add(nameBefore);
                if (!string.IsNullOrEmpty(nameAfter) && nameAfter != nameBefore) names.Add(nameAfter);
                if (names.Count == 0) continue;

                // Parse members
                var memberList = new List<CompletionEntry>();
                var memberMatches = Regex.Matches(members,
                    @"(\w[\w\s\*]*?)\s+(\w+)\s*(?:\[(\d+)\])?\s*;");
                foreach (Match mm in memberMatches)
                {
                    string memberType = mm.Groups[1].Value.Trim();
                    string memberName = mm.Groups[2].Value.Trim();
                    memberList.Add(new CompletionEntry
                    {
                        Text = memberName,
                        Description = $"{memberType} {memberName}" +
                                      (mm.Groups[3].Success ? $"[{mm.Groups[3].Value}]" : ""),
                        Category = "member"
                    });
                }

                // Register ALL names as struct types with the same member list
                foreach (string name in names)
                {
                    _structMembers[name] = memberList;
                    _userEntries.Add(new CompletionEntry
                    {
                        Text = name,
                        Description = $"struct {name} {{ {memberList.Count} members }}",
                        Category = "type",
                        InsertText = name
                    });

                    // Also track for syntax highlighting
                    if (!_structTypeNames.Contains(name))
                        _structTypeNames.Add(name);
                }
            }

            // Extract enum values
            var enumMatches = Regex.Matches(code,
                @"enum\s+\w*\s*\{([^}]+)\}",
                RegexOptions.Singleline);
            foreach (Match m in enumMatches)
            {
                var vals = m.Groups[1].Value.Split(',');
                foreach (var v in vals)
                {
                    string val = v.Trim().Split('=')[0].Trim();
                    if (!string.IsNullOrEmpty(val))
                    {
                        _userEntries.Add(new CompletionEntry
                        {
                            Text = val,
                            Description = $"enum value: {val}",
                            Category = "constant"
                        });
                    }
                }
            }

            // Extract typedef aliases
            var typedefMatches = Regex.Matches(code, @"typedef\s+\w+\s+(\w+)\s*;");
            foreach (Match m in typedefMatches)
            {
                _userEntries.Add(new CompletionEntry
                {
                    Text = m.Groups[1].Value,
                    Description = $"typedef {m.Groups[1].Value}",
                    Category = "type"
                });
            }
        }

        #endregion

        #region Stdlib Database

        private void BuildStdlibEntries()
        {
            _stdlibEntries = new List<CompletionEntry>();

            // Helper to add entries concisely
            void F(string name, string sig, string cat = "function") =>
                _stdlibEntries.Add(new CompletionEntry { Text = name, Description = sig, Category = cat, InsertText = name + "(" });
            void C(string name, string desc) =>
                _stdlibEntries.Add(new CompletionEntry { Text = name, Description = desc, Category = "constant" });

            // ── Printf/Sprintf ──
            F("printf", "void printf(char* fmt, ...)  — Print formatted string. %d %x %s %c %p %b %u %%");
            F("sprintf", "int sprintf(char* buf, char* fmt, ...)  — Format into buffer, returns length");

            // ── VGA ──
            F("vga_clear", "void vga_clear()  — Clear screen");
            F("vga_setcolor", "void vga_setcolor(int fg, int bg)  — Set text color (0-15)");
            F("vga_putc", "void vga_putc(int ch)  — Print char at cursor");
            F("vga_putc_at", "void vga_putc_at(int x, int y, int ch)  — Char at position");
            F("vga_putc_at_attr", "void vga_putc_at_attr(int x, int y, int ch, int attr)  — Char with attribute byte");
            F("vga_puts", "void vga_puts(char* s)  — Print string");
            F("vga_println", "void vga_println(char* s)  — Print string + newline");
            F("vga_putint", "void vga_putint(int n)  — Print integer");
            F("vga_putuint", "void vga_putuint(unsigned n)  — Print unsigned");
            F("vga_puthex", "void vga_puthex(int n)  — Print hex (0x trimmed)");
            F("vga_puthex_full", "void vga_puthex_full(int n)  — Print full 8-digit hex");
            F("vga_putbin", "void vga_putbin(int n, int bits)  — Print binary");
            F("vga_printat", "void vga_printat(int x, int y, char* s)  — String at position");
            F("vga_printat_color", "void vga_printat_color(int x, int y, char* s, int fg, int bg)  — Colored string");
            F("vga_center", "void vga_center(int y, char* s)  — Center string on row");
            F("vga_setpos", "void vga_setpos(int x, int y)  — Move cursor");
            F("vga_newline", "void vga_newline()  — Print newline");
            F("vga_put", "void vga_put(int x, int y, int code)  — Put CP437 char");
            F("vga_put_color", "void vga_put_color(int x, int y, int code, int fg, int bg)");
            F("vga_int_at", "void vga_int_at(int x, int y, int n)  — Number at position");
            F("vga_scroll", "void vga_scroll()  — Scroll up one line");
            F("vga_fill", "void vga_fill(int x, int y, int w, int h, int ch)  — Fill rect");
            F("vga_hline", "void vga_hline(int x, int y, int len, int ch)  — Horizontal line");
            F("vga_vline", "void vga_vline(int x, int y, int len, int ch)  — Vertical line");
            F("vga_repeat", "void vga_repeat(int ch, int count)  — Repeat char at cursor");
            F("vga_peek", "int vga_peek(int x, int y)  — Read char from screen");
            F("vga_peek_attr", "int vga_peek_attr(int x, int y)  — Read attribute from screen");
            F("vga_getx", "int vga_getx()  — Get cursor X");
            F("vga_gety", "int vga_gety()  — Get cursor Y");
            F("cur_hide", "void cur_hide()  — Hide cursor");
            F("cur_show", "void cur_show()  — Show cursor");
            F("cur_move", "void cur_move(int x, int y)  — Move hardware cursor");

            // ── Drawing ──
            F("draw_box", "void draw_box(int x, int y, int w, int h)  — Single-line box");
            F("draw_dbox", "void draw_dbox(int x, int y, int w, int h)  — Double-line box");
            F("draw_fill", "void draw_fill(int x, int y, int w, int h, int ch)  — Fill region");
            F("draw_fill_color", "void draw_fill_color(int x, int y, int w, int h, int ch, int fg, int bg)");
            F("draw_shadow", "void draw_shadow(int x, int y, int w, int h)  — Drop shadow");
            F("draw_progress", "void draw_progress(int x, int y, int w, int val, int max)  — Progress bar");

            // ── Keyboard ──
            F("kb_init", "void kb_init()  — MUST CALL FIRST before any keyboard function");
            F("kb_getc", "char kb_getc()  — Blocking: wait for key, return ASCII (shift-aware)");
            F("kb_scan", "int kb_scan()  — Non-blocking: return scancode or 0");
            F("kb_haskey", "int kb_haskey()  — 1 if key waiting");
            F("kb_wait", "int kb_wait()  — Blocking: return raw scancode");
            F("kb_flush", "void kb_flush()  — Clear keyboard buffer");
            F("kb_getline", "void kb_getline(char* buf, int maxlen)  — Read line with echo + backspace");
            F("kb_scancode", "int kb_scancode()  — Blocking scancode read");
            F("input", "void input(char* prompt, char* buf, int maxlen)  — Prompted line input");

            // ── Strings ──
            F("str_len", "int str_len(char* s)");
            F("str_cmp", "int str_cmp(char* a, char* b)  — 0 if equal");
            F("str_eq", "int str_eq(char* a, char* b)  — 1 if equal");
            F("str_ncmp", "int str_ncmp(char* a, char* b, int n)");
            F("str_cpy", "void str_cpy(char* dst, char* src)");
            F("str_ncpy", "void str_ncpy(char* dst, char* src, int n)");
            F("str_cat", "void str_cat(char* dst, char* src)");
            F("str_chr", "char* str_chr(char* s, int ch)  — Find first char");
            F("str_rchr", "char* str_rchr(char* s, int ch)  — Find last char");
            F("str_str", "char* str_str(char* hay, char* needle)  — Find substring");
            F("str_starts", "int str_starts(char* s, char* prefix)  — Starts with prefix");
            F("str_ends", "int str_ends(char* s, char* suffix)  — Ends with suffix");
            F("str_rev", "void str_rev(char* s)  — Reverse in-place");
            F("str_trim", "void str_trim(char* s)  — Trim whitespace");
            F("str_upper", "void str_upper(char* s)  — To uppercase");
            F("str_lower", "void str_lower(char* s)  — To lowercase");
            F("str_count", "int str_count(char* s, int ch)  — Count occurrences");
            F("str_dup", "char* str_dup(char* s)  — Duplicate (heap allocated)");
            F("str_to_int", "int str_to_int(char* s)  — Parse integer");
            F("int_to_str", "void int_to_str(int n, char* buf)");
            F("hex_to_int", "int hex_to_int(char* s)  — Parse '0x1A2B'");
            F("int_to_hex", "void int_to_hex(int n, char* buf)  — Trimmed hex");
            F("int_to_hex_full", "void int_to_hex_full(int n, char* buf)  — Full 8-digit hex");

            // ── Memory ──
            F("mem_set", "void mem_set(char* ptr, int val, int count)");
            F("mem_cpy", "void mem_cpy(char* dst, char* src, int count)");
            F("mem_cmp", "int mem_cmp(char* a, char* b, int count)");
            F("mem_zero", "void mem_zero(char* ptr, int count)");
            F("os_malloc", "char* os_malloc(int size)  — Heap alloc with free-list");
            F("os_free", "void os_free(char* ptr)  — Free heap block");
            F("os_heap_init", "void os_heap_init()  — Initialize heap (auto-called)");
            F("os_heap_compact", "void os_heap_compact()  — Coalesce free blocks");
            F("os_heap_used", "int os_heap_used()  — Bytes in use");
            F("os_heap_free", "int os_heap_free()  — Bytes free");
            F("os_block_count", "int os_block_count()  — Number of heap blocks");
            F("alloc", "char* alloc(int size)  — Bump allocator");
            F("alloc_reset", "void alloc_reset()  — Reset bump allocator");
            F("zalloc", "char* zalloc(int size)  — Alloc + zero-fill");

            // ── Port I/O ──
            F("inb", "int inb(int port)  — Read byte from I/O port");
            F("outb", "void outb(int port, int val)  — Write byte to I/O port");
            F("inw", "int inw(int port)  — Read word from I/O port");
            F("outw", "void outw(int port, int val)  — Write word");
            F("inl", "int inl(int port)  — Read dword");
            F("outl", "void outl(int port, int val)  — Write dword");

            // ── Timing ──
            F("delay", "void delay(int ms)  — Busy-wait milliseconds");
            F("delay_pit", "void delay_pit(int ms)  — PIT-calibrated delay");
            F("sleep", "void sleep(int seconds)  — Sleep seconds");
            F("util_delay", "void util_delay(int count)  — Raw loop delay");

            // ── Random ──
            F("srand", "void srand(int seed)  — Seed RNG");
            F("rand", "int rand()  — Random 0-32767");
            F("randint", "int randint(int min, int max)  — Random in range");
            F("rng_srand", "void rng_srand(int seed)");
            F("rng_rand", "int rng_rand()");

            // ── Sound ──
            F("play_tone", "void play_tone(int freq, int ms)  — PC speaker tone");
            F("speaker_on", "void speaker_on()");
            F("speaker_off", "void speaker_off()");
            F("snd_click", "void snd_click()  — Short click");
            F("snd_drop", "void snd_drop()  — Drop sound");
            F("snd_clear", "void snd_clear()  — Clear/complete sound");
            F("snd_levelup", "void snd_levelup()  — Level-up jingle");
            F("snd_gameover", "void snd_gameover()  — Game over sound");
            F("snd_beep", "void snd_beep()  — Standard beep");
            F("snd_error", "void snd_error()  — Error sound");
            F("snd_success", "void snd_success()  — Success sound");

            // ── Utility ──
            F("util_abs", "int util_abs(int n)");
            F("util_min", "int util_min(int a, int b)");
            F("util_max", "int util_max(int a, int b)");
            F("util_clamp", "int util_clamp(int n, int lo, int hi)");
            F("util_swap", "void util_swap(int* a, int* b)");
            F("sort", "void sort(int* arr, int count)  — Insertion sort");

            // ── Char ──
            F("is_digit", "int is_digit(int c)");
            F("is_alpha", "int is_alpha(int c)");
            F("is_alnum", "int is_alnum(int c)");
            F("is_space", "int is_space(int c)");
            F("is_upper", "int is_upper(int c)");
            F("is_lower", "int is_lower(int c)");
            F("is_print", "int is_print(int c)");
            F("is_hex", "int is_hex(int c)");
            F("to_upper", "int to_upper(int c)");
            F("to_lower", "int to_lower(int c)");

            // ── Debug ──
            F("panic", "void panic(char* msg)  — Red screen + halt");
            F("assert", "void assert(int cond, char* msg)  — Panic if false");
            F("hex_dump", "void hex_dump(int addr, int count)  — Formatted hex dump");

            // ── System (runtime intrinsics) ──
            F("disable_interrupts", "void disable_interrupts()  — cli");
            F("enable_interrupts", "void enable_interrupts()  — sti");
            F("halt", "void halt()  — hlt");
            F("read_cr0", "int read_cr0()");
            F("write_cr0", "void write_cr0(int val)");
            F("read_cr3", "int read_cr3()");
            F("write_cr3", "void write_cr3(int val)");

            // ── Colors ──
            C("COLOR_BLACK", "0"); C("COLOR_BLUE", "1");
            C("COLOR_GREEN", "2"); C("COLOR_CYAN", "3");
            C("COLOR_RED", "4"); C("COLOR_MAGENTA", "5");
            C("COLOR_BROWN", "6"); C("COLOR_LGRAY", "7");
            C("COLOR_DGRAY", "8"); C("COLOR_LBLUE", "9");
            C("COLOR_LGREEN", "10"); C("COLOR_LCYAN", "11");
            C("COLOR_LRED", "12"); C("COLOR_LMAGENTA", "13");
            C("COLOR_YELLOW", "14"); C("COLOR_WHITE", "15");

            // ── Box drawing ──
            C("BOX_H", "196 (─)"); C("BOX_V", "179 (│)");
            C("BOX_TL", "218 (┌)"); C("BOX_TR", "191 (┐)");
            C("BOX_BL", "192 (└)"); C("BOX_BR", "217 (┘)");
            C("DBOX_H", "205 (═)"); C("DBOX_V", "186 (║)");
            C("DBOX_TL", "201 (╔)"); C("DBOX_TR", "187 (╗)");
            C("DBOX_BL", "200 (╚)"); C("DBOX_BR", "188 (╝)");
            C("CH_FULL", "219 (█)");
            C("CH_SHADE1", "176 (░)");
            C("CH_SHADE2", "177 (▒)");
            C("CH_SHADE3", "178 (▓)");

            // ── Key scancodes ──
            C("KEY_ESC", "1"); C("KEY_ENTER", "28"); C("KEY_SPACE", "57"); C("KEY_BKSP", "14");
            C("KEY_UP", "72"); C("KEY_DOWN", "80"); C("KEY_LEFT", "75"); C("KEY_RIGHT", "77");
            C("KEY_W", "17"); C("KEY_A", "30"); C("KEY_S", "31"); C("KEY_D", "32");
            C("KEY_Q", "16"); C("KEY_E", "18"); C("KEY_F", "33");
            C("KEY_1", "2"); C("KEY_2", "3"); C("KEY_3", "4");
            C("KEY_F1", "59"); C("KEY_F2", "60"); C("KEY_F3", "61");

            // ── VGA constants ──
            C("VGA_WIDTH", "80");
            C("VGA_HEIGHT", "25");
            C("VGA_MEMORY", "0xB8000");

            // ── Music notes ──
            C("C4", "262"); C("D4", "294"); C("E4", "330"); C("F4", "349");
            C("G4", "392"); C("A4", "440"); C("B4", "494"); C("C5", "523");
        }

        private void BuildKeywordEntries()
        {
            _keywordEntries = new List<CompletionEntry>();
            void K(string kw, string desc = "", string insert = null) =>
                _keywordEntries.Add(new CompletionEntry { Text = kw, Description = desc, Category = "keyword", InsertText = insert });

            K("if", "if (condition) { }");
            K("else", "else { }");
            K("while", "while (condition) { }");
            K("for", "for (init; cond; incr) { }");
            K("do", "do { } while (condition);");
            K("switch", "switch (expr) { case N: break; default: break; }");
            K("case", "case value:");
            K("default", "default:");
            K("break", "break;");
            K("continue", "continue;");
            K("return", "return value;");
            K("struct", "struct Name { members };", "struct\n{\n\t\n};");
            K("typedef", "typedef old_name new_name;");
            K("enum", "enum Name { A, B, C };");
            K("int", "int (32-bit signed integer)");
            K("char", "char (8-bit byte)");
            K("void", "void (no type)");
            K("unsigned", "unsigned int");
            K("static", "static — file scope only");
            K("const", "const — read-only");
            K("volatile", "volatile — prevent optimization");
            K("sizeof", "sizeof(type) — size in bytes");
            K("NULL", "0 — null pointer");
            K("asm", "asm(\"instruction\"); — inline assembly");
        }

        #endregion
    }

    #region Completion Data Types

    public class CompletionEntry
    {
        public string Text { get; set; }
        public string Description { get; set; }
        public string Category { get; set; } // function, variable, constant, keyword, type, member
        public string InsertText { get; set; } // null = use Text
    }

    public class CompletionEntryComparer : IEqualityComparer<CompletionEntry>
    {
        public bool Equals(CompletionEntry a, CompletionEntry b) => a.Text == b.Text;
        public int GetHashCode(CompletionEntry obj) => obj.Text.GetHashCode();
    }

    /// <summary>
    /// Highlights user-defined struct/typedef names in the editor with a distinct color.
    /// Install on CodeEditor.TextArea.TextView.LineTransformers.
    /// </summary>
    public class StructTypeHighlighter : ICSharpCode.AvalonEdit.Rendering.DocumentColorizingTransformer
    {
        private SubsetCCompletion _provider;
        private SolidColorBrush _typeBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4E, 0xC9, 0xB0)); // Teal/green like VS

        public StructTypeHighlighter(SubsetCCompletion provider)
        {
            _provider = provider;
            _typeBrush.Freeze();
        }

        protected override void ColorizeLine(ICSharpCode.AvalonEdit.Document.DocumentLine line)
        {
            if (_provider == null) return;
            var names = _provider.StructTypeNames;
            if (names == null || names.Count == 0) return;

            string lineText = CurrentContext.Document.GetText(line);
            int lineStart = line.Offset;

            foreach (string typeName in names)
            {
                int idx = 0;
                while (idx < lineText.Length)
                {
                    int pos = lineText.IndexOf(typeName, idx, StringComparison.Ordinal);
                    if (pos < 0) break;

                    // Check word boundaries
                    bool startOk = (pos == 0) || !char.IsLetterOrDigit(lineText[pos - 1]) && lineText[pos - 1] != '_';
                    int end = pos + typeName.Length;
                    bool endOk = (end >= lineText.Length) || !char.IsLetterOrDigit(lineText[end]) && lineText[end] != '_';

                    if (startOk && endOk)
                    {
                        base.ChangeLinePart(lineStart + pos, lineStart + end, element =>
                        {
                            element.TextRunProperties.SetForegroundBrush(_typeBrush);
                        });
                    }
                    idx = pos + 1;
                }
            }
        }
    }

    public class SubsetCCompletionData : ICompletionData
    {
        private CompletionEntry _entry;

        public SubsetCCompletionData(CompletionEntry entry)
        {
            _entry = entry;
        }

        public System.Windows.Media.ImageSource Image => null;

        public string Text => _entry.Text;

        public object Content
        {
            get
            {
                var panel = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };

                // Category icon with color
                string icon; System.Windows.Media.Color iconColor;
                switch (_entry.Category)
                {
                    case "function":
                        icon = "ƒ"; iconColor = System.Windows.Media.Color.FromRgb(0xDC, 0xDC, 0xAA); break; // Yellow
                    case "variable":
                        icon = "⊞"; iconColor = System.Windows.Media.Color.FromRgb(0x9C, 0xDC, 0xFE); break; // Light blue
                    case "constant":
                        icon = "#"; iconColor = System.Windows.Media.Color.FromRgb(0xB5, 0xCE, 0xA8); break; // Green
                    case "keyword":
                        icon = "⚷"; iconColor = System.Windows.Media.Color.FromRgb(0xC5, 0x86, 0xC0); break; // Purple
                    case "type":
                        icon = "T"; iconColor = System.Windows.Media.Color.FromRgb(0x4E, 0xC9, 0xB0); break; // Teal
                    case "member":
                        icon = "·"; iconColor = System.Windows.Media.Color.FromRgb(0x9C, 0xDC, 0xFE); break; // Light blue
                    default:
                        icon = " "; iconColor = System.Windows.Media.Color.FromRgb(0xCC, 0xCC, 0xCC); break;
                }

                panel.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = icon + " ",
                    Foreground = new SolidColorBrush(iconColor),
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize = 12,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center
                });

                panel.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = _entry.Text,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xCC, 0xCC)),
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize = 12,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center
                });

                return panel;
            }
        }

        public object Description
        {
            get
            {
                if (string.IsNullOrEmpty(_entry.Description)) return null;
                // Wrap in a Border to control the tooltip appearance — no white borders
                return new System.Windows.Controls.Border
                {
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x1E, 0x1E)),
                    BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3F, 0x3F, 0x46)),
                    BorderThickness = new System.Windows.Thickness(1),
                    CornerRadius = new System.Windows.CornerRadius(2),
                    Padding = new System.Windows.Thickness(8, 4, 8, 4),
                    Margin = new System.Windows.Thickness(-4, -2, -4, -2), // Bleed over any parent padding
                    Child = new System.Windows.Controls.TextBlock
                    {
                        Text = _entry.Description,
                        Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xCC, 0xCC)),
                        FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                        FontSize = 11.5,
                        TextWrapping = System.Windows.TextWrapping.Wrap,
                        MaxWidth = 500
                    }
                };
            }
        }

        public double Priority
        {
            get
            {
                return _entry.Category switch
                {
                    "function" => 1.0,
                    "keyword" => 0.9,
                    "variable" => 0.8,
                    "constant" => 0.7,
                    "type" => 0.6,
                    "member" => 1.0,
                    _ => 0.5
                };
            }
        }

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            string insert = _entry.InsertText ?? _entry.Text;
            textArea.Document.Replace(completionSegment, insert);

            // For struct auto-insert, position caret inside the braces
            if (insert.Contains("{\n\t\n}"))
            {
                // Find the tab between the braces — that's where the caret goes
                int insertStart = completionSegment.Offset;
                string docText = textArea.Document.Text;
                int tabPos = docText.IndexOf('\t', insertStart);
                if (tabPos > 0 && tabPos < docText.Length)
                    textArea.Caret.Offset = tabPos + 1;
            }
        }
    }

    #endregion
}