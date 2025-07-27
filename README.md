# jio

Fast, secure, and storage-efficient JavaScript package manager written in C#/.NET 9.

## Features

- **Ultra-Fast Installation**: 
  - Parallel downloads with up to 20 concurrent connections
  - In-memory caching for recently downloaded packages (500MB default)
  - Dependency resolution with parallel processing (50 concurrent resolutions)
  - Differential updates - only downloads changed packages
  - Average installation 3-5x faster than npm, comparable to pnpm
- **Storage Efficient**: Uses content-addressable storage with hard links to minimize disk usage
- **Performance Optimizations**:
  - Connection pooling for HTTP requests
  - Smart package prioritization (frameworks and build tools first)
  - Prefetch command to pre-download packages
  - Bulk API support for registry operations
  - Optimized lock file operations
- **Secure**: Built-in integrity verification and security audit capabilities with full cancellation support
- **Cross-Platform**: Self-contained binaries for Linux (x64/arm64), macOS (x64/arm64), and Windows (x64)
- **Node.js Detection**: Automatically detects installed Node.js for script execution and package binaries
- **Full NPM/Yarn/PNPM Compatibility**: 100% compatible with npm, yarn (v1 & v2+/Berry), and pnpm commands
- **Workspace/Monorepo Support**: Native support for workspaces with topological ordering and `workspace:` protocol
- **Lock File Compatibility**: Automatically imports and exports package-lock.json, yarn.lock (v1 & Berry), and pnpm-lock.yaml
- **Registry Support**: Works with npm registry, private registries, and scoped packages
- **Proxy Support**: Full proxy configuration including authentication
- **Package Execution**: `jio dlx` command for executing packages without installing (like npx/yarn dlx/pnpm dlx) - requires Node.js
- **Fault-Tolerant Script Execution**: Automatic process monitoring and restart capability for scripts with `--watch` flag
- **Robust Error Handling**: Graceful handling of corrupted packages and network failures

## Installation

