using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using System.Xml;

namespace OSDevIDE
{
    public partial class MainWindow : Window
    {
        private string projectPath = "";
        private string currentFile = "";
        private string selectedTemplate = "Empty Project";

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

        public MainWindow()
        {
            InitializeComponent();
            LoadSyntaxHighlighting();

            CodeEditor.Text = "// Welcome to OS Dev IDE\n// Create or open a project to get started\n";
            OutputConsole.Text = "";
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
                MaximizeWindow_Click(sender, null);
            else
                DragMove();
        }

        private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void MaximizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

        private void SearchFiles_Click(object sender, RoutedEventArgs e)
            => MessageBox.Show("Search feature coming soon!", "OS Dev IDE", MessageBoxButton.OK, MessageBoxImage.Information);

        private void Extensions_Click(object sender, RoutedEventArgs e)
            => MessageBox.Show("Extensions feature coming soon!", "OS Dev IDE", MessageBoxButton.OK, MessageBoxImage.Information);

        private void LoadSyntaxHighlighting()
        {
            string cSyntax = @"<?xml version=""1.0""?>
<SyntaxDefinition name=""C"" xmlns=""http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008"">
    <Color name=""Comment"" foreground=""#6A9955"" />
    <Color name=""String"" foreground=""#CE9178"" />
    <Color name=""Keyword"" foreground=""#569CD6"" fontWeight=""bold"" />
    <Color name=""Type"" foreground=""#4EC9B0"" />
    <Color name=""Number"" foreground=""#B5CEA8"" />
    <Color name=""Preprocessor"" foreground=""#C586C0"" />
    
    <RuleSet>
        <Span color=""Comment"" begin=""//"" />
        <Span color=""Comment"" multiline=""true"" begin=""/\*"" end=""\*/"" />
        <Span color=""String"" multiline=""true"">
            <Begin>""</Begin>
            <End>""</End>
            <RuleSet>
                <Span begin=""\\"" end=""."" />
            </RuleSet>
        </Span>
        <Span color=""String"">
            <Begin>'</Begin>
            <End>'</End>
        </Span>
        <Span color=""Preprocessor"" begin=""#"" />
        
        <Keywords color=""Keyword"">
            <Word>if</Word><Word>else</Word><Word>while</Word><Word>for</Word><Word>do</Word>
            <Word>switch</Word><Word>case</Word><Word>default</Word><Word>break</Word><Word>continue</Word>
            <Word>return</Word><Word>goto</Word><Word>sizeof</Word><Word>typedef</Word><Word>struct</Word>
            <Word>union</Word><Word>enum</Word><Word>static</Word><Word>extern</Word><Word>const</Word>
            <Word>volatile</Word><Word>register</Word><Word>auto</Word>
        </Keywords>
        
        <Keywords color=""Type"">
            <Word>void</Word><Word>char</Word><Word>short</Word><Word>int</Word><Word>long</Word>
            <Word>float</Word><Word>double</Word><Word>signed</Word><Word>unsigned</Word>
        </Keywords>
        
        <Rule color=""Number"">
            \b0[xX][0-9a-fA-F]+|(\b\d+(\.[0-9]+)?|\.[0-9]+)([eE][+-]?[0-9]+)?
        </Rule>
    </RuleSet>
</SyntaxDefinition>";

            using (XmlReader reader = XmlReader.Create(new System.IO.StringReader(cSyntax)))
            {
                CodeEditor.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            }
        }

        private void NewProject_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Window
            {
                Title = "New OS Project",
                Width = 500,
                Height = 250,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D2D30")),
                ResizeMode = ResizeMode.NoResize
            };

