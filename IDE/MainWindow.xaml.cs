using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using System.Xml;
using MaterialDesignThemes.Wpf;

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
        private ObservableCollection<TabItem> openTabs = new ObservableCollection<TabItem>();
        private TabItem draggedTab = null;
        private Border draggedTabBorder = null;
        private HashSet<string> expandedFolders = new HashSet<string>();

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

            // Add zoom functionality with Ctrl+Scroll
            CodeEditor.PreviewMouseWheel += CodeEditor_MouseWheel;
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

        private void SearchFiles_Click(object sender, RoutedEventArgs e)
            => ShowDarkMessageBox("Search feature coming soon!", "OS Dev IDE");

        private void Extensions_Click(object sender, RoutedEventArgs e)
            => ShowDarkMessageBox("Extensions feature coming soon!", "OS Dev IDE");

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
                Title = "New OS Project",
                Width = 500,
                Height = 250,
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
                    ShowDarkMessageBox("This doesn't look like a valid OS project (missing Kernel/kernel.c)", "Invalid Project");
                    return;
                }

                projectPath = selectedPath;
                RefreshFileTree();

                LoadFile(kernelFile);

                OutputConsole.Text = $"✓ Project opened: {projectPath}\n";
                StatusText.Text = "Project opened successfully";
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

        private void RefreshFileTree()
        {
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

            root.IsExpanded = true;
            FileTree.Items.Add(root);

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
                        icon = "Memory";
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
                    contextMenuBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
                    contextMenuBorder.SetValue(Border.PaddingProperty, new Thickness(0));

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
                    border.SetValue(Border.BorderThicknessProperty, new Thickness(0));
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
                CodeEditor.Text = $@"
╔════════════════════════════════════════════════════════════╗
║                                                            ║
║                    BINARY FILE VIEWER                      ║
║                                                            ║
╔════════════════════════════════════════════════════════════╗

File: {Path.GetFileName(filePath)}
Type: {ext.ToUpper().TrimStart('.')} Binary File
Size: {new FileInfo(filePath).Length:N0} bytes
Path: {filePath}

────────────────────────────────────────────────────────────

This is a binary file and cannot be displayed as text.
Binary files contain compiled machine code or data.

Common binary files in OS development:
  • .bin  - Raw binary executable or bootloader
  • .o    - Object file (compiled but not linked)
  • .img  - Disk image
  • .iso  - ISO disk image

To view or edit this file, use a hex editor or
appropriate binary tools.

────────────────────────────────────────────────────────────
";
                FileTypeText.Text = "Binary";
                StatusText.Text = $"Binary file: {Path.GetFileName(filePath)}";
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
                    Padding = new Thickness(12, 8, 8, 8),
                    Cursor = Cursors.Hand,
                    AllowDrop = true
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
                    BorderThickness = new Thickness(0),
                    FontSize = 14,
                    Padding = new Thickness(4, 0, 4, 0),
                    Cursor = Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center
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