# ═══════════════════════════════════════════════════════════════════════════
# OS Dev IDE - Dependency Installer
# ═══════════════════════════════════════════════════════════════════════════
# 
# This script installs:
#   - NASM (Netwide Assembler)
#   - QEMU (x86 Emulator)
#   - Adds them to PATH
#
# Usage:
#   .\install_dependencies.ps1                    # Install all
#   .\install_dependencies.ps1 -InstallNasm $true # Install only NASM
#   .\install_dependencies.ps1 -InstallQemu $true # Install only QEMU
#
# ═══════════════════════════════════════════════════════════════════════════

param(
    [bool]$InstallNasm = $true,
    [bool]$InstallQemu = $true,
    [string]$InstallDir = "$env:ProgramFiles\OSDevIDE\tools"
)

$ErrorActionPreference = "Stop"

# URLs for downloads
$NasmVersion = "2.16.01"
$NasmUrl = "https://www.nasm.us/pub/nasm/releasebuilds/$NasmVersion/win64/nasm-$NasmVersion-win64.zip"

$QemuVersion = "8.2.0"
$QemuUrl = "https://qemu.weilnetz.de/w64/2024/qemu-w64-setup-20240423.exe"

# Alternate QEMU from GitHub releases
$QemuAltUrl = "https://github.com/qemu/qemu/releases/download/v8.2.0/qemu-w64-setup-20240123.exe"

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Cyan
    Write-Host " $Message" -ForegroundColor White
    Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Cyan
}

function Write-Success {
    param([string]$Message)
    Write-Host "  ✓ $Message" -ForegroundColor Green
}

function Write-Info {
    param([string]$Message)
    Write-Host "  → $Message" -ForegroundColor Gray
}

function Write-Warning {
    param([string]$Message)
    Write-Host "  ⚠ $Message" -ForegroundColor Yellow
}

function Write-Error {
    param([string]$Message)
    Write-Host "  ✗ $Message" -ForegroundColor Red
}