            var grid = new Grid { Margin = new Thickness(20) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var title = new TextBlock
            {
                Text = "Choose Project Template",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
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
                Margin = new Thickness(0, 0, 0, 15),
                GroupName = "template"
            };

            var radio2 = new RadioButton
            {
                Content = "Empty Kernel (with bootloader at 0x1000)",
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCCCCC")),
                FontSize = 14,
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
                Cursor = System.Windows.Input.Cursors.Hand
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
                Cursor = System.Windows.Input.Cursors.Hand
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
                selectedTemplate = radio2.IsChecked == true ? "Empty Kernel" : "Empty Project";

                var folderDialog = new System.Windows.Forms.FolderBrowserDialog();
                folderDialog.Description = "Select folder for new OS project";

                if (folderDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    projectPath = Path.Combine(folderDialog.SelectedPath, "MyOSProject");
                    Directory.CreateDirectory(projectPath);
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
                    RefreshFileTree();
                    OutputConsole.Text = $"✓ Project created at {projectPath}\n✓ Template: {selectedTemplate}\n";
                    StatusText.Text = "Project created successfully";
                }
            }
        }

        private void OpenProject_Click(object sender, RoutedEventArgs e)
        {
            var folderDialog = new System.Windows.Forms.FolderBrowserDialog();
            folderDialog.Description = "Select existing OS project folder";

            if (folderDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                string selectedPath = folderDialog.SelectedPath;

                string kernelFile = Path.Combine(selectedPath, "Kernel", "kernel.c");
                if (!File.Exists(kernelFile))
                {
                    MessageBox.Show("This doesn't look like a valid OS project (missing Kernel/kernel.c)",
                        "Invalid Project", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                projectPath = selectedPath;
                RefreshFileTree();

                LoadFile(kernelFile);

                OutputConsole.Text = $"✓ Project opened: {projectPath}\n";
                StatusText.Text = "Project opened successfully";
            }
        }

        private void RefreshFileTree()
        {
            FileTree.Items.Clear();

            if (string.IsNullOrEmpty(projectPath) || !Directory.Exists(projectPath))
                return;

            TreeViewItem root = new TreeViewItem
            {
                Header = CreateHeader("FolderOpen", Path.GetFileName(projectPath), "#D7BA7D", true),
                Tag = projectPath
            };

            string bootDir = Path.Combine(projectPath, "Bootloader");
            if (Directory.Exists(bootDir))
            {
                TreeViewItem bootFolder = new TreeViewItem
                {
                    Header = CreateHeader("Folder", "Bootloader", "#D7BA7D", false),
                    Tag = bootDir
                };

                foreach (var file in Directory.GetFiles(bootDir))
                {
                    TreeViewItem fileItem = new TreeViewItem
                    {
                        Header = CreateHeader("FileCode", Path.GetFileName(file), "#8DC891", false),
                        Tag = file
                    };
                    bootFolder.Items.Add(fileItem);
                }
                root.Items.Add(bootFolder);
            }

            string kernelDir = Path.Combine(projectPath, "Kernel");
            if (Directory.Exists(kernelDir))
            {
                TreeViewItem kernelFolder = new TreeViewItem
                {
                    Header = CreateHeader("Folder", "Kernel", "#D7BA7D", false),
                    Tag = kernelDir
                };

                foreach (var file in Directory.GetFiles(kernelDir))
                {
                    TreeViewItem fileItem = new TreeViewItem
                    {
                        Header = CreateHeader("FileCode", Path.GetFileName(file), "#6AB7FF", false),
                        Tag = file
                    };
                    kernelFolder.Items.Add(fileItem);
                }
                root.Items.Add(kernelFolder);
            }

            string buildDir = Path.Combine(projectPath, "build");
            if (Directory.Exists(buildDir))
            {
                TreeViewItem buildFolder = new TreeViewItem
                {
                    Header = CreateHeader("Folder", "build", "#D7BA7D", false),
                    Tag = buildDir
                };
                root.Items.Add(buildFolder);
            }

            root.IsExpanded = true;
            FileTree.Items.Add(root);
        }

        private StackPanel CreateHeader(string iconKind, string text, string color, bool bold)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };

            var icon = new MaterialDesignThemes.Wpf.PackIcon
            {
                Width = 16,
                Height = 16,
                Margin = new Thickness(0, 0, 6, 0),
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color))
            };

            var kindType = typeof(MaterialDesignThemes.Wpf.PackIconKind);
            icon.Kind = (MaterialDesignThemes.Wpf.PackIconKind)Enum.Parse(kindType, iconKind);

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

