using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Jio.Core.Commands;
using Jio.Core.Configuration;
using Jio.Core.Registry;
using Jio.Core.Resolution;
using Jio.Core.Storage;
using Jio.Core.Http;
using Jio.Core.Cache;
using Jio.Core.Logging;
using Jio.Core.Telemetry;
using Jio.Core.Monitoring;
using Jio.Core.Scripts;
using Jio.Core.Dependencies;
using Jio.Core.Patches;
using Jio.Core.Security;

var services = new ServiceCollection();

// Register services
services.AddSingleton<JioConfiguration>(sp =>
{
    return JioConfiguration.CreateWithNpmrcAsync().GetAwaiter().GetResult();
});

// Logging and telemetry
services.AddSingleton<ILogger>(sp =>
{
    var logLevel = Environment.GetEnvironmentVariable("JIO_LOG_LEVEL") switch
    {
        "DEBUG" => LogLevel.Debug,
        "INFO" => LogLevel.Info,
        "WARN" or "WARNING" => LogLevel.Warning,
        "ERROR" => LogLevel.Error,
        _ => LogLevel.Info
    };
    
    var enableStructuredLogging = Environment.GetEnvironmentVariable("JIO_STRUCTURED_LOGGING") == "true";
    return new ConsoleLogger(logLevel, enableStructuredLogging);
});

services.AddSingleton<ITelemetryService>(sp =>
{
    var logger = sp.GetRequiredService<ILogger>();
    var enableTelemetry = Environment.GetEnvironmentVariable("JIO_TELEMETRY_ENABLED") != "false";
    return new TelemetryService(logger, enableTelemetry);
});

services.AddSingleton<HttpClient>(sp =>
{
    var config = sp.GetRequiredService<JioConfiguration>();
    return ProxyAwareHttpClientFactory.CreateHttpClient(config);
});

services.AddSingleton<IHealthCheckService, HealthCheckService>();
services.AddSingleton<IPackageRegistry, NpmRegistry>();
services.AddSingleton<IPackageStore, ContentAddressableStore>();
services.AddSingleton<IPackageCache, FileSystemPackageCache>();
services.AddScoped<IDependencyResolver>(sp =>
{
    var registry = sp.GetRequiredService<IPackageRegistry>();
    var config = sp.GetRequiredService<JioConfiguration>();
    var logger = sp.GetRequiredService<ILogger>();
    return new DependencyResolver(registry, config, logger);
});
services.AddScoped<ICommandHandler<InstallCommand>, InstallCommandHandler>();
services.AddScoped<ICommandHandler<InitCommand>, InitCommandHandler>();
services.AddScoped<ICommandHandler<RunCommand>, RunCommandHandler>();
services.AddScoped<ICommandHandler<UninstallCommand>, UninstallCommandHandler>();
services.AddScoped<ICommandHandler<UpdateCommand>, UpdateCommandHandler>();
services.AddScoped<ICommandHandler<ListCommand>, ListCommandHandler>();
services.AddScoped<ICommandHandler<OutdatedCommand>, OutdatedCommandHandler>();
services.AddScoped<ICommandHandler<ExecCommand>, ExecCommandHandler>();
services.AddScoped<ICommandHandler<AuditCommand>, AuditCommandHandler>();
services.AddScoped<ICommandHandler<LinkCommand>, LinkCommandHandler>();
services.AddScoped<ICommandHandler<PublishCommand>, PublishCommandHandler>();
services.AddScoped<ICommandHandler<SearchCommand>, SearchCommandHandler>();
services.AddScoped<ICommandHandler<ViewCommand>, ViewCommandHandler>();
services.AddScoped<ICommandHandler<DlxCommand>, DlxCommandHandler>();
services.AddScoped<ICommandHandler<CiCommand>, CiCommandHandler>();
services.AddScoped<ICommandHandler<PackCommand>, PackCommandHandler>();
services.AddScoped<ICommandHandler<VersionCommand>, VersionCommandHandler>();
services.AddScoped<ICommandHandler<PruneCommand>, PruneCommandHandler>();
services.AddScoped<ICommandHandler<DedupeCommand>, DedupeCommandHandler>();
services.AddScoped<ICommandHandler<PatchCommand>, PatchCommandHandler>();
services.AddScoped<InstallCommandHandler>();
services.AddSingleton<IPatchManager, PatchManager>();
services.AddSingleton<ISignatureVerifier, SignatureVerifier>();
services.AddSingleton<ILifecycleScriptRunner, LifecycleScriptRunner>();
services.AddSingleton<IGitDependencyResolver>(sp =>
{
    var logger = sp.GetRequiredService<ILogger>();
    var config = sp.GetRequiredService<JioConfiguration>();
    return new GitDependencyResolver(logger, config.CacheDirectory);
});
services.AddSingleton<ILocalDependencyResolver>(sp =>
{
    var logger = sp.GetRequiredService<ILogger>();
    var config = sp.GetRequiredService<JioConfiguration>();
    return new LocalDependencyResolver(logger, config.CacheDirectory);
});
services.AddSingleton<IOverrideResolver, OverrideResolver>();