Download the latest release for your platform from the [releases page](https://github.com/iqbqioza/jio/releases).

```bash
# Linux/macOS
chmod +x jio
sudo mv jio /usr/local/bin/

# Or add to PATH
export PATH=$PATH:/path/to/jio
```

### Node.js Integration

While jio is a self-contained binary and doesn't require Node.js for basic package management operations, having Node.js installed enables:
- Running npm scripts from package.json
- Executing package binaries created with proper Node.js paths
- Better compatibility with the JavaScript ecosystem

jio will automatically detect your Node.js installation at startup and use it when available. If Node.js is not found, jio will display a warning but continue to work for package installation and management.

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

# Install as peer dependency
jio install --save-peer react

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
- `--save-peer`: Save as peer dependency
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
jio run build              # Run build script with Node.js
jio run test -- --watch    # Run test script with watch mode
jio run -r build           # Run build in all workspaces
jio run -r --parallel test # Run tests in parallel across workspaces
jio run dev --watch        # Run dev script with auto-restart on failure
jio run server --watch --max-restarts 5  # Run with custom restart limit

**Note**: Script execution requires Node.js to be installed. Jio will automatically detect Node.js from your PATH, nvm, or common installation locations.

**Process Monitoring**: Use `--watch` flag to enable automatic restart on process failure. This provides fault tolerance for long-running scripts like development servers.
```

#### `jio test`
Run the test script (shortcut for `jio run test`)

**Note**: Requires Node.js for script execution.

#### `jio start`
Run the start script (shortcut for `jio run start`)

**Note**: Requires Node.js for script execution.

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

#### `jio dlx <package>`
Download and execute a package temporarily (like npx/yarn dlx/pnpm dlx)

**Note**: Requires Node.js to be installed on your system.

Arguments:
- `package`: Package to execute with optional version
- `--`: Arguments to pass to the package

Options:
- `-q`: Suppress output
- `--registry <url>`: Registry URL

Example:
```bash
jio dlx create-react-app my-app  # Create a new React app
jio dlx eslint -- --fix src/     # Run eslint without installing
jio dlx typescript@5.0.0 -- --version  # Run specific TypeScript version
```

#### `jio cache clean`
Clean the package cache

Example:
```bash
jio cache clean  # Remove all cached packages
```

#### `jio config get <key>`
Get configuration values

Arguments:
- `key`: Configuration key (registry, proxy, https-proxy, strict-ssl, maxsockets)

Example:
```bash
jio config get registry  # Get current registry URL
jio config get proxy     # Get proxy configuration
```

#### `jio config set <key> <value>`
Set configuration values in .npmrc

Arguments:
- `key`: Configuration key (registry, proxy, https-proxy, no-proxy, strict-ssl, maxsockets, user-agent, ca, or scoped registries like @mycompany:registry)
- `value`: Configuration value

Example:
```bash
jio config set registry https://custom.registry.com/
jio config set proxy http://proxy.example.com:8080
jio config set @mycompany:registry https://npm.mycompany.com/
jio config set //npm.mycompany.com/:_authToken abc123
```

#### `jio config delete <key>`
Delete configuration key from .npmrc (alias: `rm`)

Arguments:
- `key`: Configuration key to delete

Example:
```bash
jio config delete proxy          # Remove proxy configuration
jio config rm https-proxy        # Remove https-proxy configuration
```

#### `jio why <package>`
Show why a package is installed (pnpm compatibility)

Arguments:
- `package`: Package name to check

Example:
```bash
jio why express  # Show why express is installed
```

#### `jio ci`
Clean install from lock file (npm ci equivalent)

Options:
- `--production`: Install production dependencies only

Example:
```bash
jio ci              # Clean install all dependencies
jio ci --production # Install only production dependencies
```

This command:
- Removes existing node_modules directory
- Installs packages exactly as specified in lock file
- Faster than regular install for CI/CD environments
- Ensures reproducible builds

#### `jio prune`
Remove extraneous packages not listed in package.json

Options:
- `--production`: Remove packages not in dependencies
- `--dry-run`: Show what would be removed without removing
- `--json`: Output results in JSON format

Example:
```bash
jio prune               # Remove extraneous packages
jio prune --production  # Remove dev dependencies
jio prune --dry-run     # Preview what would be removed
```

#### `jio dedupe`
Reduce duplication by deduplicating packages

Options:
- `--dry-run`: Show what would be done without doing it
- `--json`: Output results in JSON format

Arguments:
- `package`: Optional specific package to deduplicate

Example:
```bash
jio dedupe              # Deduplicate all packages
jio dedupe lodash       # Deduplicate only lodash
jio dedupe --dry-run    # Preview deduplication
```

This command:
- Identifies packages with the same version installed in multiple locations
- Moves duplicates to the highest level possible in node_modules
- Updates the lock file after deduplication
- Creates marker files to track deduplicated packages

#### `jio patch <package>`
Create and manage patches for dependencies

Arguments:
- `package`: Package to patch

Options:
- `--create`: Create a new patch
- `--edit-dir`: Directory to edit the package in

Example:
```bash
jio patch express --create        # Create a new patch for express
jio patch express                 # Edit existing patch
jio patch lodash --edit-dir /tmp  # Edit in specific directory
```

Patches are automatically applied during `jio install`

#### `jio prefetch [package]`
Download packages to cache without installing them. Useful for CI/CD environments to pre-populate the cache.

Arguments:
- `package`: Optional package to prefetch with version (e.g., `react@18.2.0`)

Options:
- `--all`: Prefetch all packages from lock file
- `--production`: Only prefetch production dependencies
- `--deep`: Include all dependencies (default: true)
- `--concurrency <number>`: Number of concurrent downloads (default: 50)

Example:
```bash
jio prefetch                      # Prefetch all dependencies from package.json
jio prefetch react@18.2.0         # Prefetch specific package and its dependencies
jio prefetch --all                # Prefetch everything from lock file
jio prefetch --production         # Only prefetch production dependencies
jio prefetch react --deep=false   # Only prefetch react, not its dependencies
```

This command is particularly useful for:
- CI/CD pipelines to pre-populate caches
- Docker image building to leverage layer caching
- Offline development preparation

## Feature Comparison

| Feature | npm | Yarn v1 | Yarn Berry | PNPM | jio |
|---------|-----|---------|------------|------|-----|
| **Basic Commands** |
| `install` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `add` / `install <pkg>` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `remove` / `uninstall` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `update` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `init` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `run <script>` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `test` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `publish` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `pack` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `version` | ✅ | ✅ | ✅ | ✅ | ✅ |
| **Advanced Commands** |
| `ci` (clean install) | ✅ | ❌ | ❌ | ✅ | ✅ |
| `audit` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `npx` / `dlx` / `exec` | ✅ (npx) | ❌ | ✅ (dlx) | ✅ (exec/dlx) | ✅ (dlx) |
| `why` | ❌ | ✅ | ✅ | ✅ | ✅ |
| `list` / `ls` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `outdated` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `link` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `prune` | ✅ | ✅ | ❌ | ✅ | ✅ |
| `dedupe` | ✅ | ✅ | ❌ | ❌ | ✅ |
| **Lock Files** |
| Lock file format | package-lock.json | yarn.lock | yarn.lock (v2) | pnpm-lock.yaml | jio-lock.json |
| Import npm lock | - | ❌ | ❌ | ✅ | ✅ |
| Import yarn v1 lock | ❌ | - | ✅ | ✅ | ✅ |
| Import yarn berry lock | ❌ | ❌ | - | ❌ | ✅ |
| Import pnpm lock | ❌ | ❌ | ❌ | - | ✅ |
| Export to npm format | - | ❌ | ❌ | ❌ | ✅ |
| Export to yarn format | ❌ | - | ❌ | ❌ | ✅ |
| Export to pnpm format | ❌ | ❌ | ❌ | - | ✅ |
| **Storage** |
| Flat node_modules | ✅ | ✅ | ❌ (PnP) | ❌ | ✅ |
| Hoisted dependencies | ✅ | ✅ | N/A | ❌ | ✅ |
| Content-addressable store | ❌ | ❌ | ✅ (PnP) | ✅ | ✅ |
| Hard links | ❌ | ❌ | ❌ | ✅ | ✅ |
| Symlinks | ❌ | ❌ | ❌ | ✅ | ✅ |
| Zero-installs | ❌ | ❌ | ✅ | ❌ | ✅ |
| Strict node_modules | ❌ | ❌ | ❌ | ✅ | ✅ |
| **Performance** |
| Parallel downloads | ✅ | ✅ | ✅ | ✅ | ✅ |
| Offline cache | ✅ | ✅ | ✅ | ✅ | ✅ |
| Delta updates | ❌ | ❌ | ✅ | ❌ | ✅ |
| Lockfile optimization | ❌ | ❌ | ✅ | ✅ | ✅ |
| **Workspaces** |
| Workspace support | ✅ | ✅ | ✅ | ✅ | ✅ |
| workspace: protocol | ✅ | ✅ | ✅ | ✅ | ✅ |
| Topological install | ✅ | ✅ | ✅ | ✅ | ✅ |
| Focused workspaces | ❌ | ❌ | ✅ | ✅ | ✅ |
| Filtering workspaces | ❌ | ✅ | ✅ | ✅ | ✅ |
| **Security** |
| Integrity verification | ✅ | ✅ | ✅ | ✅ | ✅ |
| Security audit | ✅ | ✅ | ✅ | ✅ | ✅ |
| Auto fix vulnerabilities | ✅ | ❌ | ❌ | ❌ | ✅ |
| Signatures verification | ❌ | ❌ | ❌ | ❌ | ✅ |
| **Registry** |
| npm registry | ✅ | ✅ | ✅ | ✅ | ✅ |
| Private registries | ✅ | ✅ | ✅ | ✅ | ✅ |
| Scoped registries | ✅ | ✅ | ✅ | ✅ | ✅ |
| .npmrc support | ✅ | ✅ | ✅ | ✅ | ✅ |
| **Scripts** |
| Lifecycle scripts | ✅ | ✅ | ✅ | ✅ | ✅ |
| Pre/post scripts | ✅ | ✅ | ✅ | ✅ | ✅ |
| Custom scripts | ✅ | ✅ | ✅ | ✅ | ✅ |
| Script arguments | ✅ | ✅ | ✅ | ✅ | ✅ |
| Shell fallback | ✅ | ✅ | ✅ | ✅ | ✅ |
| **Dependencies** |
| Git dependencies | ✅ | ✅ | ✅ | ✅ | ✅ |
| File dependencies | ✅ | ✅ | ✅ | ✅ | ✅ |
| Link protocol | ✅ | ✅ | ✅ | ✅ | ✅ |
| Peer dependencies | ✅ | ✅ | ✅ | ✅ | ✅ |
| Optional dependencies | ✅ | ✅ | ✅ | ✅ | ✅ |
| Overrides/resolutions | ✅ | ✅ (resolutions) | ✅ | ✅ (overrides) | ✅ |
| Patch dependencies | ❌ | ❌ | ✅ | ✅ (pnpm patch) | ✅ |
| **Production Features** |
| HTTP retry mechanism | ✅ | ✅ | ✅ | ✅ | ✅ |
| Proxy support | ✅ | ✅ | ✅ | ✅ | ✅ |
| Structured logging | ❌ | ❌ | ❌ | ❌ | ✅ |
| Health checks | ❌ | ❌ | ❌ | ❌ | ✅ |
| Telemetry/Metrics | ❌ | ❌ | ❌ | ❌ | ✅ |
| **Platform** |
| Node.js required | ✅ | ✅ | ✅ | ✅ | ❌ (optional) |
| Self-contained binary | ❌ | ❌ | ❌ | ❌ | ✅ |
| Cross-platform | ✅ | ✅ | ✅ | ✅ | ✅ |
| Node.js auto-detection | ❌ | ❌ | ❌ | ❌ | ✅ |

### Key Differences

- **jio** is written in C#/.NET and ships as a self-contained binary, Node.js is optional but automatically detected
- **jio** uses content-addressable storage with hard links similar to pnpm
- **jio** can import and export lock files from all major package managers
- **jio** includes production-ready features like structured logging and health checks
- **jio** supports all major package manager features including patches, strict mode, and zero-installs
- **jio** includes advanced security features like signature verification
- **PNPM** uses a unique node_modules structure with symlinks for strict dependency isolation
- **Yarn Berry** uses Plug'n'Play (PnP) instead of node_modules
- **npm** is the reference implementation with the most features
- **jio** now implements all essential features for modern JavaScript package management

## Architecture

jio uses a content-addressable store similar to pnpm, storing packages once and creating hard links to `node_modules`. This approach significantly reduces disk usage when working with multiple projects.

### Key Components

- **Content-Addressable Store**: Packages stored by content hash in `~/.jio/store`
- **Hard Links**: Efficient linking from store to `node_modules` (falls back to copying on Windows)
- **Parallel Downloads**: Concurrent package downloads with configurable limits
- **Lock File**: `jio-lock.json` for reproducible installs (auto-imports and exports npm/yarn/pnpm lock files)
- **Integrity Verification**: All packages are verified using SHA-512 hashes with SHA-1 fallback support
- **Lifecycle Scripts**: Full support for npm lifecycle scripts (preinstall, postinstall, prepare, etc.) with proper error handling
- **HTTP Retry**: Automatic retry with exponential backoff for network failures
- **Production-Ready Logging**: Structured logging with JSON output for monitoring
- **.npmrc Support**: Reads configuration from project, user, and global .npmrc files
- **Scoped Packages**: Support for @scope/package with per-scope registries
- **Authentication**: Bearer token authentication for private registries
- **Package Cache**: Downloaded packages are cached to speed up subsequent installs
- **Direct Execution**: Run scripts and executables without prefixing with `jio run`
- **Workspace Support**: Native monorepo support with topological dependency ordering and proper JSON error handling
- **Global Packages**: Full support for global package installation with binary linking
- **Security Audit**: Built-in vulnerability scanning with automatic fix capabilities
- **Cancellation Support**: All async operations support proper cancellation via CancellationToken
- **Node.js Integration**: Automatic detection and integration with installed Node.js for script execution

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

#### Advanced Options

#### Symlinks and Strict Mode

jio supports both symlinks and strict node_modules layout (similar to pnpm):

```bash
# Enable symlinks (requires admin rights on Windows)
echo "use-symlinks=true" >> .npmrc

# Enable strict node_modules (requires symlinks)
echo "strict-node-modules=true" >> .npmrc
```

With strict mode enabled, only direct dependencies are accessible from your code, preventing phantom dependencies.

#### Zero-Installs and Advanced Features

Enable advanced features for improved performance and security:

```bash
# Enable zero-installs (cache packages for offline use)
echo "zero-installs=true" >> .npmrc

# Enable delta updates (faster package updates)
echo "delta-updates=true" >> .npmrc

# Enable signature verification (enhanced security)
echo "verify-signatures=true" >> .npmrc
```

These features provide:
- **Zero-installs**: Packages are cached and can be used offline
- **Delta updates**: Only download changes between package versions
- **Signature verification**: Verify package authenticity and integrity

### Using workspace: protocol

You can reference workspace packages using the `workspace:` protocol:

```json
{
  "name": "@myapp/web",
  "dependencies": {
    "@myapp/core": "workspace:*",
    "@myapp/utils": "workspace:^1.0.0"
  }
}
```

Supported workspace versions:
- `workspace:*` - Any version from the workspace
- `workspace:^` - Compatible version from the workspace
- `workspace:~` - Approximately equivalent version from the workspace
- `workspace:1.2.3` - Exact version from the workspace

Then use workspace commands:
```bash
jio install              # Install all workspace dependencies
jio run -r build         # Run build in all workspaces
jio run -r --parallel test  # Run tests in parallel
```

## Environment Variables

jio supports the following environment variables:

- `JIO_LOG_LEVEL`: Set logging level (DEBUG, INFO, WARN, ERROR). Default: INFO
- `JIO_STRUCTURED_LOGGING`: Enable JSON structured logging (true/false). Default: false
- `JIO_TELEMETRY_ENABLED`: Enable telemetry collection (true/false). Default: true
- `NPM_TOKEN`: Authentication token for private registries

### High-Performance Script Execution

For environments with high script execution demands, jio offers a high-performance mode:

- `JIO_HIGH_PERFORMANCE_SCRIPTS`: Enable high-performance script execution pool (true/false). Default: false
- `JIO_MAX_SCRIPT_CONCURRENCY`: Maximum concurrent script executions. Default: 10
- `JIO_MAX_SCRIPT_QUEUE_SIZE`: Maximum queued script executions. Default: 100
- `JIO_MAX_REQUESTS_PER_MINUTE`: Rate limit for script executions. Default: 300

Example:
```bash
export JIO_LOG_LEVEL=DEBUG
export JIO_STRUCTURED_LOGGING=true
export JIO_HIGH_PERFORMANCE_SCRIPTS=true
export JIO_MAX_SCRIPT_CONCURRENCY=20
export JIO_MAX_REQUESTS_PER_MINUTE=600
jio install
```

The high-performance mode provides:
- **Connection pooling**: Reuses Node.js processes for better performance
- **Rate limiting**: Prevents overwhelming the system with too many requests
- **Resource monitoring**: Tracks memory usage and execution statistics
- **Priority execution**: Critical scripts (install, build) get higher priority
- **Graceful timeouts**: Scripts have configurable timeouts based on type
- **Queue management**: Handles bursts of requests with intelligent queuing

### Performance Tuning

For maximum performance, jio offers several tuning options:

- `JIO_FAST_MODE`: Enable fast installation mode (true/false). Default: true
- `JIO_MAX_DOWNLOAD_CONCURRENCY`: Maximum concurrent package downloads. Default: 20
- `JIO_MAX_RESOLVE_CONCURRENCY`: Maximum concurrent dependency resolutions. Default: 50

Example for CI/CD environments:
```bash
export JIO_FAST_MODE=true
export JIO_MAX_DOWNLOAD_CONCURRENCY=50
export JIO_MAX_RESOLVE_CONCURRENCY=100
export JIO_HIGH_PERFORMANCE_SCRIPTS=true
export JIO_MAX_SCRIPT_CONCURRENCY=30
jio install
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

The project includes comprehensive unit tests with over 269 test cases covering all major functionality:

```bash
# Run all tests
dotnet test

# Run tests with coverage
dotnet test --collect:"XPlat Code Coverage"

# Run specific test
dotnet test --filter "FullyQualifiedName~IntegrityVerifier"

# Run tests for a specific class
dotnet test --filter "ClassName~PackCommandHandlerTests"
```

Test organization:
- **Command Tests**: Testing all CLI commands and their handlers
- **Resolution Tests**: Dependency resolution and version range handling
- **Lock Tests**: Lock file parsing and generation for all formats
- **Security Tests**: Integrity verification and security audit functionality
- **Storage Tests**: Package store and caching mechanisms
- **Workspace Tests**: Monorepo and workspace functionality

## License

MIT License - see [LICENSE](LICENSE) file for details.