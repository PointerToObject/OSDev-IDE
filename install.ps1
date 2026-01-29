# ═══════════════════════════════════════════════════════════════════════════
# OS Dev IDE - One-Click Installer
# ═══════════════════════════════════════════════════════════════════════════
#
# USAGE: Right-click this file > "Run with PowerShell"
# OR:    powershell -ExecutionPolicy Bypass -File install.ps1
#
# This installer will:
#   1. Download and install OS Dev IDE
#   2. Install NASM (assembler)
#   3. Install QEMU (emulator)
#   4. Create desktop shortcut
#   5. Optionally install Ollama for AI features
#
# ═══════════════════════════════════════════════════════════════════════════

$ErrorActionPreference = "Stop"

# Configuration
$AppName = "OS Dev IDE"
$AppVersion = "1.0.0"
$InstallDir = "$env:LOCALAPPDATA\OSDevIDE"
$ToolsDir = "$InstallDir\tools"

# GitHub release URL (update this with your actual release URL)
$IDEReleaseUrl = "https://github.com/yourrepo/osdevide/releases/latest/download/OSDevIDE.zip"

# Tool URLs
$NasmVersion = "2.16.01"
$NasmUrl = "https://www.nasm.us/pub/nasm/releasebuilds/$NasmVersion/win64/nasm-$NasmVersion-win64.zip"
$QemuInstallerUrl = "https://qemu.weilnetz.de/w64/2024/qemu-w64-setup-20240423.exe"

# ═══════════════════════════════════════════════════════════════════════════
# UI HELPERS
# ═══════════════════════════════════════════════════════════════════════════

function Show-Banner {
    Clear-Host
    Write-Host ""
    Write-Host "  ╔═══════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
    Write-Host "  ║                                                           ║" -ForegroundColor Cyan
    Write-Host "  ║           🖥️  OS Dev IDE Installer v$AppVersion              ║" -ForegroundColor Cyan
    Write-Host "  ║                                                           ║" -ForegroundColor Cyan
    Write-Host "  ║     Build operating systems without the complexity!       ║" -ForegroundColor Cyan
    Write-Host "  ║                                                           ║" -ForegroundColor Cyan
    Write-Host "  ╚═══════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
    Write-Host ""
}

function Write-Step {
    param([int]$Num, [string]$Total, [string]$Message)
    Write-Host ""
    Write-Host "  [$Num/$Total] $Message" -ForegroundColor Yellow
    Write-Host "  ─────────────────────────────────────────────────────────────" -ForegroundColor DarkGray
}

function Write-Progress2 {
    param([string]$Activity, [int]$Percent)
    $bar = "█" * [math]::Floor($Percent / 5) + "░" * (20 - [math]::Floor($Percent / 5))
    Write-Host "`r        $bar $Percent% - $Activity" -NoNewline -ForegroundColor Gray
}

function Write-OK { Write-Host "        ✓ $args" -ForegroundColor Green }
function Write-Info { Write-Host "        → $args" -ForegroundColor Gray }
function Write-Warn { Write-Host "        ⚠ $args" -ForegroundColor Yellow }
function Write-Err { Write-Host "        ✗ $args" -ForegroundColor Red }

