using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Jio.Core.Commands;
using Jio.Core.Configuration;
using Jio.Core.Registry;
using Jio.Core.Resolution;
using Jio.Core.Storage;
using Jio.Core.Http;

var services = new ServiceCollection();

// Register services
services.AddSingleton<JioConfiguration>(sp =>
{
    return JioConfiguration.CreateWithNpmrcAsync().GetAwaiter().GetResult();
});
services.AddSingleton<HttpClient>(sp =>
{
    var config = sp.GetRequiredService<JioConfiguration>();
    return ProxyAwareHttpClientFactory.CreateHttpClient(config);
});
services.AddSingleton<IPackageRegistry, NpmRegistry>();
services.AddSingleton<IPackageStore, ContentAddressableStore>();
services.AddScoped<IDependencyResolver, DependencyResolver>();
services.AddScoped<ICommandHandler<InstallCommand>, InstallCommandHandler>();
services.AddScoped<ICommandHandler<InitCommand>, InitCommandHandler>();
services.AddScoped<ICommandHandler<RunCommand>, RunCommandHandler>();
services.AddScoped<ICommandHandler<UninstallCommand>, UninstallCommandHandler>();
services.AddScoped<ICommandHandler<UpdateCommand>, UpdateCommandHandler>();
services.AddScoped<InstallCommandHandler>();

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
runCommand.AddArgument(scriptArgument);
runCommand.AddOption(scriptArgsOption);
runCommand.SetHandler(async (string? script, string[] scriptArgs) =>
{
    var handler = serviceProvider.GetRequiredService<ICommandHandler<RunCommand>>();
    var exitCode = await handler.ExecuteAsync(new RunCommand 
    { 
        Script = script,
        Args = scriptArgs?.ToList() ?? []
    });
    Environment.Exit(exitCode);
}, scriptArgument, scriptArgsOption);

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
addCommand.SetHandler(async (string? package) =>
{
    var handler = serviceProvider.GetRequiredService<ICommandHandler<InstallCommand>>();
    var exitCode = await handler.ExecuteAsync(new InstallCommand { Package = package });
    Environment.Exit(exitCode);
}, packageArgument);
rootCommand.AddCommand(addCommand);

return await rootCommand.InvokeAsync(args);