            currentFile = filePath;
            CodeEditor.Load(filePath);

            string ext = Path.GetExtension(filePath).ToLower();
            FileTypeText.Text = ext == ".c" ? "C" : ext == ".asm" ? "Assembly" : "Text";
            StatusText.Text = $"Opened: {Path.GetFileName(filePath)}";
        }

        private async void CompileOnly_Click(object sender, RoutedEventArgs e) => await DoBuild(false);
        private async void BuildRun_Click(object sender, RoutedEventArgs e) => await DoBuild(true);

        private async System.Threading.Tasks.Task DoBuild(bool runQemu)
        {
            if (string.IsNullOrEmpty(projectPath))
            {
                OutputConsole.Text = "⚠ Create a project first!";
                StatusText.Text = "No project loaded";
                return;
            }

            if (!string.IsNullOrEmpty(currentFile))
                CodeEditor.Save(currentFile);

            OutputConsole.Clear();
            OutputConsole.AppendText("╔═══════════════════════════════════════╗\n");
            OutputConsole.AppendText("║         BUILD STARTED                 ║\n");
            OutputConsole.AppendText("╚═══════════════════════════════════════╝\n\n");

            string kernelDir = Path.Combine(projectPath, "Kernel");
            string bootDir = Path.Combine(projectPath, "Bootloader");

            string compilerExe = "Compiler-x86_32.exe";
            string compilerPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, compilerExe);

            if (!File.Exists(compilerPath))
            {
                OutputConsole.AppendText($"✗ ERROR: Compiler not found at {compilerPath}\n");
                StatusText.Text = "Build failed";
                return;
            }

            // Compile kernel.c → kernel.asm
            string kernelAsm = Path.Combine(kernelDir, "kernel.asm");
            OutputConsole.AppendText($"[1/4] Compiling kernel.c to assembly...\n");

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

            if (!string.IsNullOrWhiteSpace(compOut)) OutputConsole.AppendText(compOut + "\n");
            if (!string.IsNullOrWhiteSpace(compErr)) OutputConsole.AppendText(compErr + "\n");

            if (compilerProc.ExitCode != 0 || !File.Exists(kernelAsm))
            {
                OutputConsole.AppendText("\n✗ COMPILATION FAILED\n");
                StatusText.Text = "Build failed";
                return;
            }

            OutputConsole.AppendText("✓ kernel.asm generated\n\n");

            // WSL: assemble and create os-image.bin
            OutputConsole.AppendText("[2/4] Assembling and creating image...\n");
            StatusText.Text = "Creating image...";

            string driveLetter = projectPath[0].ToString().ToLower();
            string wslPath = "/mnt/" + driveLetter + projectPath.Substring(2).Replace("\\", "/");

            string wslScript = $"cd '{wslPath}' && ";

            if (File.Exists(Path.Combine(bootDir, "bootloader.asm")))
                wslScript += $"cd Bootloader && nasm -f bin bootloader.asm -o bootloader.bin && cd .. && ";
            else
                OutputConsole.AppendText("⚠ bootloader.asm missing, skipping bootloader\n");

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
                    Dispatcher.Invoke(() => OutputConsole.AppendText(a.Data + "\n"));
            };
            wslProc.ErrorDataReceived += (s, a) =>
            {
                if (a.Data != null)
                    Dispatcher.Invoke(() => OutputConsole.AppendText(a.Data + "\n"));
            };

            wslProc.Start();
            wslProc.BeginOutputReadLine();
            wslProc.BeginErrorReadLine();
            await wslProc.WaitForExitAsync();

            if (wslProc.ExitCode == 0)
            {
                OutputConsole.AppendText("\n╔═══════════════════════════════════════╗\n");
                OutputConsole.AppendText("║       ✓ BUILD SUCCESSFUL              ║\n");
                OutputConsole.AppendText("╚═══════════════════════════════════════╝\n");
                StatusText.Text = runQemu ? "Running in QEMU" : "Build successful";
            }
            else
            {
                OutputConsole.AppendText("\n✗ BUILD FAILED\n");
                StatusText.Text = "Build failed";
            }
        }

    }
}