var serviceProvider = services.BuildServiceProvider();

// Create root command
var rootCommand = new RootCommand("jio - Fast, secure, and storage-efficient JavaScript package manager");

// Init command
var initCommand = new Command("init", "Initialize a new package.json file");
var nameOption = new Option<string?>("--name", "Package name");
var yesOption = new Option<bool>("-y", "Skip prompts and use defaults");
initCommand.AddOption(nameOption);
initCommand.AddOption(yesOption);
initCommand.SetHandler(async (string? name, bool yes) =>
{
    var handler = serviceProvider.GetRequiredService<ICommandHandler<InitCommand>>();
    var exitCode = await handler.ExecuteAsync(new InitCommand { Name = name, Yes = yes });
    Environment.Exit(exitCode);
}, nameOption, yesOption);

// Install command
var installCommand = new Command("install", "Install packages");
installCommand.AddAlias("i");
var packageArgument = new Argument<string?>("package", () => null, "Package to install");
var saveDevOption = new Option<bool>("--save-dev", "Save as dev dependency");
var saveOptionalOption = new Option<bool>("--save-optional", "Save as optional dependency");
var saveExactOption = new Option<bool>("--save-exact", "Save exact version");
var globalOption = new Option<bool>("-g", "Install globally");
installCommand.AddArgument(packageArgument);
installCommand.AddOption(saveDevOption);
installCommand.AddOption(saveOptionalOption);
installCommand.AddOption(saveExactOption);
installCommand.AddOption(globalOption);
installCommand.SetHandler(async (string? package, bool saveDev, bool saveOptional, bool saveExact, bool global) =>
{
    var handler = serviceProvider.GetRequiredService<ICommandHandler<InstallCommand>>();
    var exitCode = await handler.ExecuteAsync(new InstallCommand 
    { 
        Package = package,
        SaveDev = saveDev,
        SaveOptional = saveOptional,
        SaveExact = saveExact,
        Global = global
    });
    Environment.Exit(exitCode);
}, packageArgument, saveDevOption, saveOptionalOption, saveExactOption, globalOption);

// Run command
var runCommand = new Command("run", "Run scripts defined in package.json");
var scriptArgument = new Argument<string?>("script", () => null, "Script to run");
var scriptArgsOption = new Option<string[]>("--", "Arguments to pass to the script") { AllowMultipleArgumentsPerToken = true };
var recursiveOption = new Option<bool>("-r", "Run script in all workspaces recursively");
var filterOption = new Option<string?>("--filter", "Filter workspaces by name");
var parallelOption = new Option<bool>("--parallel", "Run scripts in parallel");
var streamOption = new Option<bool>("--stream", "Stream output from scripts");
runCommand.AddArgument(scriptArgument);
runCommand.AddOption(scriptArgsOption);
runCommand.AddOption(recursiveOption);
runCommand.AddOption(filterOption);
runCommand.AddOption(parallelOption);
runCommand.AddOption(streamOption);
runCommand.SetHandler(async (string? script, string[] scriptArgs, bool recursive, string? filter, bool parallel, bool stream) =>
{
    var handler = serviceProvider.GetRequiredService<ICommandHandler<RunCommand>>();
    var exitCode = await handler.ExecuteAsync(new RunCommand 
    { 
        Script = script,
        Args = scriptArgs?.ToList() ?? [],
        Recursive = recursive,
        Filter = filter,
        Parallel = parallel,
        Stream = stream
    });
    Environment.Exit(exitCode);
}, scriptArgument, scriptArgsOption, recursiveOption, filterOption, parallelOption, streamOption);

// Test command (alias for run test)
var testCommand = new Command("test", "Run test script");
testCommand.SetHandler(async () =>
{
    var handler = serviceProvider.GetRequiredService<ICommandHandler<RunCommand>>();
    var exitCode = await handler.ExecuteAsync(new RunCommand { Script = "test" });
    Environment.Exit(exitCode);
});

// Start command (alias for run start)
var startCommand = new Command("start", "Run start script");
startCommand.SetHandler(async () =>
{
    var handler = serviceProvider.GetRequiredService<ICommandHandler<RunCommand>>();
    var exitCode = await handler.ExecuteAsync(new RunCommand { Script = "start" });
    Environment.Exit(exitCode);
});