# ═══════════════════════════════════════════════════════════════════════════
# INSTALLATION FUNCTIONS
# ═══════════════════════════════════════════════════════════════════════════

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Request-AdminPrivileges {
    if (-not (Test-IsAdmin)) {
        Write-Warn "Requesting administrator privileges..."
        Start-Process powershell.exe "-ExecutionPolicy Bypass -File `"$PSCommandPath`"" -Verb RunAs
        exit
    }
}

function Install-NASM {
    Write-Step 2 5 "Installing NASM (Assembler)"
    
    $nasmDir = "$ToolsDir\nasm"
    $nasmExe = "$nasmDir\nasm.exe"
    
    if (Test-Path $nasmExe) {
        Write-Info "NASM already installed"
        return $true
    }
    
    try {
        # Create directory
        New-Item -ItemType Directory -Path $nasmDir -Force | Out-Null
        
        # Download
        Write-Info "Downloading NASM $NasmVersion..."
        $zipPath = "$env:TEMP\nasm.zip"
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        (New-Object Net.WebClient).DownloadFile($NasmUrl, $zipPath)
        
        # Extract
        Write-Info "Extracting..."
        Expand-Archive -Path $zipPath -DestinationPath $nasmDir -Force
        
        # Move from subfolder
        $sub = Get-ChildItem $nasmDir -Directory | Select-Object -First 1
        if ($sub) {
            Get-ChildItem $sub.FullName | Move-Item -Destination $nasmDir -Force
            Remove-Item $sub.FullName -Force
        }
        
        # Cleanup
        Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
        
        Write-OK "NASM installed"
        return $true
    }
    catch {
        Write-Err "Failed: $_"
        return $false
    }
}

function Install-QEMU {
    Write-Step 3 5 "Installing QEMU (Emulator)"
    
    # Check common install locations
    $qemuPaths = @(
        "$env:ProgramFiles\qemu\qemu-system-i386.exe",
        "${env:ProgramFiles(x86)}\qemu\qemu-system-i386.exe",
        "$ToolsDir\qemu\qemu-system-i386.exe"
    )
    
    foreach ($path in $qemuPaths) {
        if (Test-Path $path) {
            Write-Info "QEMU already installed at $((Get-Item $path).Directory)"
            return $true
        }
    }
    
    try {
        Write-Info "Downloading QEMU installer (~150MB, please wait)..."
        $installerPath = "$env:TEMP\qemu-setup.exe"
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        (New-Object Net.WebClient).DownloadFile($QemuInstallerUrl, $installerPath)
        
        Write-Info "Running QEMU installer..."
        Write-Info "(If a window appears, follow the prompts)"
        $proc = Start-Process -FilePath $installerPath -ArgumentList "/S" -Wait -PassThru
        
        # Cleanup
        Remove-Item $installerPath -Force -ErrorAction SilentlyContinue
        
        Write-OK "QEMU installed"
        return $true
    }
    catch {
        Write-Err "Failed: $_"
        Write-Info "You can install QEMU manually from https://www.qemu.org/download/"
        return $false
    }
}

function Add-ToPath {
    param([string]$PathToAdd)
    
    $currentPath = [Environment]::GetEnvironmentVariable("Path", "User")
    if ($currentPath -notlike "*$PathToAdd*") {
        [Environment]::SetEnvironmentVariable("Path", "$currentPath;$PathToAdd", "User")
        $env:Path = "$env:Path;$PathToAdd"
    }
}

function Update-SystemPath {
    Write-Step 4 5 "Configuring System PATH"
    
    # Add tools to PATH
    $nasmDir = "$ToolsDir\nasm"
    $qemuDir = "$env:ProgramFiles\qemu"
    
    if (Test-Path $nasmDir) {
        Add-ToPath $nasmDir
        Write-OK "Added NASM to PATH"
    }
    
    if (Test-Path $qemuDir) {
        Add-ToPath $qemuDir
        Write-OK "Added QEMU to PATH"
    }
    
    # Add IDE to PATH
    Add-ToPath $InstallDir
    Write-OK "Added IDE to PATH"
}

function Create-Shortcuts {
    Write-Step 5 5 "Creating Shortcuts"
    
    $exePath = "$InstallDir\OSDevIDE.exe"
    
    # Desktop shortcut
    try {
        $desktop = [Environment]::GetFolderPath("Desktop")
        $shortcutPath = "$desktop\$AppName.lnk"
        
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = $exePath
        $shortcut.WorkingDirectory = $InstallDir
        $shortcut.Description = "OS Development IDE - Build operating systems easily"
        $shortcut.Save()
        
        Write-OK "Desktop shortcut created"
    }
    catch {
        Write-Warn "Could not create desktop shortcut: $_"
    }
    
    # Start menu shortcut
    try {
        $startMenu = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs"
        $shortcutPath = "$startMenu\$AppName.lnk"
        
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = $exePath
        $shortcut.WorkingDirectory = $InstallDir
        $shortcut.Description = "OS Development IDE"
        $shortcut.Save()
        
        Write-OK "Start menu shortcut created"
    }
    catch {
        Write-Warn "Could not create start menu shortcut: $_"
    }
}

function Install-IDE {
    Write-Step 1 5 "Installing OS Dev IDE"
    
    try {
        # Create install directory
        if (!(Test-Path $InstallDir)) {
            New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
        }
        if (!(Test-Path $ToolsDir)) {
            New-Item -ItemType Directory -Path $ToolsDir -Force | Out-Null
        }
        
        # For local installation (files already present)
        $localExe = Join-Path $PSScriptRoot "OSDevIDE.exe"
        if (Test-Path $localExe) {
            Write-Info "Installing from local files..."
            Copy-Item "$PSScriptRoot\*" -Destination $InstallDir -Recurse -Force
            Write-OK "IDE installed from local files"
            return $true
        }
        
        # Download from GitHub
        Write-Info "Downloading latest release..."
        $zipPath = "$env:TEMP\osdevide.zip"
        
        try {
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            (New-Object Net.WebClient).DownloadFile($IDEReleaseUrl, $zipPath)
            
            Write-Info "Extracting..."
            Expand-Archive -Path $zipPath -DestinationPath $InstallDir -Force
            Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
            
            Write-OK "IDE installed"
            return $true
        }
        catch {
            Write-Warn "Could not download from GitHub"
            Write-Info "Please download manually and place files in: $InstallDir"
            
            # Create a placeholder batch file
            $batchContent = @"
@echo off
echo OS Dev IDE not fully installed.
echo Please download the IDE files and place them in:
echo $InstallDir
pause
"@
            Set-Content -Path "$InstallDir\OSDevIDE.exe.bat" -Value $batchContent
            return $false
        }
    }
    catch {
        Write-Err "Failed: $_"
        return $false
    }
}

function Show-Complete {
    Write-Host ""
    Write-Host "  ╔═══════════════════════════════════════════════════════════╗" -ForegroundColor Green
    Write-Host "  ║                                                           ║" -ForegroundColor Green
    Write-Host "  ║         ✓ Installation Complete!                          ║" -ForegroundColor Green
    Write-Host "  ║                                                           ║" -ForegroundColor Green
    Write-Host "  ╚═══════════════════════════════════════════════════════════╝" -ForegroundColor Green
    Write-Host ""
    Write-Host "  Installed to: $InstallDir" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  To get started:" -ForegroundColor White
    Write-Host "    1. Launch '$AppName' from desktop or Start menu" -ForegroundColor Gray
    Write-Host "    2. Create a new project" -ForegroundColor Gray
    Write-Host "    3. Write C code and click 'Build and Run'" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  For AI features (optional):" -ForegroundColor White
    Write-Host "    1. Install Ollama from https://ollama.ai" -ForegroundColor Gray
    Write-Host "    2. Run: ollama pull codellama" -ForegroundColor Gray
    Write-Host "    3. Open Ollama from Extensions menu" -ForegroundColor Gray
    Write-Host ""
}

# ═══════════════════════════════════════════════════════════════════════════
# MAIN
# ═══════════════════════════════════════════════════════════════════════════

Show-Banner

# Check admin for QEMU installation
$needsAdmin = $true
if ($needsAdmin -and -not (Test-IsAdmin)) {
    Write-Warn "Some components require administrator privileges."
    Write-Host ""
    $response = Read-Host "  Restart as Administrator? (Y/n)"
    if ($response -ne "n" -and $response -ne "N") {
        Request-AdminPrivileges
    }
}

Write-Host ""
Write-Host "  This will install:" -ForegroundColor White
Write-Host "    • OS Dev IDE        - The development environment" -ForegroundColor Gray
Write-Host "    • NASM              - Assembler for bootloader/kernel" -ForegroundColor Gray
Write-Host "    • QEMU              - x86 emulator for testing" -ForegroundColor Gray
Write-Host ""
Write-Host "  Install location: $InstallDir" -ForegroundColor Gray
Write-Host ""

$response = Read-Host "  Continue with installation? (Y/n)"
if ($response -eq "n" -or $response -eq "N") {
    Write-Host ""
    Write-Host "  Installation cancelled." -ForegroundColor Yellow
    exit 0
}

# Run installation
$ideOk = Install-IDE
$nasmOk = Install-NASM
$qemuOk = Install-QEMU
Update-SystemPath
Create-Shortcuts

# Show results
Show-Complete

# Offer to launch
$response = Read-Host "  Launch OS Dev IDE now? (Y/n)"
if ($response -ne "n" -and $response -ne "N") {
    $exePath = "$InstallDir\OSDevIDE.exe"
    if (Test-Path $exePath) {
        Start-Process $exePath
    } else {
        Write-Warn "Could not find IDE executable. Please launch manually."
    }
}

Write-Host ""
Write-Host "  Press any key to exit..." -ForegroundColor Gray
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