function Test-Admin {
    $currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Add-ToPath {
    param([string]$PathToAdd)
    
    $currentPath = [Environment]::GetEnvironmentVariable("Path", "Machine")
    if ($currentPath -notlike "*$PathToAdd*") {
        [Environment]::SetEnvironmentVariable("Path", "$currentPath;$PathToAdd", "Machine")
        $env:Path = "$env:Path;$PathToAdd"
        Write-Success "Added to PATH: $PathToAdd"
    } else {
        Write-Info "Already in PATH: $PathToAdd"
    }
}

function Test-CommandExists {
    param([string]$Command)
    $null = Get-Command $Command -ErrorAction SilentlyContinue
    return $?
}

function Install-Nasm {
    Write-Step "Installing NASM $NasmVersion"
    
    # Check if already installed
    if (Test-CommandExists "nasm") {
        $version = & nasm --version 2>&1
        Write-Info "NASM already installed: $version"
        return $true
    }
    
    $nasmDir = "$InstallDir\nasm"
    $zipFile = "$env:TEMP\nasm.zip"
    
    try {
        # Create directory
        if (!(Test-Path $nasmDir)) {
            New-Item -ItemType Directory -Path $nasmDir -Force | Out-Null
        }
        
        # Download
        Write-Info "Downloading from nasm.us..."
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        
        $webClient = New-Object System.Net.WebClient
        $webClient.DownloadFile($NasmUrl, $zipFile)
        Write-Success "Downloaded NASM"
        
        # Extract
        Write-Info "Extracting..."
        Expand-Archive -Path $zipFile -DestinationPath $nasmDir -Force
        
        # Move files from subfolder
        $subFolder = Get-ChildItem $nasmDir -Directory | Select-Object -First 1
        if ($subFolder) {
            Get-ChildItem $subFolder.FullName | Move-Item -Destination $nasmDir -Force
            Remove-Item $subFolder.FullName -Force
        }
        Write-Success "Extracted to $nasmDir"
        
        # Add to PATH
        Add-ToPath $nasmDir
        
        # Verify
        $env:Path = "$env:Path;$nasmDir"
        if (Test-Path "$nasmDir\nasm.exe") {
            Write-Success "NASM installed successfully!"
            return $true
        } else {
            Write-Error "NASM executable not found"
            return $false
        }
    }
    catch {
        Write-Error "Failed to install NASM: $_"
        return $false
    }
    finally {
        if (Test-Path $zipFile) { Remove-Item $zipFile -Force }
    }
}

function Install-Qemu {
    Write-Step "Installing QEMU"
    
    # Check if already installed
    if (Test-CommandExists "qemu-system-i386") {
        $version = & qemu-system-i386 --version 2>&1 | Select-Object -First 1
        Write-Info "QEMU already installed: $version"
        return $true
    }
    
    $installerFile = "$env:TEMP\qemu-setup.exe"
    $qemuDir = "$env:ProgramFiles\qemu"
    
    try {
        # Download
        Write-Info "Downloading QEMU (this may take a while, ~200MB)..."
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        
        $webClient = New-Object System.Net.WebClient
        try {
            $webClient.DownloadFile($QemuUrl, $installerFile)
        }
        catch {
            Write-Warning "Primary download failed, trying alternate..."
            $webClient.DownloadFile($QemuAltUrl, $installerFile)
        }
        Write-Success "Downloaded QEMU installer"
        
        # Run silent installer
        Write-Info "Running installer (silent mode)..."
        $process = Start-Process -FilePath $installerFile -ArgumentList "/S" -Wait -PassThru
        
        if ($process.ExitCode -eq 0) {
            Write-Success "QEMU installed"
        } else {
            # Try with user interaction
            Write-Warning "Silent install failed, launching interactive installer..."
            Start-Process -FilePath $installerFile -Wait
        }
        
        # Find QEMU installation
        $possiblePaths = @(
            "$env:ProgramFiles\qemu",
            "${env:ProgramFiles(x86)}\qemu",
            "C:\Program Files\qemu"
        )
        
        foreach ($path in $possiblePaths) {
            if (Test-Path "$path\qemu-system-i386.exe") {
                $qemuDir = $path
                break
            }
        }
        
        # Add to PATH
        if (Test-Path $qemuDir) {
            Add-ToPath $qemuDir
            Write-Success "QEMU installed successfully!"
            return $true
        } else {
            Write-Warning "QEMU directory not found. You may need to add it to PATH manually."
            return $false
        }
    }
    catch {
        Write-Error "Failed to install QEMU: $_"
        return $false
    }
    finally {
        if (Test-Path $installerFile) { Remove-Item $installerFile -Force }
    }
}

function Install-Ollama {
    Write-Step "Installing Ollama (Optional - for AI features)"
    
    # Check if already installed
    if (Test-CommandExists "ollama") {
        Write-Info "Ollama already installed"
        return $true
    }
    
    $installerUrl = "https://ollama.ai/download/OllamaSetup.exe"
    $installerFile = "$env:TEMP\OllamaSetup.exe"
    
    try {
        Write-Info "Downloading Ollama..."
        $webClient = New-Object System.Net.WebClient
        $webClient.DownloadFile($installerUrl, $installerFile)
        
        Write-Info "Running installer..."
        Start-Process -FilePath $installerFile -Wait
        
        Write-Success "Ollama installed!"
        Write-Info "Run 'ollama pull codellama' to download the AI model"
        return $true
    }
    catch {
        Write-Warning "Could not install Ollama: $_"
        Write-Info "You can install it later from https://ollama.ai"
        return $false
    }
}

# ═══════════════════════════════════════════════════════════════════════════
# MAIN
# ═══════════════════════════════════════════════════════════════════════════

Write-Host ""
Write-Host "╔═══════════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║         OS Dev IDE - Dependency Installer                     ║" -ForegroundColor Cyan
Write-Host "╚═══════════════════════════════════════════════════════════════╝" -ForegroundColor Cyan

# Check for admin rights
if (!(Test-Admin)) {
    Write-Error "This script requires administrator privileges."
    Write-Info "Please run PowerShell as Administrator and try again."
    exit 1
}

# Create install directory
if (!(Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    Write-Info "Created install directory: $InstallDir"
}

$nasmOk = $true
$qemuOk = $true

# Install NASM
if ($InstallNasm) {
    $nasmOk = Install-Nasm
}

# Install QEMU
if ($InstallQemu) {
    $qemuOk = Install-Qemu
}

# Summary
Write-Step "Installation Complete"

if ($InstallNasm) {
    if ($nasmOk) {
        Write-Success "NASM: Installed"
    } else {
        Write-Error "NASM: Failed"
    }
}

if ($InstallQemu) {
    if ($qemuOk) {
        Write-Success "QEMU: Installed"
    } else {
        Write-Error "QEMU: Failed"
    }
}

Write-Host ""
Write-Info "You may need to restart your terminal for PATH changes to take effect."
Write-Host ""

# Optional: Install Ollama
$response = Read-Host "Would you like to install Ollama for AI features? (y/N)"
if ($response -eq "y" -or $response -eq "Y") {
    Install-Ollama
}

Write-Host ""
Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host " Setup complete! You can now run OS Dev IDE." -ForegroundColor Green
Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host ""

# Keep window open if running interactively
if ($Host.Name -eq "ConsoleHost") {
    Write-Host "Press any key to exit..."
    $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
}