// Uninstall command
var uninstallCommand = new Command("uninstall", "Remove packages");
uninstallCommand.AddAlias("remove");
uninstallCommand.AddAlias("rm");
uninstallCommand.AddAlias("r");
var uninstallPackageArgument = new Argument<string>("package", "Package to uninstall");
var uninstallSaveDevOption = new Option<bool>("--save-dev", "Remove from devDependencies");
var uninstallSaveOptionalOption = new Option<bool>("--save-optional", "Remove from optionalDependencies");
var uninstallGlobalOption = new Option<bool>("-g", "Uninstall globally");
uninstallCommand.AddArgument(uninstallPackageArgument);
uninstallCommand.AddOption(uninstallSaveDevOption);
uninstallCommand.AddOption(uninstallSaveOptionalOption);
uninstallCommand.AddOption(uninstallGlobalOption);
uninstallCommand.SetHandler(async (string package, bool saveDev, bool saveOptional, bool global) =>
{
    var handler = serviceProvider.GetRequiredService<ICommandHandler<UninstallCommand>>();
    var exitCode = await handler.ExecuteAsync(new UninstallCommand 
    { 
        Package = package,
        SaveDev = saveDev,
        SaveOptional = saveOptional,
        Global = global
    });
    Environment.Exit(exitCode);
}, uninstallPackageArgument, uninstallSaveDevOption, uninstallSaveOptionalOption, uninstallGlobalOption);

// Update command
var updateCommand = new Command("update", "Update packages to latest versions");
updateCommand.AddAlias("upgrade");
updateCommand.AddAlias("up");
var updatePackageArgument = new Argument<string?>("package", () => null, "Package to update");
var updateLatestOption = new Option<bool>("--latest", "Update to latest version, ignoring version ranges");
var updateDevOption = new Option<bool>("--dev", "Update devDependencies only");
var updateAllOption = new Option<bool>("--all", "Update all dependencies");
updateCommand.AddArgument(updatePackageArgument);
updateCommand.AddOption(updateLatestOption);
updateCommand.AddOption(updateDevOption);
updateCommand.AddOption(updateAllOption);
updateCommand.SetHandler(async (string? package, bool latest, bool dev, bool all) =>
{
    var handler = serviceProvider.GetRequiredService<ICommandHandler<UpdateCommand>>();
    var exitCode = await handler.ExecuteAsync(new UpdateCommand 
    { 
        Package = package,
        Latest = latest,
        Dev = dev,
        All = all
    });
    Environment.Exit(exitCode);
}, updatePackageArgument, updateLatestOption, updateDevOption, updateAllOption);

// Add all commands to root
rootCommand.AddCommand(initCommand);
rootCommand.AddCommand(installCommand);
rootCommand.AddCommand(runCommand);
rootCommand.AddCommand(testCommand);
rootCommand.AddCommand(startCommand);
rootCommand.AddCommand(uninstallCommand);
rootCommand.AddCommand(updateCommand);

// Add yarn-style aliases
var addCommand = new Command("add", "Install packages (yarn compatibility)");
var addPackageArgument = new Argument<string[]>("packages", () => Array.Empty<string>(), "Packages to install") { Arity = ArgumentArity.ZeroOrMore };
var addDevOption = new Option<bool>("-D", "Save as dev dependency");
var addExactOption = new Option<bool>("-E", "Save exact version");
var addOptionalOption = new Option<bool>("-O", "Save as optional dependency");
addCommand.AddArgument(addPackageArgument);
addCommand.AddOption(addDevOption);
addCommand.AddOption(addExactOption);
addCommand.AddOption(addOptionalOption);
addCommand.SetHandler(async (string[] packages, bool dev, bool exact, bool optional) =>
{
    var handler = serviceProvider.GetRequiredService<ICommandHandler<InstallCommand>>();
    if (packages.Length == 0)
    {
        // No packages specified, install all
        var exitCode = await handler.ExecuteAsync(new InstallCommand());
        Environment.Exit(exitCode);
    }
    else
    {
        // Install each package
        foreach (var package in packages)
        {
            var exitCode = await handler.ExecuteAsync(new InstallCommand 
            { 
                Package = package,
                SaveDev = dev,
                SaveExact = exact,
                SaveOptional = optional
            });
            if (exitCode != 0)
            {
                Environment.Exit(exitCode);
            }
        }
        Environment.Exit(0);
    }
}, addPackageArgument, addDevOption, addExactOption, addOptionalOption);
rootCommand.AddCommand(addCommand);

