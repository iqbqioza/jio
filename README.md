# jio

Fast, secure, and storage-efficient JavaScript package manager written in C#/.NET 9.

## Features

- **Lightning Fast**: Optimized for speed with parallel downloads and efficient dependency resolution
- **Storage Efficient**: Uses content-addressable storage with hard links to minimize disk usage
- **Secure**: Built-in integrity verification and security audit capabilities
- **Cross-Platform**: Self-contained binaries for Linux (x64/arm64), macOS (x64/arm64), and Windows (x64)
- **Full NPM/Yarn/PNPM Compatibility**: 100% compatible with npm, yarn, and pnpm commands
- **Workspace/Monorepo Support**: Native support for workspaces with topological ordering
- **Lock File Compatibility**: Automatically imports package-lock.json, yarn.lock, or pnpm-lock.yaml
- **Registry Support**: Works with npm registry, private registries, and scoped packages
- **Proxy Support**: Full proxy configuration including authentication

## Installation

Download the latest release for your platform from the [releases page](https://github.com/iqbqioza/jio/releases).

```bash
# Linux/macOS
chmod +x jio
sudo mv jio /usr/local/bin/

# Or add to PATH
export PATH=$PATH:/path/to/jio
```

## Usage

### Initialize a new project

```bash
jio init
# or with defaults
jio init -y
```

### Install dependencies

```bash
# Install all dependencies
jio install

# Install a specific package
jio install express
# or yarn-style
jio add express

# Install as dev dependency
jio install --save-dev typescript

# Install exact version
jio install express@4.18.2 --save-exact
```

### Run scripts

```bash
# Run a script from package.json
jio run build
jio run dev

# Run test script
jio test

# Run start script  
jio start

# Pass arguments to scripts
jio run test -- --coverage
```

### Manage packages

```bash
# Remove a package
jio uninstall lodash
# or
jio remove lodash

# Update packages
jio update              # Update all packages
jio update express      # Update specific package
jio update --latest     # Update to latest versions
jio update --dev        # Update only devDependencies
```

### Command Reference

#### `jio init`
Initialize a new package.json file

Options:
- `--name <name>`: Set package name
- `-y`: Skip prompts and use defaults

#### `jio install [package]`
Install packages (aliases: `i`, `add`)

Arguments:
- `package`: Optional package name with optional version (e.g., `express@4.18.2`)

Options:
- `--save-dev`: Save as dev dependency
- `--save-optional`: Save as optional dependency
- `--save-exact`: Save exact version instead of using caret (^) range
- `-g`: Install globally

#### `jio uninstall <package>`
Remove packages (aliases: `remove`, `rm`, `r`)

Arguments:
- `package`: Package name to uninstall

Options:
- `--save-dev`: Remove from devDependencies
- `--save-optional`: Remove from optionalDependencies
- `-g`: Uninstall globally

#### `jio update [package]`
Update packages to newer versions (aliases: `upgrade`, `up`)

Arguments:
- `package`: Optional package name to update (updates all if not specified)

Options:
- `--latest`: Update to latest version, ignoring version ranges
- `--dev`: Update devDependencies only
- `--all`: Update all dependencies (both dependencies and devDependencies)

#### `jio run [script]`
Run scripts defined in package.json

Arguments:
- `script`: Script name to run (lists available scripts if not specified)
- `--`: Arguments to pass to the script

Options:
- `-r`: Run script in all workspaces recursively
- `--filter <pattern>`: Filter workspaces by name
- `--parallel`: Run scripts in parallel
- `--stream`: Stream output from scripts

Example:
```bash
jio run build
jio run test -- --watch
jio run -r build           # Run build in all workspaces
jio run -r --parallel test # Run tests in parallel across workspaces
```

#### `jio test`
Run the test script (shortcut for `jio run test`)

#### `jio start`
Run the start script (shortcut for `jio run start`)

#### `jio list [pattern]`
List installed packages (alias: `ls`)

Arguments:
- `pattern`: Optional pattern to filter packages

Options:
- `--depth <number>`: Max display depth of the dependency tree (default: 0)
- `-g`: List global packages
- `--json`: Output in JSON format
- `--parseable`: Output parseable results

Example:
```bash
jio list           # List direct dependencies
jio list --depth 2 # Show dependencies up to 2 levels deep
jio list express   # Filter packages containing "express"
```

#### `jio outdated`
Check for outdated packages

Options:
- `-g`: Check global packages
- `--json`: Output in JSON format
- `--depth <number>`: Max depth for checking

Example:
```bash
jio outdated       # Check all outdated packages
jio outdated --json # Output results as JSON
```

#### `jio exec <command>`
Execute a command from installed packages

Arguments:
- `command`: Command to execute
- `--`: Arguments to pass to the command

Options:
- `-p`: Execute from package
- `--call`: Script to execute from package.json

Example:
```bash
jio exec tsc       # Execute TypeScript compiler
jio exec eslint -- --fix src/  # Run eslint with arguments
```

#### `jio audit`
Security audit for installed packages

Options:
- `--fix`: Automatically install compatible updates to fix vulnerabilities
- `--json`: Output in JSON format
- `--audit-level <level>`: Minimum level to exit with non-zero code (low, moderate, high, critical)
- `--production`: Only audit production dependencies
- `--dev`: Only audit dev dependencies

Example:
```bash
jio audit              # Check for vulnerabilities
jio audit --fix        # Fix vulnerabilities automatically
jio audit --json       # Output as JSON
```

#### `jio link [package]`
Create a symbolic link from the global or local folder

Arguments:
- `package`: Optional package to link (if empty, links current package)

Options:
- `-g`: Link globally

Example:
```bash
jio link               # Link current package
jio link -g            # Link current package globally
jio link express       # Link express from global to current project
```

#### `jio publish`
Publish a package to the registry

Options:
- `--tag <tag>`: Tag to publish under (default: latest)
- `--access <level>`: Access level (public or restricted)
- `--dry-run`: Perform a dry run without publishing
- `--otp <code>`: One-time password for 2FA
- `--registry <url>`: Registry URL

Example:
```bash
jio publish            # Publish package
jio publish --dry-run  # Test publish without uploading
jio publish --tag beta # Publish with beta tag
```

#### `jio search <query>`
Search for packages

Arguments:
- `query`: Search query

Options:
- `--json`: Output in JSON format
- `--long`: Show extended information
- `--parseable`: Output parseable results
- `--registry <url>`: Registry URL

Example:
```bash
jio search express     # Search for express packages
jio search --long react # Show detailed results
```

#### `jio view <package> [field]`
View package information (aliases: `info`, `show`)

Arguments:
- `package`: Package name with optional version
- `field`: Optional specific field to display

Options:
- `--json`: Output in JSON format

Example:
```bash
jio view express              # View latest express info
jio view express@4.18.2       # View specific version
jio view express dependencies # View only dependencies
```

## Architecture

jio uses a content-addressable store similar to pnpm, storing packages once and creating hard links to `node_modules`. This approach significantly reduces disk usage when working with multiple projects.

### Key Components

- **Content-Addressable Store**: Packages stored by content hash in `~/.jio/store`
- **Hard Links**: Efficient linking from store to `node_modules` (falls back to copying on Windows)
- **Parallel Downloads**: Concurrent package downloads with configurable limits
- **Lock File**: `jio-lock.json` for reproducible installs (auto-imports npm/yarn/pnpm lock files)
- **Integrity Verification**: All packages are verified using SHA-512 hashes
- **.npmrc Support**: Reads configuration from project, user, and global .npmrc files
- **Scoped Packages**: Support for @scope/package with per-scope registries
- **Authentication**: Bearer token authentication for private registries
- **Package Cache**: Downloaded packages are cached to speed up subsequent installs
- **Direct Execution**: Run scripts and executables without prefixing with `jio run`
- **Workspace Support**: Native monorepo support with topological dependency ordering
- **Global Packages**: Full support for global package installation with binary linking
- **Security Audit**: Built-in vulnerability scanning with automatic fix capabilities

### Directory Structure

```
~/.jio/
├── store/           # Content-addressable storage
│   ├── ab/
│   │   └── cd/
│   │       └── abcd.../  # Package contents
├── cache/           # HTTP cache and temporary files
├── global/          # Global packages
│   ├── node_modules/  # Global package installations
│   ├── bin/         # Global executable links
│   └── package.json # Global package manifest
└── links/           # Package link metadata

project/
├── node_modules/    # Hard links to store
├── package.json     # Package manifest
├── jio-lock.json    # Lock file for reproducible installs
└── workspaces/      # Workspace packages (monorepo)
    ├── package-a/
    └── package-b/
```

## Building from Source

Requirements:
- .NET 9.0 SDK

```bash
# Clone the repository
git clone https://github.com/iqbqioza/jio.git
cd jio

# Build
dotnet build

# Run tests
dotnet test

# Publish for specific runtime
dotnet publish src/Jio.CLI/Jio.CLI.csproj -c Release -r linux-x64 --self-contained false -p:PublishSingleFile=true
```

Available runtime identifiers:
- `linux-x64`: Linux x64
- `linux-arm64`: Linux ARM64
- `osx-x64`: macOS x64
- `osx-arm64`: macOS ARM64 (Apple Silicon)
- `win-x64`: Windows x64

## Configuration

jio supports .npmrc configuration files for compatibility with npm. Configuration is loaded from:

1. Project-level: `.npmrc` in the current directory
2. User-level: `~/.npmrc`
3. Global: `/etc/npmrc` (Linux/macOS) or `%APPDATA%\npm\etc\npmrc` (Windows)

### Supported .npmrc Options

```ini
# Registry configuration
registry=https://registry.npmjs.org/

# Scoped registries
@mycompany:registry=https://npm.mycompany.com/

# Authentication
//registry.npmjs.org/:_authToken=npm_token_here
//npm.mycompany.com/:_authToken=company_token_here

# Proxy settings
proxy=http://proxy.example.com:8080
https-proxy=https://proxy.example.com:8443
no-proxy=localhost,127.0.0.1,.internal.company.com

# SSL/TLS settings
strict-ssl=true
ca=/path/to/ca-certificate.pem

# Other settings
maxsockets=10
user-agent=jio/1.0.0
```

### Using Private Registries

To use a private registry, create a `.npmrc` file:

```ini
@mycompany:registry=https://npm.mycompany.com/
//npm.mycompany.com/:_authToken=${NPM_TOKEN}
```

Then set the environment variable:
```bash
export NPM_TOKEN=your_auth_token
jio install @mycompany/private-package
```

### Workspace Configuration

For monorepo/workspace support, add a `workspaces` field to your root `package.json`:

```json
{
  "name": "my-monorepo",
  "workspaces": [
    "packages/*",
    "apps/*"
  ]
}
```

Then use workspace commands:
```bash
jio install              # Install all workspace dependencies
jio run -r build         # Run build in all workspaces
jio run -r --parallel test  # Run tests in parallel
```

## Development

### Running locally

```bash
# Run the CLI directly
dotnet run --project src/Jio.CLI/Jio.CLI.csproj -- init -y

# Run with debugging
dotnet run --project src/Jio.CLI/Jio.CLI.csproj -- install express
```

### Testing

The project includes comprehensive unit tests:

```bash
# Run all tests
dotnet test

# Run tests with coverage
dotnet test --collect:"XPlat Code Coverage"

# Run specific test
dotnet test --filter "FullyQualifiedName~IntegrityVerifier"
```

## License

MIT License - see [LICENSE](LICENSE) file for details.