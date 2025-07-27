namespace Jio.Core.Configuration;

public class ProcessResilienceConfiguration
{
    public bool EnableAutoRestart { get; set; } = true;
    public int MaxRestarts { get; set; } = 3;
    public int RestartDelaySeconds { get; set; } = 1;
    public int HealthCheckIntervalSeconds { get; set; } = 5;
    public RestartPolicy RestartPolicy { get; set; } = RestartPolicy.OnFailure;
    public string[]? RestartOnExitCodes { get; set; }
    public string[]? NoRestartOnExitCodes { get; set; }
    public bool EnableHealthChecks { get; set; } = true;
}

public enum RestartPolicy
{
    Never,
    OnFailure,
    Always,
    OnSpecificExitCodes
}

public static class ProcessResilienceDefaults
{
    public static ProcessResilienceConfiguration Development => new()
    {
        EnableAutoRestart = true,
        MaxRestarts = 5,
        RestartDelaySeconds = 1,
        HealthCheckIntervalSeconds = 10,
        RestartPolicy = RestartPolicy.OnFailure
    };
    
    public static ProcessResilienceConfiguration Production => new()
    {
        EnableAutoRestart = true,
        MaxRestarts = 5,
        RestartDelaySeconds = 3,
        HealthCheckIntervalSeconds = 20,
        RestartPolicy = RestartPolicy.OnFailure,
        NoRestartOnExitCodes = new[] { "0", "130", "137", "143" }, // Don't restart on success, SIGINT, SIGKILL, SIGTERM
        EnableHealthChecks = true
    };
    
    public static ProcessResilienceConfiguration CriticalProduction => new()
    {
        EnableAutoRestart = true,
        MaxRestarts = 10,
        RestartDelaySeconds = 1,
        HealthCheckIntervalSeconds = 10,
        RestartPolicy = RestartPolicy.OnFailure,
        NoRestartOnExitCodes = new[] { "0", "130" }, // Only don't restart on success or user interrupt
        EnableHealthChecks = true
    };
    
    public static ProcessResilienceConfiguration Disabled => new()
    {
        EnableAutoRestart = false,
        RestartPolicy = RestartPolicy.Never
    };
}