// List command
var listCommand = new Command("list", "List installed packages");
listCommand.AddAlias("ls");
var listDepthOption = new Option<int>("--depth", () => 0, "Max display depth of the dependency tree");
var listGlobalOption = new Option<bool>("-g", "List global packages");
var listJsonOption = new Option<bool>("--json", "Output in JSON format");
var listParseableOption = new Option<bool>("--parseable", "Output parseable results");
var listPatternArgument = new Argument<string?>("pattern", () => null, "Pattern to filter packages");
listCommand.AddOption(listDepthOption);
listCommand.AddOption(listGlobalOption);
listCommand.AddOption(listJsonOption);
listCommand.AddOption(listParseableOption);
listCommand.AddArgument(listPatternArgument);
listCommand.SetHandler(async (int depth, bool global, bool json, bool parseable, string? pattern) =>
{
    var handler = serviceProvider.GetRequiredService<ICommandHandler<ListCommand>>();
    var exitCode = await handler.ExecuteAsync(new ListCommand 
    { 
        Depth = depth,
        Global = global,
        Json = json,
        Parseable = parseable,
        Pattern = pattern
    });
    Environment.Exit(exitCode);
}, listDepthOption, listGlobalOption, listJsonOption, listParseableOption, listPatternArgument);
rootCommand.AddCommand(listCommand);

// Outdated command
var outdatedCommand = new Command("outdated", "Check for outdated packages");
var outdatedGlobalOption = new Option<bool>("-g", "Check global packages");
var outdatedJsonOption = new Option<bool>("--json", "Output in JSON format");
var outdatedDepthOption = new Option<int>("--depth", () => int.MaxValue, "Max depth for checking");
outdatedCommand.AddOption(outdatedGlobalOption);
outdatedCommand.AddOption(outdatedJsonOption);
outdatedCommand.AddOption(outdatedDepthOption);
outdatedCommand.SetHandler(async (bool global, bool json, int depth) =>
{
    var handler = serviceProvider.GetRequiredService<ICommandHandler<OutdatedCommand>>();
    var exitCode = await handler.ExecuteAsync(new OutdatedCommand 
    { 
        Global = global,
        Json = json,
        Depth = depth
    });
    Environment.Exit(exitCode);
}, outdatedGlobalOption, outdatedJsonOption, outdatedDepthOption);
rootCommand.AddCommand(outdatedCommand);

// Exec command
var execCommand = new Command("exec", "Execute a command");
var execCommandArgument = new Argument<string>("command", "Command to execute");
var execArgsOption = new Option<string[]>("--", "Arguments to pass to the command") { AllowMultipleArgumentsPerToken = true };
var execPackageOption = new Option<bool>("-p", "Execute from package");
var execCallOption = new Option<string?>("--call", "Script to execute from package.json");
execCommand.AddArgument(execCommandArgument);
execCommand.AddOption(execArgsOption);
execCommand.AddOption(execPackageOption);
execCommand.AddOption(execCallOption);
execCommand.SetHandler(async (string command, string[] args, bool package, string? call) =>
{
    var handler = serviceProvider.GetRequiredService<ICommandHandler<ExecCommand>>();
    var exitCode = await handler.ExecuteAsync(new ExecCommand 
    { 
        Command = command,
        Arguments = args?.ToList() ?? [],
        Package = package,
        Call = call
    });
    Environment.Exit(exitCode);
}, execCommandArgument, execArgsOption, execPackageOption, execCallOption);
rootCommand.AddCommand(execCommand);

// Audit command
var auditCommand = new Command("audit", "Scan project for vulnerabilities");
var auditFixOption = new Option<bool>("--fix", "Automatically install compatible updates");
var auditJsonOption = new Option<bool>("--json", "Output in JSON format");
var auditLevelOption = new Option<string>("--audit-level", () => "low", "Minimum level to exit with non-zero code");
var auditProductionOption = new Option<bool>("--production", "Only audit production dependencies");
var auditDevOption = new Option<bool>("--dev", "Only audit dev dependencies");
auditCommand.AddOption(auditFixOption);
auditCommand.AddOption(auditJsonOption);
auditCommand.AddOption(auditLevelOption);
auditCommand.AddOption(auditProductionOption);
auditCommand.AddOption(auditDevOption);
auditCommand.SetHandler(async (bool fix, bool json, string level, bool production, bool dev) =>
{
    var handler = serviceProvider.GetRequiredService<ICommandHandler<AuditCommand>>();
    var auditLevel = level.ToLowerInvariant() switch
    {
        "critical" => AuditLevel.Critical,
        "high" => AuditLevel.High,
        "moderate" => AuditLevel.Moderate,
        _ => AuditLevel.Low
    };
    var exitCode = await handler.ExecuteAsync(new AuditCommand 
    { 
        Fix = fix,
        Json = json,
        Level = auditLevel,
        Production = production,
        Dev = dev
    });
    Environment.Exit(exitCode);
}, auditFixOption, auditJsonOption, auditLevelOption, auditProductionOption, auditDevOption);
rootCommand.AddCommand(auditCommand);

