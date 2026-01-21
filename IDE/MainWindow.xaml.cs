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
    mov dx, 0x3F8
    mov al, 'B'
    out dx, al

    xor ax, ax
    mov ds, ax
    mov es, ax
    mov ss, ax
    mov sp, 0x7BFE

    mov al, 'S'
    out dx, al
    mov al, 'R'
    out dx, al

    mov ah, 0x00
    mov dl, 0x80
    int 0x13
    jc reset_fail
    mov al, 'Z'
    out dx, al
    jmp read_sector

reset_fail:
    mov al, 'X'
    out dx, al
    jmp hang

read_sector:
    mov ah, 0x02
    mov al, 8
    mov ch, 0
    mov cl, 2
    mov dh, 0
    mov dl, 0x80
    mov bx, 0x1000
    int 0x13
    jc read_fail
    mov al, 'A'
    out dx, al
    jmp success

read_fail:
    mov al, 'E'
    out dx, al
    jmp hang

success:
    mov al, 'L'
    out dx, al
    jmp mode_switch

mode_switch:
    mov ax, 0x3
    int 0x10

    cli
    lgdt [gdt_desc]
    mov eax, cr0
    or eax, 1
    mov cr0, eax
    jmp 0x08:protected_mode

hang:
    mov al, 'H'
    out dx, al
    jmp $

[BITS 32]
protected_mode:
    mov ax, 0x10
    mov ds, ax
    mov es, ax
    mov ss, ax
    mov esp, 0x90000
    mov dx, 0x3F8
    mov al, 'K'
    out dx, al
    jmp 0x1000

gdt_start:
    dq 0
gdt_code:
    dw 0xFFFF
    dw 0
    db 0
    db 10011010b
    db 11001111b
    db 0
gdt_data:
    dw 0xFFFF
    dw 0
    db 0
    db 10010010b
    db 11111111b
    db 0
gdt_end:
gdt_desc:
    dw gdt_end - gdt_start - 1
    dd gdt_start

