using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Jio.Core.Commands;
using Jio.Core.Configuration;
using Jio.Core.Registry;
using Jio.Core.Resolution;
using Jio.Core.Storage;

var services = new ServiceCollection();

// Register services
services.AddSingleton<JioConfiguration>();
services.AddHttpClient<IPackageRegistry, NpmRegistry>();
services.AddSingleton<IPackageStore, ContentAddressableStore>();
services.AddScoped<IDependencyResolver, DependencyResolver>();
services.AddScoped<ICommandHandler<InstallCommand>, InstallCommandHandler>();
services.AddScoped<ICommandHandler<InitCommand>, InitCommandHandler>();

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

rootCommand.AddCommand(initCommand);
rootCommand.AddCommand(installCommand);

return await rootCommand.InvokeAsync(args);