// Link command
var linkCommand = new Command("link", "Create a symbolic link from the global or local folder");
var linkPackageArgument = new Argument<string?>("package", () => null, "Package to link (if empty, links current package)");
var linkGlobalOption = new Option<bool>("-g", "Link globally");
linkCommand.AddArgument(linkPackageArgument);
linkCommand.AddOption(linkGlobalOption);
linkCommand.SetHandler(async (string? package, bool global) =>
{
    var handler = serviceProvider.GetRequiredService<ICommandHandler<LinkCommand>>();
    var exitCode = await handler.ExecuteAsync(new LinkCommand 
    { 
        Package = package,
        Global = global
    });
    Environment.Exit(exitCode);
}, linkPackageArgument, linkGlobalOption);
rootCommand.AddCommand(linkCommand);

// Publish command
var publishCommand = new Command("publish", "Publish a package to the registry");
var publishTagOption = new Option<string>("--tag", () => "latest", "Tag to publish under");
var publishAccessOption = new Option<string>("--access", "Access level (public or restricted)");
var publishDryRunOption = new Option<bool>("--dry-run", "Perform a dry run without publishing");
var publishOtpOption = new Option<string>("--otp", "One-time password for 2FA");
var publishRegistryOption = new Option<string>("--registry", "Registry URL");
publishCommand.AddOption(publishTagOption);
publishCommand.AddOption(publishAccessOption);
publishCommand.AddOption(publishDryRunOption);
publishCommand.AddOption(publishOtpOption);
publishCommand.AddOption(publishRegistryOption);
publishCommand.SetHandler(async (string tag, string? access, bool dryRun, string? otp, string? registry) =>
{
    var handler = serviceProvider.GetRequiredService<ICommandHandler<PublishCommand>>();
    var exitCode = await handler.ExecuteAsync(new PublishCommand 
    { 
        Tag = tag,
        Access = access,
        DryRun = dryRun,
        Otp = otp,
        Registry = registry
    });
    Environment.Exit(exitCode);
}, publishTagOption, publishAccessOption, publishDryRunOption, publishOtpOption, publishRegistryOption);
rootCommand.AddCommand(publishCommand);

// Search command
var searchCommand = new Command("search", "Search for packages");
var searchQueryArgument = new Argument<string>("query", "Search query");
var searchJsonOption = new Option<bool>("--json", "Output in JSON format");
var searchLongOption = new Option<bool>("--long", "Show extended information");
var searchParseableOption = new Option<bool>("--parseable", "Output parseable results");
var searchRegistryOption = new Option<string>("--registry", "Registry URL");
searchCommand.AddArgument(searchQueryArgument);
searchCommand.AddOption(searchJsonOption);
searchCommand.AddOption(searchLongOption);
searchCommand.AddOption(searchParseableOption);
searchCommand.AddOption(searchRegistryOption);
searchCommand.SetHandler(async (string query, bool json, bool longFormat, bool parseable, string? registry) =>
{
    var handler = serviceProvider.GetRequiredService<ICommandHandler<SearchCommand>>();
    var exitCode = await handler.ExecuteAsync(new SearchCommand 
    { 
        Query = query,
        Json = json,
        Long = longFormat,
        ParseableOutput = parseable,
        Registry = registry
    });
    Environment.Exit(exitCode);
}, searchQueryArgument, searchJsonOption, searchLongOption, searchParseableOption, searchRegistryOption);
rootCommand.AddCommand(searchCommand);

// View command
var viewCommand = new Command("view", "View package information");
viewCommand.AddAlias("info");
viewCommand.AddAlias("show");
var viewPackageArgument = new Argument<string>("package", "Package name with optional version");
var viewFieldArgument = new Argument<string?>("field", () => null, "Specific field to display");
var viewJsonOption = new Option<bool>("--json", "Output in JSON format");
viewCommand.AddArgument(viewPackageArgument);
viewCommand.AddArgument(viewFieldArgument);
viewCommand.AddOption(viewJsonOption);
viewCommand.SetHandler(async (string package, string? field, bool json) =>
{
    var handler = serviceProvider.GetRequiredService<ICommandHandler<ViewCommand>>();
    var exitCode = await handler.ExecuteAsync(new ViewCommand 
    { 
        Package = package,
        Field = field,
        Json = json
    });
    Environment.Exit(exitCode);
}, viewPackageArgument, viewFieldArgument, viewJsonOption);
rootCommand.AddCommand(viewCommand);

