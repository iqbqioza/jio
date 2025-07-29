#!/bin/bash
set -euo pipefail

# jio installer script for Linux/macOS
# Usage: curl -fsSL https://github.com/iqbqioza/jio/raw/main/cli/install.sh | bash

# Configuration
REPO="iqbqioza/jio"
INSTALL_DIR="${JIO_INSTALL_DIR:-$HOME/.jio}"
BIN_DIR="$INSTALL_DIR/bin"
BINARY_NAME="jio"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[0;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Functions
print_error() {
    echo -e "${RED}Error: $1${NC}" >&2
}

print_success() {
    echo -e "${GREEN}$1${NC}"
}

print_info() {
    echo -e "${BLUE}$1${NC}"
}

print_warning() {
    echo -e "${YELLOW}$1${NC}"
}

# Detect OS and architecture
detect_platform() {
    local os=""
    local arch=""
    
    # Detect OS
    case "$(uname -s)" in
        Linux*)     os="linux";;
        Darwin*)    os="darwin";;
        *)          print_error "Unsupported operating system: $(uname -s)"; exit 1;;
    esac
    
    # Detect architecture
    case "$(uname -m)" in
        x86_64)     arch="amd64";;
        aarch64|arm64) arch="arm64";;
        *)          print_error "Unsupported architecture: $(uname -m)"; exit 1;;
    esac
    
    echo "${os}-${arch}"
}

# Get latest release version from GitHub
get_latest_version() {
    local api_url="https://api.github.com/repos/${REPO}/releases/latest"
    local version
    
    if command -v curl >/dev/null 2>&1; then
        version=$(curl -s "${api_url}" | grep '"tag_name":' | sed -E 's/.*"([^"]+)".*/\1/')
    elif command -v wget >/dev/null 2>&1; then
        version=$(wget -qO- "${api_url}" | grep '"tag_name":' | sed -E 's/.*"([^"]+)".*/\1/')
    else
        print_error "Neither curl nor wget is available. Please install one of them."
        exit 1
    fi
    
    if [ -z "$version" ]; then
        print_error "Failed to fetch latest version"
        exit 1
    fi
    
    echo "$version"
}

# Download binary
download_binary() {
    local version="$1"
    local platform="$2"
    local download_url="https://github.com/${REPO}/releases/download/${version}/${BINARY_NAME}-${platform}"
    local temp_file=$(mktemp)
    
    print_info "Downloading jio ${version} for ${platform}..."
    
    if command -v curl >/dev/null 2>&1; then
        curl -fsSL "$download_url" -o "$temp_file" || {
            print_error "Failed to download binary"
            rm -f "$temp_file"
            exit 1
        }
    else
        wget -q "$download_url" -O "$temp_file" || {
            print_error "Failed to download binary"
            rm -f "$temp_file"
            exit 1
        }
    fi
    
    echo "$temp_file"
}

# Install binary
install_binary() {
    local binary_path="$1"
    
    # Create installation directories
    mkdir -p "$BIN_DIR"
    
    # Copy binary to installation directory
    cp "$binary_path" "$BIN_DIR/$BINARY_NAME"
    chmod +x "$BIN_DIR/$BINARY_NAME"
    
    print_success "jio installed successfully to $BIN_DIR/$BINARY_NAME"
}

# Setup PATH
setup_path() {
    local shell_config=""
    local shell_name=$(basename "$SHELL")
    
    case "$shell_name" in
        bash)
            if [ -f "$HOME/.bashrc" ]; then
                shell_config="$HOME/.bashrc"
            elif [ -f "$HOME/.bash_profile" ]; then
                shell_config="$HOME/.bash_profile"
            fi
            ;;
        zsh)
            shell_config="$HOME/.zshrc"
            ;;
        fish)
            shell_config="$HOME/.config/fish/config.fish"
            ;;
        *)
            print_warning "Unknown shell: $shell_name. You'll need to add $BIN_DIR to your PATH manually."
            return
            ;;
    esac
    
    if [ -n "$shell_config" ]; then
        local path_export="export PATH=\"\$PATH:$BIN_DIR\""
        
        if [ "$shell_name" = "fish" ]; then
            path_export="set -gx PATH \$PATH $BIN_DIR"
        fi
        
        if ! grep -q "$BIN_DIR" "$shell_config" 2>/dev/null; then
            echo "" >> "$shell_config"
            echo "# jio path" >> "$shell_config"
            echo "$path_export" >> "$shell_config"
            print_info "Added $BIN_DIR to PATH in $shell_config"
            print_warning "Please run 'source $shell_config' or restart your terminal to use jio"
        else
            print_info "$BIN_DIR is already in your PATH"
        fi
    fi
}

# Main installation process
main() {
    print_info "Installing jio..."
    
    # Check if jio is already installed
    if [ -f "$BIN_DIR/$BINARY_NAME" ]; then
        print_warning "jio is already installed at $BIN_DIR/$BINARY_NAME"
        
        # Read user input with proper handling
        printf "Do you want to reinstall? (y/N) "
        read -r REPLY
        
        # Convert to lowercase for case-insensitive comparison
        REPLY=$(echo "$REPLY" | tr '[:upper:]' '[:lower:]')
        
        # Check if user wants to continue
        case "$REPLY" in
            y|yes)
                print_info "Proceeding with reinstallation..."
                ;;
            n|no|"")
                print_info "Installation cancelled"
                exit 0
                ;;
            *)
                print_error "Invalid input. Please answer 'y' or 'n'"
                exit 1
                ;;
        esac
    fi
    
    # Detect platform
    local platform=$(detect_platform)
    print_info "Detected platform: $platform"
    
    # Get latest version
    local version=$(get_latest_version)
    print_info "Latest version: $version"
    
    # Download binary
    local temp_binary=$(download_binary "$version" "$platform")
    
    # Install binary
    install_binary "$temp_binary"
    
    # Cleanup
    rm -f "$temp_binary"
    
    # Setup PATH
    setup_path
    
    print_success "Installation complete!"
    print_info "Run 'jio --help' to get started"
}

# Run main function
main "$@"