# jio installer script for Windows
# Usage: powershell -c "irm https://github.com/iqbqioza/jio/raw/main/cli/install.ps1 | iex"

# Stop on error
$ErrorActionPreference = "Stop"

# Configuration
$Repo = "iqbqioza/jio"
$InstallDir = if ($env:JIO_INSTALL_DIR) { $env:JIO_INSTALL_DIR } else { "$env:USERPROFILE\.jio" }
$BinDir = "$InstallDir\bin"
$BinaryName = "jio.exe"

# Functions
function Write-ErrorMessage {
    param([string]$Message)
    Write-Host "Error: $Message" -ForegroundColor Red
}

function Write-SuccessMessage {
    param([string]$Message)
    Write-Host $Message -ForegroundColor Green
}

function Write-InfoMessage {
    param([string]$Message)
    Write-Host $Message -ForegroundColor Blue
}

function Write-WarningMessage {
    param([string]$Message)
    Write-Host $Message -ForegroundColor Yellow
}

# Detect architecture
function Get-Architecture {
    $arch = $env:PROCESSOR_ARCHITECTURE
    switch ($arch) {
        "AMD64" { return "amd64" }
        "ARM64" { return "arm64" }
        default {
            Write-ErrorMessage "Unsupported architecture: $arch"
            exit 1
        }
    }
}

# Get latest release version from GitHub
function Get-LatestVersion {
    try {
        $apiUrl = "https://api.github.com/repos/$Repo/releases/latest"
        $response = Invoke-RestMethod -Uri $apiUrl -Method Get
        $version = $response.tag_name
        
        if ([string]::IsNullOrEmpty($version)) {
            Write-ErrorMessage "Failed to fetch latest version"
            exit 1
        }
        
        return $version
    }
    catch {
        Write-ErrorMessage "Failed to fetch latest version: $_"
        exit 1
    }
}

# Download binary
function Download-Binary {
    param(
        [string]$Version,
        [string]$Architecture
    )
    
    $downloadUrl = "https://github.com/$Repo/releases/download/$Version/jio-windows-$Architecture.exe"
    $tempFile = [System.IO.Path]::GetTempFileName()
    
    Write-InfoMessage "Downloading jio $Version for windows-$Architecture..."
    
    try {
        # Use WebClient for better download progress
        $webClient = New-Object System.Net.WebClient
        $webClient.DownloadFile($downloadUrl, $tempFile)
        return $tempFile
    }
    catch {
        Write-ErrorMessage "Failed to download binary: $_"
        if (Test-Path $tempFile) {
            Remove-Item $tempFile -Force
        }
        exit 1
    }
}

# Install binary
function Install-Binary {
    param([string]$BinaryPath)
    
    # Create installation directories
    if (!(Test-Path $BinDir)) {
        New-Item -ItemType Directory -Path $BinDir -Force | Out-Null
    }
    
    # Copy binary to installation directory
    $destinationPath = Join-Path $BinDir $BinaryName
    Copy-Item -Path $BinaryPath -Destination $destinationPath -Force
    
    Write-SuccessMessage "jio installed successfully to $destinationPath"
}

# Setup PATH
function Setup-Path {
    $currentPath = [Environment]::GetEnvironmentVariable("Path", [EnvironmentVariableTarget]::User)
    
    if ($currentPath -notlike "*$BinDir*") {
        # Add to user PATH
        $newPath = "$currentPath;$BinDir"
        [Environment]::SetEnvironmentVariable("Path", $newPath, [EnvironmentVariableTarget]::User)
        
        # Update current session PATH
        $env:Path = "$env:Path;$BinDir"
        
        Write-InfoMessage "Added $BinDir to user PATH"
        Write-WarningMessage "You may need to restart your terminal to use jio"
    }
    else {
        Write-InfoMessage "$BinDir is already in your PATH"
    }
}

# Check if running as administrator
function Test-Administrator {
    $currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Main installation process
function Install-Jio {
    Write-InfoMessage "Installing jio..."
    
    # Check if jio is already installed
    $existingBinary = Join-Path $BinDir $BinaryName
    if (Test-Path $existingBinary) {
        Write-WarningMessage "jio is already installed at $existingBinary"
        
        # Read user input with proper validation
        do {
            $response = Read-Host "Do you want to reinstall? (y/N)"
            
            # Handle empty input as 'N'
            if ([string]::IsNullOrWhiteSpace($response)) {
                $response = 'N'
            }
            
            # Convert to uppercase for case-insensitive comparison
            $response = $response.ToUpper()
            
            switch ($response) {
                'Y' {
                    Write-InfoMessage "Proceeding with reinstallation..."
                    $validInput = $true
                }
                'YES' {
                    Write-InfoMessage "Proceeding with reinstallation..."
                    $validInput = $true
                }
                'N' {
                    Write-InfoMessage "Installation cancelled"
                    exit 0
                }
                'NO' {
                    Write-InfoMessage "Installation cancelled"
                    exit 0
                }
                default {
                    Write-ErrorMessage "Invalid input. Please answer 'y' or 'n'"
                    $validInput = $false
                }
            }
        } while (-not $validInput)
    }
    
    # Detect architecture
    $architecture = Get-Architecture
    Write-InfoMessage "Detected architecture: $architecture"
    
    # Get latest version
    $version = Get-LatestVersion
    Write-InfoMessage "Latest version: $version"
    
    # Download binary
    $tempBinary = Download-Binary -Version $version -Architecture $architecture
    
    try {
        # Install binary
        Install-Binary -BinaryPath $tempBinary
        
        # Setup PATH
        Setup-Path
        
        Write-SuccessMessage "Installation complete!"
        Write-InfoMessage "Run 'jio --help' to get started"
    }
    finally {
        # Cleanup
        if (Test-Path $tempBinary) {
            Remove-Item $tempBinary -Force
        }
    }
}

# Check Windows version
$osVersion = [System.Environment]::OSVersion.Version
if ($osVersion.Major -lt 10) {
    Write-WarningMessage "This installer is optimized for Windows 10 and later. Installation may not work correctly on older versions."
}

# Run installation
try {
    Install-Jio
}
catch {
    Write-ErrorMessage "Installation failed: $_"
    exit 1
}