// Dlx command (npx/yarn dlx/pnpm dlx equivalent)
var dlxCommand = new Command("dlx", "Download and execute a package temporarily");
var dlxPackageArgument = new Argument<string>("package", "Package to execute");
var dlxArgsOption = new Option<string[]>("--", "Arguments to pass to the package") { AllowMultipleArgumentsPerToken = true };
var dlxQuietOption = new Option<bool>("-q", "Suppress output");
var dlxRegistryOption = new Option<string>("--registry", "Registry URL");
dlxCommand.AddArgument(dlxPackageArgument);
dlxCommand.AddOption(dlxArgsOption);
dlxCommand.AddOption(dlxQuietOption);
dlxCommand.AddOption(dlxRegistryOption);
dlxCommand.SetHandler(async (string package, string[] args, bool quiet, string? registry) =>
{
    var handler = serviceProvider.GetRequiredService<ICommandHandler<DlxCommand>>();
    var exitCode = await handler.ExecuteAsync(new DlxCommand 
    { 
        Package = package,
        Arguments = args?.ToList() ?? [],
        Quiet = quiet,
        Registry = registry
    });
    Environment.Exit(exitCode);
}, dlxPackageArgument, dlxArgsOption, dlxQuietOption, dlxRegistryOption);
rootCommand.AddCommand(dlxCommand);

// Cache command
var cacheCommand = new Command("cache", "Manage the package cache");
var cacheCleanCommand = new Command("clean", "Clean the package cache");
cacheCleanCommand.SetHandler(() =>
{
    var cacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".jio", "cache"
    );
    if (Directory.Exists(cacheDir))
    {
        Directory.Delete(cacheDir, true);
        Console.WriteLine("Cache cleaned successfully.");
    }
    else
    {
        Console.WriteLine("Cache is already clean.");
    }
    Environment.Exit(0);
});
cacheCommand.AddCommand(cacheCleanCommand);
rootCommand.AddCommand(cacheCommand);

// Config command
var configCommand = new Command("config", "Manage configuration");
var configGetCommand = new Command("get", "Get a configuration value");
var configKeyArgument = new Argument<string>("key", "Configuration key");
configGetCommand.AddArgument(configKeyArgument);
configGetCommand.SetHandler((string key) =>
{
    var config = serviceProvider.GetRequiredService<JioConfiguration>();
    var value = key switch
    {
        "registry" => config.Registry,
        "proxy" => config.Proxy,
        "https-proxy" => config.HttpsProxy,
        "strict-ssl" => config.StrictSsl.ToString(),
        "maxsockets" => config.MaxConcurrentDownloads.ToString(),
        _ => null
    };
    if (value != null)
    {
        Console.WriteLine(value);
    }
    Environment.Exit(value != null ? 0 : 1);
}, configKeyArgument);
configCommand.AddCommand(configGetCommand);

// Config set command
var configSetCommand = new Command("set", "Set a configuration value");
var configSetKeyArgument = new Argument<string>("key", "Configuration key");
var configSetValueArgument = new Argument<string>("value", "Configuration value");
configSetCommand.AddArgument(configSetKeyArgument);
configSetCommand.AddArgument(configSetValueArgument);
configSetCommand.SetHandler(async (string key, string value) =>
{
    // Validate key
    var validKeys = new[] { "registry", "proxy", "https-proxy", "no-proxy", "strict-ssl", "maxsockets", "user-agent", "ca" };
    if (!validKeys.Contains(key) && !key.StartsWith("@") && !key.StartsWith("//"))
    {
        Console.Error.WriteLine($"Unknown configuration key: {key}");
        Environment.Exit(1);
    }
    
    // Get .npmrc path
    var npmrcPath = Path.Combine(Environment.CurrentDirectory, ".npmrc");
    
    try
    {
        await NpmrcParser.WriteAsync(npmrcPath, key, value);
        Console.WriteLine($"Set {key}={value}");
        Environment.Exit(0);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed to set configuration: {ex.Message}");
        Environment.Exit(1);
    }
}, configSetKeyArgument, configSetValueArgument);
configCommand.AddCommand(configSetCommand);

// Config delete command
var configDeleteCommand = new Command("delete", "Delete a configuration key");
configDeleteCommand.AddAlias("rm");
var configDeleteKeyArgument = new Argument<string>("key", "Configuration key to delete");
configDeleteCommand.AddArgument(configDeleteKeyArgument);
configDeleteCommand.SetHandler(async (string key) =>
{
    // Get .npmrc path
    var npmrcPath = Path.Combine(Environment.CurrentDirectory, ".npmrc");
    
    try
    {
        await NpmrcParser.DeleteAsync(npmrcPath, key);
        Console.WriteLine($"Deleted {key}");
        Environment.Exit(0);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed to delete configuration: {ex.Message}");
        Environment.Exit(1);
    }
}, configDeleteKeyArgument);
configCommand.AddCommand(configDeleteCommand);

