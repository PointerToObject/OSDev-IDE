using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OSDevIDE
{
    /// <summary>
    /// Bootloader Generator - Configure and generate bootloader.asm visually
    /// No assembly knowledge required!
    /// </summary>
    public class BootloaderGenerator : Window
    {
        // Configuration options
        private TextBox _kernelLoadAddress;
        private TextBox _kernelSectors;
        private TextBox _stackAddress;
        private CheckBox _enableA20;
        private CheckBox _protectedMode;
        private CheckBox _vgaTextMode;
        private ComboBox _videoMode;
        private CheckBox _showBootMessage;
        private TextBox _bootMessage;
        private CheckBox _clearScreen;
        private TextBox _projectPath;
        
        private TextBox _previewBox;
        private Action<string> _onGenerate;

        public BootloaderGenerator(string projectPath = "", Action<string> onGenerate = null)
        {
            _onGenerate = onGenerate;
            
            Title = "Bootloader Generator";
            Width = 900;
            Height = 700;
            MinWidth = 800;
            MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E1E1E"));

            var mainGrid = new Grid();
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380) });
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Left Panel - Configuration
            var leftPanel = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#252526")),
                BorderThickness = new Thickness(0, 0, 1, 0),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3F3F46"))
            };

            var leftScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(16) };
            var configPanel = new StackPanel();

            // Title
            configPanel.Children.Add(new TextBlock
            {
                Text = "⚙️ Bootloader Configuration",
                Foreground = Brushes.White,
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 8)
            });
            configPanel.Children.Add(new TextBlock
            {
                Text = "Configure your bootloader without writing assembly!",
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#888888")),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 20),
                TextWrapping = TextWrapping.Wrap
            });

            // Memory Configuration Section
            configPanel.Children.Add(CreateSectionHeader("📍 Memory Configuration"));

            var memPanel = CreateOptionPanel();
            _kernelLoadAddress = CreateTextInput("Kernel Load Address:", "0x8000", "Where the kernel is loaded in memory");
            _stackAddress = CreateTextInput("Stack Address:", "0x7BFE", "Stack pointer location (grows down)");
            _kernelSectors = CreateTextInput("Kernel Sectors:", "100", "Number of 512-byte sectors to load (max ~127)");
            memPanel.Children.Add(CreateLabeledInput("Kernel Load Address:", _kernelLoadAddress));
            memPanel.Children.Add(CreateLabeledInput("Stack Address:", _stackAddress));
            memPanel.Children.Add(CreateLabeledInput("Kernel Sectors:", _kernelSectors));
            configPanel.Children.Add(WrapInBorder(memPanel));

            // CPU Mode Section
            configPanel.Children.Add(CreateSectionHeader("🖥️ CPU Mode"));

            var cpuPanel = CreateOptionPanel();
            _protectedMode = CreateCheckbox("Enable 32-bit Protected Mode", true, "Required for most OS development");
            _enableA20 = CreateCheckbox("Enable A20 Line", true, "Access memory above 1MB (recommended)");
            cpuPanel.Children.Add(_protectedMode);
            cpuPanel.Children.Add(_enableA20);
            configPanel.Children.Add(WrapInBorder(cpuPanel));

            // Video Section
            configPanel.Children.Add(CreateSectionHeader("🎨 Video Configuration"));

            var videoPanel = CreateOptionPanel();
            _vgaTextMode = CreateCheckbox("VGA Text Mode (80x25)", true, "Standard text mode for console output");
            _videoMode = new ComboBox { Width = 200, Margin = new Thickness(0, 8, 0, 0) };
            _videoMode.Items.Add("Text 80x25 (Mode 0x03)");
            _videoMode.Items.Add("Graphics 320x200 256-color (Mode 0x13)");
            _videoMode.Items.Add("Graphics 640x480 16-color (Mode 0x12)");
            _videoMode.SelectedIndex = 0;
            _videoMode.SelectionChanged += (s, e) => UpdatePreview();
            
            var videoModePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            videoModePanel.Children.Add(new TextBlock { Text = "Video Mode: ", Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCCCCC")), VerticalAlignment = VerticalAlignment.Center, Width = 140 });
            videoModePanel.Children.Add(_videoMode);
            
            videoPanel.Children.Add(_vgaTextMode);
            videoPanel.Children.Add(videoModePanel);
            configPanel.Children.Add(WrapInBorder(videoPanel));

            // Boot Display Section
            configPanel.Children.Add(CreateSectionHeader("💬 Boot Display"));

            var displayPanel = CreateOptionPanel();
            _clearScreen = CreateCheckbox("Clear Screen on Boot", true, "Clear the screen before showing message");
            _showBootMessage = CreateCheckbox("Show Boot Message", true, "Display a message while loading");
            _bootMessage = CreateTextInput("Boot Message:", "Loading OS...", "Message shown during boot");
            displayPanel.Children.Add(_clearScreen);
            displayPanel.Children.Add(_showBootMessage);
            displayPanel.Children.Add(CreateLabeledInput("Boot Message:", _bootMessage));
            configPanel.Children.Add(WrapInBorder(displayPanel));

            // Output Section
            configPanel.Children.Add(CreateSectionHeader("📁 Output"));
            var outputPanel = CreateOptionPanel();
            _projectPath = CreateTextInput("Project Path:", projectPath, "Where to save bootloader.asm");
            outputPanel.Children.Add(CreateLabeledInput("Project Path:", _projectPath));
            configPanel.Children.Add(WrapInBorder(outputPanel));

            // Generate Button
            var generateBtn = new Button
            {
                Content = "🚀 Generate Bootloader",
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#007ACC")),
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(20, 12, 20, 12),
                Margin = new Thickness(0, 24, 0, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                BorderThickness = new Thickness(0)
            };
            generateBtn.Click += GenerateBootloader_Click;
            configPanel.Children.Add(generateBtn);

            // Quick Presets
            configPanel.Children.Add(new TextBlock
            {
                Text = "Quick Presets:",
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#888888")),
                FontSize = 11,
                Margin = new Thickness(0, 16, 0, 8)
            });

            var presetPanel = new WrapPanel();
            var preset1 = CreatePresetButton("Basic OS", () => ApplyPreset("basic"));
            var preset2 = CreatePresetButton("Game Dev", () => ApplyPreset("game"));
            var preset3 = CreatePresetButton("Graphics", () => ApplyPreset("graphics"));
            var preset4 = CreatePresetButton("Minimal", () => ApplyPreset("minimal"));
            presetPanel.Children.Add(preset1);
            presetPanel.Children.Add(preset2);
            presetPanel.Children.Add(preset3);
            presetPanel.Children.Add(preset4);
            configPanel.Children.Add(presetPanel);

            leftScroll.Content = configPanel;
            leftPanel.Child = leftScroll;
            Grid.SetColumn(leftPanel, 0);
            mainGrid.Children.Add(leftPanel);

            // Right Panel - Preview
            var rightPanel = new Grid();
            rightPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
            rightPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var previewHeader = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D2D30")),
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3F3F46"))
            };
            previewHeader.Child = new TextBlock
            {
                Text = "📄 Preview: bootloader.asm",
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(16, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(previewHeader, 0);
            rightPanel.Children.Add(previewHeader);

            var previewScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E1E1E"))
            };
            _previewBox = new TextBox
            {
                IsReadOnly = true,
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D4D4D4")),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Padding = new Thickness(16),
                BorderThickness = new Thickness(0),
                TextWrapping = TextWrapping.NoWrap,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            previewScroll.Content = _previewBox;
            Grid.SetRow(previewScroll, 1);
            rightPanel.Children.Add(previewScroll);

            Grid.SetColumn(rightPanel, 1);
            mainGrid.Children.Add(rightPanel);

            Content = mainGrid;

            // Wire up change events
            WireUpEvents();
            UpdatePreview();
        }

        private void WireUpEvents()
        {
            _kernelLoadAddress.TextChanged += (s, e) => UpdatePreview();
            _stackAddress.TextChanged += (s, e) => UpdatePreview();
            _kernelSectors.TextChanged += (s, e) => UpdatePreview();
            _protectedMode.Checked += (s, e) => UpdatePreview();
            _protectedMode.Unchecked += (s, e) => UpdatePreview();
            _enableA20.Checked += (s, e) => UpdatePreview();
            _enableA20.Unchecked += (s, e) => UpdatePreview();
            _vgaTextMode.Checked += (s, e) => UpdatePreview();
            _vgaTextMode.Unchecked += (s, e) => UpdatePreview();
            _clearScreen.Checked += (s, e) => UpdatePreview();
            _clearScreen.Unchecked += (s, e) => UpdatePreview();
            _showBootMessage.Checked += (s, e) => UpdatePreview();
            _showBootMessage.Unchecked += (s, e) => UpdatePreview();
            _bootMessage.TextChanged += (s, e) => UpdatePreview();
        }

        private TextBlock CreateSectionHeader(string text)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#007ACC")),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 16, 0, 8)
            };
        }

        private StackPanel CreateOptionPanel()
        {
            return new StackPanel
            {
                Margin = new Thickness(0, 0, 0, 8)
            };
        }

        private TextBox CreateTextInput(string label, string defaultValue, string tooltip)
        {
            // We just return the textbox - caller will add it with a label
            var textBox = new TextBox
            {
                Text = defaultValue,
                Width = 150,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3C3C3C")),
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCCCCC")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#555555")),
                Padding = new Thickness(6, 4, 6, 4),
                ToolTip = tooltip,
                Tag = label // Store label for reference
            };

            return textBox;
        }

        private StackPanel CreateLabeledInput(string label, TextBox textBox)
        {
            var container = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
            container.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCCCCC")),
                Width = 140,
                VerticalAlignment = VerticalAlignment.Center
            });
            container.Children.Add(textBox);
            return container;
        }

        private Border WrapInBorder(StackPanel panel)
        {
            return new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D2D30")),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 8),
                Child = panel
            };
        }

        private CheckBox CreateCheckbox(string text, bool isChecked, string tooltip)
        {
            return new CheckBox
            {
                Content = text,
                IsChecked = isChecked,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCCCCC")),
                Margin = new Thickness(0, 4, 0, 4),
                ToolTip = tooltip
            };
        }

        private Button CreatePresetButton(string text, Action onClick)
        {
            var btn = new Button
            {
                Content = text,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3C3C3C")),
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCCCCC")),
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 8, 0),
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btn.Click += (s, e) => onClick();
            return btn;
        }

        private void ApplyPreset(string preset)
        {
            switch (preset)
            {
                case "basic":
                    _kernelLoadAddress.Text = "0x8000";
                    _stackAddress.Text = "0x7BFE";
                    _kernelSectors.Text = "100";
                    _protectedMode.IsChecked = true;
                    _enableA20.IsChecked = true;
                    _vgaTextMode.IsChecked = true;
                    _videoMode.SelectedIndex = 0;
                    _clearScreen.IsChecked = true;
                    _showBootMessage.IsChecked = true;
                    _bootMessage.Text = "Loading OS...";
                    break;

                case "game":
                    _kernelLoadAddress.Text = "0x8000";
                    _stackAddress.Text = "0x7BFE";
                    _kernelSectors.Text = "127";
                    _protectedMode.IsChecked = true;
                    _enableA20.IsChecked = true;
                    _vgaTextMode.IsChecked = true;
                    _videoMode.SelectedIndex = 0;
                    _clearScreen.IsChecked = true;
                    _showBootMessage.IsChecked = false;
                    break;

                case "graphics":
                    _kernelLoadAddress.Text = "0x8000";
                    _stackAddress.Text = "0x7BFE";
                    _kernelSectors.Text = "100";
                    _protectedMode.IsChecked = true;
                    _enableA20.IsChecked = true;
                    _vgaTextMode.IsChecked = false;
                    _videoMode.SelectedIndex = 1; // 320x200
                    _clearScreen.IsChecked = false;
                    _showBootMessage.IsChecked = false;
                    break;

                case "minimal":
                    _kernelLoadAddress.Text = "0x8000";
                    _stackAddress.Text = "0x7C00";
                    _kernelSectors.Text = "50";
                    _protectedMode.IsChecked = true;
                    _enableA20.IsChecked = true;
                    _vgaTextMode.IsChecked = false;
                    _videoMode.SelectedIndex = 0;
                    _clearScreen.IsChecked = false;
                    _showBootMessage.IsChecked = false;
                    break;
            }
            UpdatePreview();
        }

        private void UpdatePreview()
        {
            _previewBox.Text = GenerateBootloaderCode();
        }

        private string GenerateBootloaderCode()
        {
            var sb = new System.Text.StringBuilder();
            
            string kernelAddr = _kernelLoadAddress.Text.Trim();
            string stackAddr = _stackAddress.Text.Trim();
            string sectors = _kernelSectors.Text.Trim();
            bool pm = _protectedMode.IsChecked == true;
            bool a20 = _enableA20.IsChecked == true;
            bool showMsg = _showBootMessage.IsChecked == true;
            bool clearScr = _clearScreen.IsChecked == true;
            string bootMsg = _bootMessage.Text;
            int videoModeIdx = _videoMode.SelectedIndex;

            sb.AppendLine("; ═══════════════════════════════════════════════════════════════════");
            sb.AppendLine("; SubsetC OS Bootloader - Generated by OS Dev IDE");
            sb.AppendLine("; ═══════════════════════════════════════════════════════════════════");
            sb.AppendLine();
            sb.AppendLine("[org 0x7C00]");
            sb.AppendLine("[BITS 16]");
            sb.AppendLine();
            sb.AppendLine("start:");
            sb.AppendLine("    ; Initialize segments");
            sb.AppendLine("    xor ax, ax");
            sb.AppendLine("    mov ds, ax");
            sb.AppendLine("    mov es, ax");
            sb.AppendLine("    mov ss, ax");
            sb.AppendLine($"    mov sp, {stackAddr}");
            sb.AppendLine();

            if (clearScr)
            {
                sb.AppendLine("    ; Clear screen");
                sb.AppendLine("    mov ax, 0x0003");
                sb.AppendLine("    int 0x10");
                sb.AppendLine();
            }

            if (showMsg && !string.IsNullOrEmpty(bootMsg))
            {
                sb.AppendLine("    ; Display boot message");
                sb.AppendLine("    mov si, boot_msg");
                sb.AppendLine("    call print_string");
                sb.AppendLine();
            }

            sb.AppendLine("    ; Check LBA extensions available");
            sb.AppendLine("    mov ah, 0x41");
            sb.AppendLine("    mov bx, 0x55AA");
            sb.AppendLine("    mov dl, 0x80");
            sb.AppendLine("    int 0x13");
            sb.AppendLine("    jc .no_lba");
            sb.AppendLine();

            sb.AppendLine("    ; Load kernel using LBA");
            sb.AppendLine("    mov si, dap");
            sb.AppendLine("    mov ah, 0x42");
            sb.AppendLine("    mov dl, 0x80");
            sb.AppendLine("    int 0x13");
            sb.AppendLine("    jc .read_fail");
            sb.AppendLine();

            if (a20)
            {
                sb.AppendLine("    ; Enable A20 line");
                sb.AppendLine("    in al, 0x92");
                sb.AppendLine("    or al, 2");
                sb.AppendLine("    out 0x92, al");
                sb.AppendLine();
            }

            if (pm)
            {
                sb.AppendLine("    ; Switch to protected mode");
                sb.AppendLine("    cli");
                sb.AppendLine("    lgdt [gdt_desc]");
                sb.AppendLine("    mov eax, cr0");
                sb.AppendLine("    or eax, 1");
                sb.AppendLine("    mov cr0, eax");
                sb.AppendLine("    jmp 0x08:protected_mode");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine($"    ; Jump to kernel (16-bit mode)");
                sb.AppendLine($"    jmp 0x0000:{kernelAddr}");
                sb.AppendLine();
            }

            sb.AppendLine(".no_lba:");
            sb.AppendLine("    mov si, err_lba");
            sb.AppendLine("    call print_string");
            sb.AppendLine("    jmp hang");
            sb.AppendLine();

            sb.AppendLine(".read_fail:");
            sb.AppendLine("    mov si, err_read");
            sb.AppendLine("    call print_string");
            sb.AppendLine("    jmp hang");
            sb.AppendLine();

            sb.AppendLine("hang:");
            sb.AppendLine("    hlt");
            sb.AppendLine("    jmp hang");
            sb.AppendLine();

            sb.AppendLine("; Print string (SI = string pointer)");
            sb.AppendLine("print_string:");
            sb.AppendLine("    lodsb");
            sb.AppendLine("    or al, al");
            sb.AppendLine("    jz .done");
            sb.AppendLine("    mov ah, 0x0E");
            sb.AppendLine("    int 0x10");
            sb.AppendLine("    jmp print_string");
            sb.AppendLine(".done:");
            sb.AppendLine("    ret");
            sb.AppendLine();

            sb.AppendLine("; Disk Address Packet for LBA read");
            sb.AppendLine("dap:");
            sb.AppendLine("    db 16          ; Size of DAP");
            sb.AppendLine("    db 0           ; Reserved");
            sb.AppendLine($"    dw {sectors}        ; Sectors to read");
            sb.AppendLine($"    dw {kernelAddr}     ; Offset");
            sb.AppendLine("    dw 0x0000      ; Segment");
            sb.AppendLine("    dq 1           ; Start LBA (sector 1)");
            sb.AppendLine();

            if (showMsg && !string.IsNullOrEmpty(bootMsg))
            {
                sb.AppendLine($"boot_msg: db \"{bootMsg}\", 13, 10, 0");
            }
            sb.AppendLine("err_lba:  db \"No LBA\", 13, 10, 0");
            sb.AppendLine("err_read: db \"Read fail\", 13, 10, 0");
            sb.AppendLine();

            if (pm)
            {
                sb.AppendLine("; GDT for protected mode");
                sb.AppendLine("gdt_start:");
                sb.AppendLine("    dq 0                    ; Null descriptor");
                sb.AppendLine("gdt_code:");
                sb.AppendLine("    dw 0xFFFF, 0x0000       ; Code segment");
                sb.AppendLine("    db 0x00, 0x9A, 0xCF, 0x00");
                sb.AppendLine("gdt_data:");
                sb.AppendLine("    dw 0xFFFF, 0x0000       ; Data segment");
                sb.AppendLine("    db 0x00, 0x92, 0xCF, 0x00");
                sb.AppendLine("gdt_end:");
                sb.AppendLine();
                sb.AppendLine("gdt_desc:");
                sb.AppendLine("    dw gdt_end - gdt_start - 1");
                sb.AppendLine("    dd gdt_start");
                sb.AppendLine();

                sb.AppendLine("; Pad to boot signature");
                sb.AppendLine("times 510-($-$$) db 0");
                sb.AppendLine("dw 0xAA55");
                sb.AppendLine();

                sb.AppendLine("; ═══════════════════════════════════════════════════════════════════");
                sb.AppendLine("; 32-BIT PROTECTED MODE CODE");
                sb.AppendLine("; ═══════════════════════════════════════════════════════════════════");
                sb.AppendLine("[BITS 32]");
                sb.AppendLine("protected_mode:");
                sb.AppendLine("    ; Setup segments");
                sb.AppendLine("    mov ax, 0x10");
                sb.AppendLine("    mov ds, ax");
                sb.AppendLine("    mov es, ax");
                sb.AppendLine("    mov fs, ax");
                sb.AppendLine("    mov gs, ax");
                sb.AppendLine("    mov ss, ax");
                sb.AppendLine("    mov esp, 0x90000");
                sb.AppendLine();

                // Set video mode if needed
                if (videoModeIdx == 1)
                {
                    sb.AppendLine("    ; Note: Graphics mode 13h should be set in real mode");
                    sb.AppendLine("    ; Consider setting it before switching to protected mode");
                }

                sb.AppendLine($"    ; Jump to kernel");
                sb.AppendLine($"    jmp {kernelAddr}");
            }
            else
            {
                sb.AppendLine("; Pad to boot signature");
                sb.AppendLine("times 510-($-$$) db 0");
                sb.AppendLine("dw 0xAA55");
            }

            return sb.ToString();
        }

        private void GenerateBootloader_Click(object sender, RoutedEventArgs e)
        {
            string code = GenerateBootloaderCode();
            string projectPath = _projectPath.Text.Trim();

            if (!string.IsNullOrEmpty(projectPath))
            {
                try
                {
                    string filePath = Path.Combine(projectPath, "bootloader.asm");
                    File.WriteAllText(filePath, code);
                    
                    _onGenerate?.Invoke(filePath);
                    
                    MessageBox.Show(
                        $"Bootloader generated successfully!\n\nSaved to: {filePath}",
                        "Success",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error saving file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                // Copy to clipboard
                Clipboard.SetText(code);
                MessageBox.Show("Bootloader code copied to clipboard!\n\nNo project path specified.", "Copied", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }
}
