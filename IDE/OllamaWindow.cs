using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace OSDevIDE
{
    /// <summary>
    /// Ollama AI Chat Window - Large, floating, professional
    /// Open from Extensions menu or AI quick panel
    /// </summary>
    public class OllamaWindow : Window
    {
        private OllamaService _ai;
        private CancellationTokenSource _aiCts;
        private bool _isGenerating = false;
        private StringBuilder _response = new StringBuilder();

        // Callbacks to main window
        private Func<string> _getEditorCode;
        private Func<string> _getEditorFile;
        private Func<string> _getBuildOutput;
        private Action<string, string> _writeFile;
        private Action<string> _setEditorCode;

        // UI Elements
        private ComboBox _modelCombo;
        private TextBox _chatOutput;
        private TextBox _chatInput;
        private ScrollViewer _chatScroller;
        private Button _sendBtn;
        private Button _stopBtn;
        private CheckBox _autoWriteCheck;

        // Performance optimization
        private StringBuilder _tokenBuffer = new StringBuilder();
        private System.Windows.Threading.DispatcherTimer _updateTimer;

        public OllamaWindow(
            Func<string> getEditorCode = null,
            Func<string> getEditorFile = null,
            Func<string> getBuildOutput = null,
            Action<string, string> writeFile = null,
            Action<string> setEditorCode = null)
        {
            _getEditorCode = getEditorCode;
            _getEditorFile = getEditorFile;
            _getBuildOutput = getBuildOutput;
            _writeFile = writeFile;
            _setEditorCode = setEditorCode;

            InitializeWindow();

            // Show loading message immediately
            _chatOutput.Text = "⏳ Initializing AI...\n\nChecking for Ollama installation...";

            // Initialize AI on background thread to not block UI
            Task.Run(async () => await InitializeAI());
        }

        private void InitializeWindow()
        {
            // Large, professional window
            Title = "Ollama AI - SubsetC Expert";
            Width = 700;
            Height = 800;
            MinWidth = 500;
            MinHeight = 500;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color(0x1E, 0x1E, 0x1E));

            var mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(50) });   // Header
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });       // Quick actions
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Chat
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });       // Input

            // === HEADER ===
            var header = new Border
            {
                Background = new SolidColorBrush(Color(0x2D, 0x2D, 0x30)),
                BorderBrush = new SolidColorBrush(Color(0x3F, 0x3F, 0x46)),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };

            var headerGrid = new Grid { Margin = new Thickness(16, 0, 16, 0) };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Logo/Title
            var titlePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            titlePanel.Children.Add(new TextBlock { Text = "✨", FontSize = 20, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            titlePanel.Children.Add(new TextBlock { Text = "OLLAMA", Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
            titlePanel.Children.Add(new TextBlock { Text = "SubsetC Expert", Foreground = new SolidColorBrush(Color(0x88, 0x88, 0x88)), FontSize = 12, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(titlePanel, 0);
            headerGrid.Children.Add(titlePanel);

            // Model selector
            var modelPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 0, 0) };
            modelPanel.Children.Add(new TextBlock { Text = "Model:", Foreground = new SolidColorBrush(Color(0x88, 0x88, 0x88)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            _modelCombo = new ComboBox
            {
                Width = 160,
                Background = new SolidColorBrush(Color(0x2D, 0x2D, 0x30)),
                Foreground = new SolidColorBrush(Color(0xCC, 0xCC, 0xCC)),
                BorderBrush = new SolidColorBrush(Color(0x3F, 0x3F, 0x46)),
                Padding = new Thickness(8, 4, 8, 4)
            };
            _modelCombo.SelectionChanged += (s, e) => { if (_modelCombo.SelectedItem != null && _ai != null) _ai.SetModel(_modelCombo.SelectedItem.ToString()); };
            modelPanel.Children.Add(_modelCombo);
            Grid.SetColumn(modelPanel, 1);
            headerGrid.Children.Add(modelPanel);

            // Clear button
            var clearBtn = CreateButton("🗑 Clear", false);
            clearBtn.Click += (s, e) => ShowWelcome();
            clearBtn.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(clearBtn, 3);
            headerGrid.Children.Add(clearBtn);

            header.Child = headerGrid;
            Grid.SetRow(header, 0);
            mainGrid.Children.Add(header);

            // === QUICK ACTIONS ===
            var actionsPanel = new WrapPanel { Margin = new Thickness(12, 8, 12, 8) };
            var actionsBorder = new Border
            {
                Background = new SolidColorBrush(Color(0x25, 0x25, 0x26)),
                BorderBrush = new SolidColorBrush(Color(0x3F, 0x3F, 0x46)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = actionsPanel
            };

            actionsPanel.Children.Add(new TextBlock { Text = "Quick:", Foreground = new SolidColorBrush(Color(0x66, 0x66, 0x66)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 12, 0) });

            var gameBtn = CreateButton("🎮 Game", false); gameBtn.Click += MakeGame_Click; actionsPanel.Children.Add(gameBtn);
            var osBtn = CreateButton("💻 OS/Shell", false); osBtn.Click += MakeOS_Click; actionsPanel.Children.Add(osBtn);
            var explainBtn = CreateButton("📖 Explain", false); explainBtn.Click += Explain_Click; actionsPanel.Children.Add(explainBtn);
            var fixBtn = CreateButton("🔧 Fix Errors", false); fixBtn.Click += FixErrors_Click; actionsPanel.Children.Add(fixBtn);
            var debugBtn = CreateButton("🐛 Debug", false); debugBtn.Click += Debug_Click; actionsPanel.Children.Add(debugBtn);
            var optimizeBtn = CreateButton("⚡ Optimize", false); optimizeBtn.Click += Optimize_Click; actionsPanel.Children.Add(optimizeBtn);
            var writeBtn = CreateButton("📝 Write to File", true, "#4EC9B0"); writeBtn.Click += WriteToFile_Click; actionsPanel.Children.Add(writeBtn);
            var examplesBtn = CreateButton("💡 Examples", false); examplesBtn.Click += Examples_Click; actionsPanel.Children.Add(examplesBtn);

            Grid.SetRow(actionsBorder, 1);
            mainGrid.Children.Add(actionsBorder);

            // === CHAT AREA ===
            _chatScroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(Color(0x1E, 0x1E, 0x1E))
            };
            _chatOutput = new TextBox
            {
                IsReadOnly = true,
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color(0xCC, 0xCC, 0xCC)),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                Padding = new Thickness(16),
                BorderThickness = new Thickness(0),
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true
            };
            _chatScroller.Content = _chatOutput;
            Grid.SetRow(_chatScroller, 2);
            mainGrid.Children.Add(_chatScroller);

            // === INPUT AREA ===
            var inputBorder = new Border
            {
                Background = new SolidColorBrush(Color(0x2D, 0x2D, 0x30)),
                BorderBrush = new SolidColorBrush(Color(0x3F, 0x3F, 0x46)),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(16, 12, 16, 12)
            };

            var inputGrid = new Grid();
            inputGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            inputGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _chatInput = new TextBox
            {
                Background = new SolidColorBrush(Color(0x2D, 0x2D, 0x30)),
                Foreground = new SolidColorBrush(Color(0xCC, 0xCC, 0xCC)),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                Padding = new Thickness(12, 10, 12, 10),
                BorderBrush = new SolidColorBrush(Color(0x00, 0x7A, 0xCC)),
                BorderThickness = new Thickness(1),
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                MinHeight = 45,
                MaxHeight = 120,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            _chatInput.KeyDown += Input_KeyDown;
            Grid.SetRow(_chatInput, 0);
            inputGrid.Children.Add(_chatInput);

            var buttonPanel = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            buttonPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            buttonPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            buttonPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _autoWriteCheck = new CheckBox
            {
                Content = "Auto-write code to editor",
                Foreground = new SolidColorBrush(Color(0x88, 0x88, 0x88)),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12
            };
            Grid.SetColumn(_autoWriteCheck, 0);
            buttonPanel.Children.Add(_autoWriteCheck);

            var sendPanel = new StackPanel { Orientation = Orientation.Horizontal };
            _sendBtn = CreateButton("Send", true, "#007ACC");
            _sendBtn.Padding = new Thickness(24, 8, 24, 8);
            _sendBtn.FontSize = 13;
            _sendBtn.Click += Send_Click;
            sendPanel.Children.Add(_sendBtn);

            _stopBtn = CreateButton("Stop", true, "#D32F2F");
            _stopBtn.Padding = new Thickness(24, 8, 24, 8);
            _stopBtn.FontSize = 13;
            _stopBtn.Margin = new Thickness(8, 0, 0, 0);
            _stopBtn.Visibility = Visibility.Collapsed;
            _stopBtn.Click += Stop_Click;
            sendPanel.Children.Add(_stopBtn);

            Grid.SetColumn(sendPanel, 2);
            buttonPanel.Children.Add(sendPanel);

            Grid.SetRow(buttonPanel, 1);
            inputGrid.Children.Add(buttonPanel);

            inputBorder.Child = inputGrid;
            Grid.SetRow(inputBorder, 3);
            mainGrid.Children.Add(inputBorder);

            Content = mainGrid;
        }

        private Color Color(byte r, byte g, byte b) => System.Windows.Media.Color.FromRgb(r, g, b);

        private Button CreateButton(string text, bool filled, string bgColor = "#3E3E42")
        {
            var btn = new Button
            {
                Content = text,
                Foreground = filled ? Brushes.White : new SolidColorBrush(Color(0xCC, 0xCC, 0xCC)),
                Background = filled ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(bgColor)) : Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(2),
                Cursor = Cursors.Hand,
                FontSize = 12
            };
            return btn;
        }

        private async Task InitializeAI()
        {
            _ai = new OllamaService();

            // Setup update timer to batch UI updates (60 FPS = ~16ms)
            _updateTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50) // 20 updates per second
            };
            _updateTimer.Tick += (s, e) =>
            {
                if (_tokenBuffer.Length > 0)
                {
                    _chatOutput.AppendText(_tokenBuffer.ToString());
                    _tokenBuffer.Clear();
                    _chatScroller.ScrollToEnd();
                }
            };

            _ai.OnTokenReceived += token =>
            {
                // Strip BOM and other garbage characters from start
                if (_response.Length == 0 && token.Length > 0)
                {
                    // Remove BOM (UTF-8: EF BB BF)
                    token = token.TrimStart('\uFEFF', '\u00EF', '\u00BB', '\u00BF');
                    // Remove any leading whitespace or weird characters
                    token = token.TrimStart();
                }

                _tokenBuffer.Append(token);
                _response.Append(token);

                // Start timer if not running
                if (!_updateTimer.IsEnabled)
                    Dispatcher.BeginInvoke(new Action(() => _updateTimer.Start()));
            };

            _ai.OnError += err => Dispatcher.BeginInvoke(new Action(() =>
            {
                _updateTimer.Stop();
                _tokenBuffer.Clear();
                _chatOutput.AppendText($"\n\n❌ Error: {err}\n\n");
                SetBusy(false);
            }));

            _ai.OnComplete += () => Dispatcher.BeginInvoke(new Action(() =>
            {
                _updateTimer.Stop();

                // Flush any remaining tokens
                if (_tokenBuffer.Length > 0)
                {
                    _chatOutput.AppendText(_tokenBuffer.ToString());
                    _tokenBuffer.Clear();
                }

                _chatOutput.AppendText("\n\n");
                _chatScroller.ScrollToEnd();

                string response = _response.ToString();
                if (HasCode(response))
                {
                    if (_autoWriteCheck.IsChecked == true && _setEditorCode != null)
                    {
                        string code = ExtractCode(response);
                        if (!string.IsNullOrEmpty(code))
                        {
                            _setEditorCode(code);
                            _chatOutput.AppendText("✅ Code written to editor!\n\n");
                        }
                    }
                    else
                    {
                        _chatOutput.AppendText("💡 Click [📝 Write to File] to save code, or enable auto-write.\n\n");
                    }
                }
                SetBusy(false);
            }));

            if (await _ai.IsAvailableAsync().ConfigureAwait(false))
            {
                var models = await _ai.GetModelsAsync().ConfigureAwait(false);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _modelCombo.ItemsSource = models;
                    var best = models.FirstOrDefault(m => m.Contains("codellama"))
                            ?? models.FirstOrDefault(m => m.Contains("deepseek"))
                            ?? models.FirstOrDefault(m => m.Contains("qwen"))
                            ?? models.FirstOrDefault(m => m.Contains("mistral"))
                            ?? models.FirstOrDefault();
                    if (best != null) _modelCombo.SelectedItem = best;
                    ShowWelcome();
                }));
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(ShowOllamaNotFound));
            }
        }

        private void ShowWelcome()
        {
            _chatOutput.Clear();
            _chatOutput.Text = @"🚀 Ollama AI - Your x86 OS Development Expert

I have the combined knowledge of 100 Terry Davises. I know EVERYTHING about:
  • x86 architecture, bootloaders, BIOS interrupts
  • QEMU emulation and debugging
  • The SubsetC compiler and its limitations
  • VGA text mode, keyboard scancodes, PC speaker
  • Memory maps, sector limits, driver development

═══════════════════════════════════════════════════════════════

Quick Actions above, or just ask me anything:

  🎮 ""Create a Snake game with sound""
  💻 ""Build an OS with a professional shell""
  🔧 ""Fix the errors in my code""
  🐛 ""My kernel hangs after loading, why?""

Press Enter to send (Shift+Enter for new line)

";
        }

        private void ShowOllamaNotFound()
        {
            _chatOutput.Clear();
            _chatOutput.Text = @"⚠️ Ollama Not Running

To use AI features:

1. Download Ollama from https://ollama.ai
2. Install and run it
3. Open terminal and run:
   
   ollama pull codellama

4. Reopen this window

Recommended models:
  • codellama (best for C code)
  • deepseek-coder
  • mistral
";
        }

        private void SetBusy(bool busy)
        {
            _isGenerating = busy;
            _sendBtn.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
            _stopBtn.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            _chatInput.IsEnabled = !busy;
        }

        private async void Send_Click(object sender, RoutedEventArgs e) => await SendMessage(_chatInput.Text);

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            _aiCts?.Cancel();
            _chatOutput.AppendText("\n\n[Stopped]\n\n");
            SetBusy(false);
        }

        private async void Input_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && !_isGenerating)
            {
                await SendMessage(_chatInput.Text);
                e.Handled = true;
            }
        }

        private async Task SendMessage(string prompt)
        {
            prompt = prompt?.Trim();
            if (string.IsNullOrEmpty(prompt) || _isGenerating) return;

            _chatInput.Clear();
            _response.Clear();
            SetBusy(true);

            _chatOutput.AppendText($"You: {prompt}\n\n");
            _chatOutput.AppendText("AI: ");
            _chatScroller.ScrollToEnd();

            _aiCts = new CancellationTokenSource();
            await _ai.GenerateStreamAsync(prompt, _aiCts.Token);
        }

        // === QUICK ACTIONS ===

        private async void MakeGame_Click(object sender, RoutedEventArgs e)
        {
            string game = ShowInputDialog("🎮 Generate Game", "What game do you want?", "Snake");
            if (!string.IsNullOrEmpty(game))
            {
                await SendPrompt(OllamaService.Prompt_MakeGame(game), $"Creating {game}...");
            }
        }

        private async void MakeOS_Click(object sender, RoutedEventArgs e)
        {
            string desc = ShowInputDialog("💻 Generate OS", "Describe your OS:", "OS with shell, help command, clear, echo");
            if (!string.IsNullOrEmpty(desc))
            {
                await SendPrompt(OllamaService.Prompt_MakeOS(desc), "Creating OS...");
            }
        }

        private async void Explain_Click(object sender, RoutedEventArgs e)
        {
            string code = _getEditorCode?.Invoke();
            string file = _getEditorFile?.Invoke() ?? "code";

            if (string.IsNullOrEmpty(code))
            {
                _chatOutput.AppendText("⚠️ No code in editor to explain.\n\n");
                return;
            }

            await SendPrompt(OllamaService.Prompt_Explain(code, file), "Analyzing code...");
        }

        private async void FixErrors_Click(object sender, RoutedEventArgs e)
        {
            string code = _getEditorCode?.Invoke() ?? "";
            string errors = _getBuildOutput?.Invoke() ?? "";
            string file = _getEditorFile?.Invoke() ?? "code";

            if (string.IsNullOrEmpty(errors) || (!errors.ToLower().Contains("error") && !errors.ToLower().Contains("fail")))
            {
                _chatOutput.AppendText("⚠️ No errors found in build output. Build your project first (F5).\n\n");
                return;
            }

            await SendPrompt(OllamaService.Prompt_FixErrors(code, errors, file), "Analyzing errors...");
        }

        private async void Debug_Click(object sender, RoutedEventArgs e)
        {
            string code = _getEditorCode?.Invoke() ?? "";
            string file = _getEditorFile?.Invoke() ?? "code";

            string issue = ShowInputDialog("🐛 Debug", "Describe the issue:", "Kernel hangs after loading");
            if (!string.IsNullOrEmpty(issue))
            {
                await SendPrompt(OllamaService.Prompt_Debug(code, issue, file), "Debugging...");
            }
        }

        private async void Optimize_Click(object sender, RoutedEventArgs e)
        {
            string code = _getEditorCode?.Invoke();
            string file = _getEditorFile?.Invoke() ?? "code";

            if (string.IsNullOrEmpty(code))
            {
                _chatOutput.AppendText("⚠️ No code in editor to optimize.\n\n");
                return;
            }

            await SendPrompt(OllamaService.Prompt_Optimize(code, file), "Analyzing...");
        }

        private void WriteToFile_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_response.ToString()))
            {
                _chatOutput.AppendText("⚠️ No AI response to write.\n\n");
                return;
            }

            string code = ExtractCode(_response.ToString());
            if (string.IsNullOrEmpty(code))
            {
                _chatOutput.AppendText("⚠️ No code found in the response.\n\n");
                return;
            }

            if (_writeFile != null)
            {
                string filename = ShowInputDialog("📝 Write to File", "Filename:", "kernel.c");
                if (!string.IsNullOrEmpty(filename))
                {
                    _writeFile(filename, code);
                    _chatOutput.AppendText($"✅ Written to {filename}!\n\n");
                }
            }
            else if (_setEditorCode != null)
            {
                _setEditorCode(code);
                _chatOutput.AppendText("✅ Code written to editor!\n\n");
            }
            else
            {
                Clipboard.SetText(code);
                _chatOutput.AppendText("✅ Code copied to clipboard!\n\n");
            }
        }

        private void Examples_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu { Background = new SolidColorBrush(Color(0x2D, 0x2D, 0x30)) };
            foreach (var example in OllamaService.ExamplePrompts)
            {
                var item = new MenuItem { Header = example, Foreground = new SolidColorBrush(Color(0xCC, 0xCC, 0xCC)) };
                item.Click += (s, ev) => { _chatInput.Text = example; _chatInput.Focus(); };
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }

        private async Task SendPrompt(string prompt, string statusMessage)
        {
            if (_ai == null)
            {
                _chatOutput.AppendText("\n\n❌ AI not initialized. Is Ollama running?\n\n");
                return;
            }

            _response.Clear();
            SetBusy(true);
            _chatOutput.AppendText($"{statusMessage}\n\nAI: ");
            _chatScroller.ScrollToEnd();
            _aiCts = new CancellationTokenSource();

            try
            {
                await _ai.GenerateStreamAsync(prompt, _aiCts.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                _chatOutput.AppendText($"\n\n❌ Connection failed: {ex.Message}\n");
                _chatOutput.AppendText("Make sure Ollama is running (http://localhost:11434)\n\n");
                SetBusy(false);
            }
            catch (OperationCanceledException)
            {
                SetBusy(false);
            }
            catch (Exception ex)
            {
                _chatOutput.AppendText($"\n\n❌ Error: {ex.Message}\n\n");
                SetBusy(false);
            }
        }

        private string ShowInputDialog(string title, string label, string defaultValue)
        {
            var dialog = new Window
            {
                Title = title,
                Width = 450,
                Height = 180,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = new SolidColorBrush(Color(0x2D, 0x2D, 0x30)),
                ResizeMode = ResizeMode.NoResize
            };

            var grid = new Grid { Margin = new Thickness(20) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var lbl = new TextBlock { Text = label, Foreground = Brushes.White, FontSize = 13, Margin = new Thickness(0, 0, 0, 8) };
            Grid.SetRow(lbl, 0);
            grid.Children.Add(lbl);

            var input = new TextBox
            {
                Text = defaultValue,
                Background = new SolidColorBrush(Color(0x3C, 0x3C, 0x3C)),
                Foreground = new SolidColorBrush(Color(0xCC, 0xCC, 0xCC)),
                Padding = new Thickness(10, 8, 10, 8),
                FontSize = 13,
                BorderBrush = new SolidColorBrush(Color(0x55, 0x55, 0x55))
            };
            input.KeyDown += (s, e) => { if (e.Key == Key.Enter) { dialog.DialogResult = true; } };
            Grid.SetRow(input, 1);
            grid.Children.Add(input);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var cancelBtn = new Button { Content = "Cancel", Padding = new Thickness(20, 8, 20, 8), Margin = new Thickness(0, 0, 8, 0) };
            cancelBtn.Click += (s, e) => dialog.DialogResult = false;
            var okBtn = new Button { Content = "OK", Padding = new Thickness(20, 8, 20, 8), Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#007ACC")), Foreground = Brushes.White };
            okBtn.Click += (s, e) => dialog.DialogResult = true;
            btnPanel.Children.Add(cancelBtn);
            btnPanel.Children.Add(okBtn);
            Grid.SetRow(btnPanel, 2);
            grid.Children.Add(btnPanel);

            dialog.Content = grid;
            dialog.Loaded += (s, e) => { input.Focus(); input.SelectAll(); };

            return dialog.ShowDialog() == true ? input.Text : null;
        }

        private bool HasCode(string text) =>
            text.Contains("```") || text.Contains("void kernel_main") || text.Contains("#include");

        private string ExtractCode(string response)
        {
            // Try markdown code blocks
            var match = System.Text.RegularExpressions.Regex.Match(response, @"```c?\s*([\s\S]*?)```");
            if (match.Success)
            {
                string code = match.Groups[1].Value.Trim();
                // Strip BOM
                code = code.TrimStart('\uFEFF', '\u00EF', '\u00BB', '\u00BF');
                return code;
            }

            // Try raw code
            if (response.Contains("#include") || response.Contains("void kernel_main"))
            {
                int start = response.IndexOf("#include");
                if (start == -1) start = response.IndexOf("void kernel_main");
                if (start >= 0)
                {
                    string code = response.Substring(start).Trim();
                    // Strip BOM
                    code = code.TrimStart('\uFEFF', '\u00EF', '\u00BB', '\u00BF');
                    // Strip any leading garbage before #include
                    if (code.Contains("#include"))
                    {
                        int includePos = code.IndexOf("#include");
                        code = code.Substring(includePos);
                    }
                    return code;
                }
            }

            return null;
        }
    }
}