rootCommand.AddCommand(configCommand);

// Why command (pnpm compatibility)
var whyCommand = new Command("why", "Show why a package is installed");
var whyPackageArgument = new Argument<string>("package", "Package name to check");
whyCommand.AddArgument(whyPackageArgument);
whyCommand.SetHandler(async (string package) =>
{
    // For now, redirect to list with pattern
    var handler = serviceProvider.GetRequiredService<ICommandHandler<ListCommand>>();
    var exitCode = await handler.ExecuteAsync(new ListCommand 
    { 
        Pattern = package,
        Depth = int.MaxValue
    });
    Environment.Exit(exitCode);
}, whyPackageArgument);
rootCommand.AddCommand(whyCommand);

// CI command
var ciCommand = new Command("ci", "Clean install from lock file");
var productionOption = new Option<bool>("--production", "Install production dependencies only");
ciCommand.AddOption(productionOption);
ciCommand.SetHandler(async (bool production) =>
{
    var handler = serviceProvider.GetRequiredService<ICommandHandler<CiCommand>>();
    var exitCode = await handler.ExecuteAsync(new CiCommand { Production = production });
    Environment.Exit(exitCode);
}, productionOption);
rootCommand.AddCommand(ciCommand);

// Pack command
var packCommand = new Command("pack", "Create a tarball from a package");
var packDirOption = new Option<string?>("--directory", "Directory to pack");
var dryRunOption = new Option<bool>("--dry-run", "Show what would be packed without creating tarball");
var packDestOption = new Option<string?>("--destination", "Destination directory for tarball");
packCommand.AddOption(packDirOption);
packCommand.AddOption(dryRunOption);
packCommand.AddOption(packDestOption);
packCommand.SetHandler(async (string? directory, bool dryRun, string? destination) =>
{
    var handler = serviceProvider.GetRequiredService<ICommandHandler<PackCommand>>();
    var exitCode = await handler.ExecuteAsync(new PackCommand 
    { 
        Directory = directory,
        DryRun = dryRun,
        Destination = destination
    });
    Environment.Exit(exitCode);
}, packDirOption, dryRunOption, packDestOption);
rootCommand.AddCommand(packCommand);

// Version command
var versionCommand = new Command("version", "Bump package version");
var newVersionArg = new Argument<string?>("new-version", () => null, "New version or increment type");
var majorOption = new Option<bool>("--major", "Increment major version");
var minorOption = new Option<bool>("--minor", "Increment minor version");
var patchOption = new Option<bool>("--patch", "Increment patch version");
var premajorOption = new Option<bool>("--premajor", "Increment premajor version");
var preminorOption = new Option<bool>("--preminor", "Increment preminor version");
var prepatchOption = new Option<bool>("--prepatch", "Increment prepatch version");
var prereleaseOption = new Option<bool>("--prerelease", "Increment prerelease version");
var preidOption = new Option<string?>("--preid", "Prerelease identifier");
var noGitTagOption = new Option<bool>("--no-git-tag-version", "Do not create git tag");
var messageOption = new Option<string?>("--message", "Custom git tag message");
versionCommand.AddArgument(newVersionArg);
versionCommand.AddOption(majorOption);
versionCommand.AddOption(minorOption);
versionCommand.AddOption(patchOption);
versionCommand.AddOption(premajorOption);
versionCommand.AddOption(preminorOption);
versionCommand.AddOption(prepatchOption);
versionCommand.AddOption(prereleaseOption);
versionCommand.AddOption(preidOption);
versionCommand.AddOption(noGitTagOption);
versionCommand.AddOption(messageOption);
versionCommand.SetHandler(async (string? newVersion, bool major, bool minor, bool patch, 
    bool premajor, bool preminor, bool prepatch, bool prerelease) =>
{
    var handler = serviceProvider.GetRequiredService<ICommandHandler<VersionCommand>>();
    var exitCode = await handler.ExecuteAsync(new VersionCommand 
    { 
        NewVersion = newVersion,
        Major = major,
        Minor = minor,
        Patch = patch,
        Premajor = premajor,
        Preminor = preminor,
        Prepatch = prepatch,
        Prerelease = prerelease,
        Preid = null,
        NoGitTagVersion = false,
        Message = null
    });
    Environment.Exit(exitCode);
}, newVersionArg, majorOption, minorOption, patchOption, 
   premajorOption, preminorOption, prepatchOption, prereleaseOption);