times 510 - ($ - $$) db 0
dw 0xAA55";

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
                if (lower.Contains("error") || lower.Contains("failed") || lower.Contains("✗") || lower.Contains("[!]"))
                {
                    run.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F44747")); // Red
                }
                else if (lower.Contains("warning") || lower.Contains("⚠"))
                {
                    run.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCA700")); // Yellow
                }
                else if (lower.Contains("success") || lower.Contains("✓") || lower.Contains("[+]") || lower.Contains("complete"))
                {
                    run.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4EC9B0")); // Green
                }
                else if (line.StartsWith("╔") || line.StartsWith("║") || line.StartsWith("╚"))
                {
                    run.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#569CD6")); // Blue for boxes
                }
                else if (lower.Contains("[*]") || lower.Contains("[cc]") || lower.Contains("[asm]") || lower.Contains("linking"))
                {
                    run.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9CDCFE")); // Light blue for info
                }
                else
                {
                    run.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCCCCC")); // Gray default
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

            // Add zoom functionality with Ctrl+Scroll
            CodeEditor.PreviewMouseWheel += CodeEditor_MouseWheel;

            // Add Find/Replace with Ctrl+F and Ctrl+H
            CodeEditor.PreviewKeyDown += CodeEditor_KeyDown;

            // Update status bar with line/column position
            CodeEditor.TextArea.Caret.PositionChanged += (s, e) => UpdateLineColumnStatus();

            // Add code folding with chevrons
            InitializeCodeFolding();
        }

        private ICSharpCode.AvalonEdit.Folding.FoldingManager foldingManager;
        private System.Windows.Threading.DispatcherTimer foldingUpdateTimer;

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
                Height = 400,
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

            // Memory Viewer (placeholder)
            var memViewBtn = new Button
            {
                Content = "💾 Memory Viewer",
                Height = 45,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3E3E42")),
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#858585")),
                BorderThickness = new Thickness(0, 0, 0, 0),
                FontSize = 14,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 0, 10),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(15, 0, 0, 0)
            };
            memViewBtn.Click += (s, args) => { ShowDarkMessageBox("Memory Viewer - Coming Soon!", "OS Dev IDE"); };

            // Bootloader Generator
            var bootGenBtn = new Button
            {
                Content = "🚀 Bootloader Generator",
                Height = 45,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3E3E42")),
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#858585")),
                BorderThickness = new Thickness(0, 0, 0, 0),
                FontSize = 14,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 0, 10),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(15, 0, 0, 0)
            };
            bootGenBtn.Click += (s, args) => { ShowDarkMessageBox("Bootloader Generator - Coming Soon!", "OS Dev IDE"); };

            var ollamaInfo = new TextBlock
            {
                Text = "🤖 Ollama AI integration coming soon!",
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#858585")),
                FontSize = 11,
                FontStyle = FontStyles.Italic,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 20, 0, 0)
            };

            stack.Children.Add(memViewBtn);
            stack.Children.Add(bootGenBtn);
            stack.Children.Add(ollamaInfo);
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
            closeBtn.Click += (s, e) => dialog.Close();
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
    <Color name=""Function"" foreground=""#DCDCAA"" />
    <Color name=""Operator"" foreground=""#D4D4D4"" />
    <Color name=""Punctuation"" foreground=""#D4D4D4"" />
    
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
        <Span color=""Preprocessor"" begin=""^\s*#"" end=""$"" />
        
        <!-- Control flow keywords (purple in VS) -->
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
        
        <!-- Storage and modifier keywords (blue in VS) -->
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
        
        <!-- Type keywords (teal in VS) -->
        <Keywords color=""Type"">
            <Word>void</Word>
            <Word>char</Word>
            <Word>short</Word>
            <Word>int</Word>
            <Word>long</Word>
            <Word>float</Word>
            <Word>double</Word>
            <Word>signed</Word>
            <Word>unsigned</Word>
            <Word>struct</Word>
            <Word>union</Word>
            <Word>enum</Word>
        </Keywords>
        
        <!-- Numbers (hex and decimal) -->
        <Rule color=""Number"">
            \b0[xX][0-9a-fA-F]+\b|\b\d+\b
        </Rule>
        
        <!-- Function calls (yellow in VS) -->
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

            File.WriteAllText(Path.Combine(projectPath, "Kernel", "kernel.c"),
                @"// kernel.c - OS Dev IDE Template
// Entry point must be kernel_main()

void kernel_main() {
    print_fmt(""Hello World!"");
    while (1) {}
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

                File.WriteAllText(newFile, "");
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

                    // Disassemble to assembly view
                    var asmBuilder = new System.Text.StringBuilder();
                    asmBuilder.AppendLine("╔═══════════════════════════════════════════════════════════════╗");
                    asmBuilder.AppendLine("║                    ASSEMBLY VIEW                              ║");
                    asmBuilder.AppendLine("╚═══════════════════════════════════════════════════════════════╝");
                    asmBuilder.AppendLine();

                    int pos = offset;
                    var instructions = new List<DisassembledInstruction>();

                    while (pos < fileBytes.Length && pos < offset + 512)
                    {
                        var instr = DisassembleInstructionDetailed(fileBytes, ref pos);
                        instructions.Add(instr);
                        asmBuilder.AppendLine(instr.FormattedLine);
                    }

                    asmText.Text = asmBuilder.ToString();

                    // Generate pseudo-code
                    var pseudoBuilder = new System.Text.StringBuilder();
                    pseudoBuilder.AppendLine("╔═══════════════════════════════════════════════════════════════╗");
                    pseudoBuilder.AppendLine("║                   PSEUDO-CODE VIEW                            ║");
                    pseudoBuilder.AppendLine("╚═══════════════════════════════════════════════════════════════╝");
                    pseudoBuilder.AppendLine();
                    pseudoBuilder.AppendLine("void function_" + offset.ToString("X") + "() {");
                    pseudoBuilder.AppendLine();

                    GenerateImprovedPseudoCode(instructions, pseudoBuilder);

                    pseudoBuilder.AppendLine("}");

                    pseudoText.Text = pseudoBuilder.ToString();
                }
                catch (Exception ex)
                {
                    asmText.Text = $"Error disassembling: {ex.Message}";
                    pseudoText.Text = "// Unable to generate pseudo-code";
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

        private class DisassembledInstruction
        {
            public int Address { get; set; }
            public string BytesHex { get; set; }
            public string Mnemonic { get; set; }
            public string Operands { get; set; }
            public string FormattedLine { get; set; }
        }

        private DisassembledInstruction DisassembleInstructionDetailed(byte[] bytes, ref int pos)
        {
            int startPos = pos;
            if (pos >= bytes.Length)
                return new DisassembledInstruction { Address = startPos, FormattedLine = "" };

            byte opcode = bytes[pos++];
            string bytesStr = $"{opcode:X2}";
            string mnemonic = "";
            string operands = "";

            // x86 disassembly with color coding
            switch (opcode)
            {
                case 0x90: mnemonic = "nop"; break;
                case 0xC3: mnemonic = "ret"; break;
                case 0xCC: mnemonic = "int3"; break;
                case 0xF4: mnemonic = "hlt"; break;
                case 0xFA: mnemonic = "cli"; break;
                case 0xFB: mnemonic = "sti"; break;
                case 0x50:
                case 0x51:
                case 0x52:
                case 0x53:
                case 0x54:
                case 0x55:
                case 0x56:
                case 0x57:
                    mnemonic = "push";
                    operands = new[] { "ax", "cx", "dx", "bx", "sp", "bp", "si", "di" }[opcode - 0x50];
                    break;
                case 0x58:
                case 0x59:
                case 0x5A:
                case 0x5B:
                case 0x5C:
                case 0x5D:
                case 0x5E:
                case 0x5F:
                    mnemonic = "pop";
                    operands = new[] { "ax", "cx", "dx", "bx", "sp", "bp", "si", "di" }[opcode - 0x58];
                    break;

                case 0xB0:
                case 0xB1:
                case 0xB2:
                case 0xB3:
                case 0xB4:
                case 0xB5:
                case 0xB6:
                case 0xB7:
                    if (pos < bytes.Length)
                    {
                        byte val = bytes[pos++];
                        bytesStr += $" {val:X2}";
                        mnemonic = "mov";
                        operands = $"{(char)('a' + (opcode - 0xB0))}l, 0x{val:X2}";
                    }
                    break;

                case 0xB8:
                case 0xB9:
                case 0xBA:
                case 0xBB:
                case 0xBC:
                case 0xBD:
                case 0xBE:
                case 0xBF:
                    if (pos + 1 < bytes.Length)
                    {
                        ushort val = (ushort)(bytes[pos] | (bytes[pos + 1] << 8));
                        bytesStr += $" {bytes[pos]:X2} {bytes[pos + 1]:X2}";
                        pos += 2;
                        mnemonic = "mov";
                        string[] regs = { "ax", "cx", "dx", "bx", "sp", "bp", "si", "di" };
                        operands = $"{regs[opcode - 0xB8]}, 0x{val:X4}";
                    }
                    break;

                case 0xE8:
                    if (pos + 1 < bytes.Length)
                    {
                        short offset = (short)(bytes[pos] | (bytes[pos + 1] << 8));
                        bytesStr += $" {bytes[pos]:X2} {bytes[pos + 1]:X2}";
                        pos += 2;
                        mnemonic = "call";
                        operands = $"0x{startPos + 3 + offset:X}";
                    }
                    break;

                case 0xE9:
                    if (pos + 1 < bytes.Length)
                    {
                        short offset = (short)(bytes[pos] | (bytes[pos + 1] << 8));
                        bytesStr += $" {bytes[pos]:X2} {bytes[pos + 1]:X2}";
                        pos += 2;
                        mnemonic = "jmp";
                        operands = $"0x{startPos + 3 + offset:X}";
                    }
                    break;

                case 0x74: // jz
                case 0x75: // jnz
                case 0x7C: // jl
                case 0x7D: // jge
                case 0x7E: // jle
                case 0x7F: // jg
                    if (pos < bytes.Length)
                    {
                        sbyte offset = (sbyte)bytes[pos++];
                        bytesStr += $" {(byte)offset:X2}";
                        mnemonic = opcode == 0x74 ? "jz" : opcode == 0x75 ? "jnz" :
                                   opcode == 0x7C ? "jl" : opcode == 0x7D ? "jge" :
                                   opcode == 0x7E ? "jle" : "jg";
                        operands = $"0x{startPos + 2 + offset:X}";
                    }
                    break;

                case 0xCD:
                    if (pos < bytes.Length)
                    {
                        byte intNum = bytes[pos++];
                        bytesStr += $" {intNum:X2}";
                        mnemonic = "int";
                        operands = $"0x{intNum:X2}";
                    }
                    break;

                default:
                    // Skip unrecognized bytes
                    return new DisassembledInstruction { Address = startPos, FormattedLine = "" };
            }

            string formatted = $"0x{startPos:X8}  {bytesStr,-18} {mnemonic,-8} {operands}";

            return new DisassembledInstruction
            {
                Address = startPos,
                BytesHex = bytesStr,
                Mnemonic = mnemonic,
                Operands = operands,
                FormattedLine = formatted
            };
        }

        private void GenerateImprovedPseudoCode(List<DisassembledInstruction> instructions, System.Text.StringBuilder sb)
        {
            int indent = 1;
            string ind() => new string(' ', indent * 4);

            foreach (var instr in instructions)
            {
                switch (instr.Mnemonic)
                {
                    case "mov":
                        sb.AppendLine($"{ind()}{instr.Operands.Replace(", ", " = ")};");
                        break;

                    case "push":
                    case "pop":
                    case "nop":
                        // Skip completely
                        break;

                    case "call":
                        sb.AppendLine($"{ind()}function_{instr.Operands.Replace("0x", "")}();");
                        break;

                    case "jmp":
                        sb.AppendLine($"{ind()}goto loc_{instr.Operands.Replace("0x", "")};");
                        break;

                    case "jz":
                    case "jnz":
                    case "jl":
                    case "jle":
                    case "jg":
                    case "jge":
                        sb.AppendLine($"{ind()}if (condition_{instr.Mnemonic}) goto loc_{instr.Operands.Replace("0x", "")};");
                        break;

                    case "ret":
                        sb.AppendLine($"{ind()}return;");
                        break;

                    case "int":
                        sb.AppendLine($"{ind()}interrupt({instr.Operands});");
                        break;

                    case "cli":
                        sb.AppendLine($"{ind()}disable_interrupts();");
                        break;

                    case "sti":
                        sb.AppendLine($"{ind()}enable_interrupts();");
                        break;

                    case "hlt":
                        sb.AppendLine($"{ind()}halt();");
                        break;

                    default:
                        sb.AppendLine($"{ind()}// {instr.Mnemonic} {instr.Operands}");
                        break;
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
                        icon = "FileCode";
                        color = "#FF9800";
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
                                    File.WriteAllText(newFile, "");
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

            var icon = new PackIcon
            {
                Width = 16,
                Height = 16,
                Margin = new Thickness(0, 0, 6, 0),
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color))
            };

            var kindType = typeof(PackIconKind);
            icon.Kind = (PackIconKind)Enum.Parse(kindType, iconKind);

            var textBlock = new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (bold)
                textBlock.FontWeight = FontWeights.SemiBold;

            panel.Children.Add(icon);
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
            AppendOutput("╔═══════════════════════════════════════╗\n");
            AppendOutput("║         BUILD STARTED                 ║\n");
            AppendOutput("╚═══════════════════════════════════════╝\n\n");

            string kernelDir = Path.Combine(projectPath, "Kernel");
            string bootDir = Path.Combine(projectPath, "Bootloader");

            string compilerExe = "Compiler-x86_32.exe";
            string compilerPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, compilerExe);

            if (!File.Exists(compilerPath))
            {
                AppendOutput($"✗ ERROR: Compiler not found at {compilerPath}\n");
                StatusText.Text = "Build failed";
                return;
            }

            string kernelAsm = Path.Combine(kernelDir, "kernel.asm");
            AppendOutput($"[1/4] Compiling kernel.c to assembly...\n");

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

            if (!string.IsNullOrWhiteSpace(compOut)) AppendOutput(compOut + "\n");
            if (!string.IsNullOrWhiteSpace(compErr)) AppendOutput(compErr + "\n");

            if (compilerProc.ExitCode != 0 || !File.Exists(kernelAsm))
            {
                AppendOutput("\n✗ COMPILATION FAILED\n");
                StatusText.Text = "Build failed";
                return;
            }

            AppendOutput("✓ kernel.asm generated\n\n");

            AppendOutput("[2/4] Assembling and creating image...\n");
            StatusText.Text = "Creating image...";

            string driveLetter = projectPath[0].ToString().ToLower();
            string wslPath = "/mnt/" + driveLetter + projectPath.Substring(2).Replace("\\", "/");

            string wslScript = $"cd '{wslPath}' && ";

            if (File.Exists(Path.Combine(bootDir, "bootloader.asm")))
                wslScript += $"cd Bootloader && nasm -f bin bootloader.asm -o bootloader.bin && cd .. && ";
            else
                AppendOutput("⚠ bootloader.asm missing, skipping bootloader\n");

            wslScript += $"cd Kernel && nasm -f bin kernel.asm -o kernel.bin && cd .. && ";
            wslScript += "cp Bootloader/bootloader.bin os-image.bin 2>/dev/null || true && ";
            wslScript += "dd if=Kernel/kernel.bin of=os-image.bin bs=512 seek=1 conv=notrunc 2>/dev/null || true && ";
            wslScript += "dd if=/dev/zero bs=1 count=0 seek=1474560 of=os-image.bin 2>/dev/null";

            if (runQemu)
                wslScript += " && qemu-system-i386 -drive file=os-image.bin,format=raw -serial stdio -no-reboot";

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
                if (a.Data != null)
                    Dispatcher.Invoke(() => AppendOutput(a.Data + "\n"));
            };
            wslProc.ErrorDataReceived += (s, a) =>
            {
                if (a.Data != null)
                    Dispatcher.Invoke(() => AppendOutput(a.Data + "\n"));
            };

            wslProc.Start();
            wslProc.BeginOutputReadLine();
            wslProc.BeginErrorReadLine();
            await wslProc.WaitForExitAsync();

            if (wslProc.ExitCode == 0)
            {
                AppendOutput("\n╔═══════════════════════════════════════╗\n");
                AppendOutput("║       ✓ BUILD SUCCESSFUL              ║\n");
                AppendOutput("╚═══════════════════════════════════════╝\n");
                StatusText.Text = runQemu ? "Running in QEMU" : "Build successful";
            }
            else
            {
                AppendOutput("\n✗ BUILD FAILED\n");
                StatusText.Text = "Build failed";
            }
        }

        #region Terminal Feature

        private void OutputTab_Checked(object sender, RoutedEventArgs e)
        {
            if (OutputScroller != null && TerminalPanel != null)
            {
                OutputScroller.Visibility = Visibility.Visible;
                TerminalPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void TerminalTab_Checked(object sender, RoutedEventArgs e)
        {
            if (OutputScroller != null && TerminalPanel != null)
            {
                OutputScroller.Visibility = Visibility.Collapsed;
                TerminalPanel.Visibility = Visibility.Visible;
                if (TerminalOutput.Text == "") TerminalOutput.Text = "PowerShell Terminal Ready\nType a command and press Enter...\n\n";
                TerminalInput.Focus();
            }
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
                // Go backwards to find the function name or block type
                int searchStart = Math.Max(0, braceOffset - 200);
                string text = document.GetText(searchStart, braceOffset - searchStart);

                // Look for function pattern: type name(...)
                var match = System.Text.RegularExpressions.Regex.Match(text, @"(\w+)\s*\([^)]*\)\s*$");
                if (match.Success)
                {
                    return match.Groups[1].Value + "()";
                }

                // Look for control structures
                if (text.Contains("if"))
                    return "if";
                if (text.Contains("else"))
                    return "else";
                if (text.Contains("while"))
                    return "while";
                if (text.Contains("for"))
                    return "for";
                if (text.Contains("struct"))
                    return "struct";

                return "...";
            }
            catch
            {
                return "...";
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

            // Track which lines we've drawn to prevent duplicates
            var drawnLines = new System.Collections.Generic.HashSet<int>();

            foreach (var folding in manager.AllFoldings)
            {
                var startLine = textView.Document.GetLineByOffset(folding.StartOffset);

                // Skip if we already drew a chevron for this line
                if (drawnLines.Contains(startLine.LineNumber)) continue;
                drawnLines.Add(startLine.LineNumber);

                var visualLine = textView.GetVisualLine(startLine.LineNumber);
                if (visualLine == null) continue;

                double lineTop = visualLine.VisualTop - textView.ScrollOffset.Y;
                double lineHeight = visualLine.Height;

                // Skip if not visible
                if (lineTop + lineHeight < 0 || lineTop > this.ActualHeight) continue;

                double centerY = lineTop + (lineHeight / 2);
                double centerX = MARGIN_WIDTH / 2;

                var pen = new System.Windows.Media.Pen(
                    new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(180, 180, 180)), 1.5);
                pen.Freeze();

                // Draw ONLY ONE chevron based on state
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

            foreach (var folding in manager.AllFoldings)
            {
                var startLine = textView.Document.GetLineByOffset(folding.StartOffset);
                var visualLine = textView.GetVisualLine(startLine.LineNumber);
                if (visualLine == null) continue;

                double lineTop = visualLine.VisualTop - textView.ScrollOffset.Y;
                double lineHeight = visualLine.Height;

                // Hitbox = full line height, full margin width
                if (pos.Y >= lineTop && pos.Y <= lineTop + lineHeight &&
                    pos.X >= 0 && pos.X <= MARGIN_WIDTH)
                {
                    folding.IsFolded = !folding.IsFolded;
                    textView.Redraw();
                    this.InvalidateVisual();
                    e.Handled = true;
                    return; // Exit after first match
                }
            }
        }
    }
}