rootCommand.AddCommand(versionCommand);

// Prune command
var pruneCommand = new Command("prune", "Remove extraneous packages not listed in package.json");
var pruneProductionOption = new Option<bool>("--production", "Remove packages not in dependencies");
var pruneDryRunOption = new Option<bool>("--dry-run", "Show what would be removed without removing");
var pruneJsonOption = new Option<bool>("--json", "Output results in JSON format");
pruneCommand.AddOption(pruneProductionOption);
pruneCommand.AddOption(pruneDryRunOption);
pruneCommand.AddOption(pruneJsonOption);
pruneCommand.SetHandler(async (bool production, bool dryRun, bool json) =>
{
    var handler = serviceProvider.GetRequiredService<ICommandHandler<PruneCommand>>();
    var exitCode = await handler.ExecuteAsync(new PruneCommand 
    { 
        Production = production,
        DryRun = dryRun,
        Json = json
    });
    Environment.Exit(exitCode);
}, pruneProductionOption, pruneDryRunOption, pruneJsonOption);
rootCommand.AddCommand(pruneCommand);

// Dedupe command
var dedupeCommand = new Command("dedupe", "Reduce duplication by moving dependencies up the tree");
dedupeCommand.AddAlias("ddp");
var dedupeDryRunOption = new Option<bool>("--dry-run", "Show what would be done without doing it");
var dedupeJsonOption = new Option<bool>("--json", "Output results in JSON format");
var dedupePackageArg = new Argument<string?>("package", () => null, "Specific package to deduplicate");
dedupeCommand.AddOption(dedupeDryRunOption);
dedupeCommand.AddOption(dedupeJsonOption);
dedupeCommand.AddArgument(dedupePackageArg);
dedupeCommand.SetHandler(async (bool dryRun, bool json, string? package) =>
{
    var handler = serviceProvider.GetRequiredService<ICommandHandler<DedupeCommand>>();
    var exitCode = await handler.ExecuteAsync(new DedupeCommand 
    { 
        DryRun = dryRun,
        Json = json,
        Package = package
    });
    Environment.Exit(exitCode);
}, dedupeDryRunOption, dedupeJsonOption, dedupePackageArg);
rootCommand.AddCommand(dedupeCommand);

// Patch command
var patchCommand = new Command("patch", "Create and manage patches for dependencies");
var patchPackageArg = new Argument<string>("package", "Package to patch");
var patchCreateOption = new Option<bool>("--create", "Create a new patch");
var patchEditDirOption = new Option<string?>("--edit-dir", "Directory to edit the package in");
patchCommand.AddArgument(patchPackageArg);
patchCommand.AddOption(patchCreateOption);
patchCommand.AddOption(patchEditDirOption);
patchCommand.SetHandler(async (string package, bool create, string? editDir) =>
{
    var handler = serviceProvider.GetRequiredService<ICommandHandler<PatchCommand>>();
    var exitCode = await handler.ExecuteAsync(new PatchCommand 
    { 
        Package = package,
        Create = create,
        EditDir = editDir
    });
    Environment.Exit(exitCode);
}, patchPackageArg, patchCreateOption, patchEditDirOption);
rootCommand.AddCommand(patchCommand);

// Support for direct command execution (npm/yarn/pnpm style)
// Check if first argument is not a known command
if (args.Length > 0)
{
    var knownCommands = new[] { "init", "install", "i", "add", "uninstall", "remove", "rm", "r", 
                                "update", "upgrade", "up", "run", "test", "start", "list", "ls", 
                                "outdated", "exec", "audit", "link", "publish", "search", "view", "info", "show", 
                                "dlx", "cache", "config", "why", "ci", "pack", "version", "prune", "dedupe", "ddp", "patch", "--help", "-h", "--version" };
    
    if (!knownCommands.Contains(args[0], StringComparer.OrdinalIgnoreCase))
    {
        // Try to run as script first
        var runHandler = serviceProvider.GetRequiredService<ICommandHandler<RunCommand>>();
        var runResult = await runHandler.ExecuteAsync(new RunCommand 
        { 
            Script = args[0],
            Args = args.Skip(1).ToList()
        });
        
        if (runResult == 0)
        {
            return 0;
        }
        
        // If not a script, try exec
        var execHandler = serviceProvider.GetRequiredService<ICommandHandler<ExecCommand>>();
        var execResult = await execHandler.ExecuteAsync(new ExecCommand 
        { 
            Command = args[0],
            Arguments = args.Skip(1).ToList()
        });
        
        return execResult;
    }
}

return await rootCommand.InvokeAsync(args);
