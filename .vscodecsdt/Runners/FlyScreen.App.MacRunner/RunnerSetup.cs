using Microsoft.Extensions.DependencyInjection;

namespace Runners;

/// <summary>
/// Configuration class for Runner execution.
/// Modify this file to customise service registrations, environment, etc.
/// </summary>
public static class RunnerSetup
{
    /// <summary>
    /// Set the environment for the runner (e.g., "Development", "Staging", "Production").
    /// This determines which appsettings.{Environment}.json file is loaded.
    /// Default: "Development"
    /// </summary>
    public static string Environment { get; set; } = "Development";

    /// <summary>
    /// When true, concrete implementation types become directly injectable even when
    /// the production code only registers them against an interface.
    /// Set to false to keep the DI graph identical to production.
    /// </summary>
    public static bool AutoRegisterConcreteTypes { get; set; } = true;

    /// <summary>
    /// Override or add services to the DI container.
    /// Called after the source project's services are registered.
    /// </summary>
    public static void ConfigureServices(IServiceCollection services)
    {
        // Example: Replace a service implementation
        // services.RemoveAll<IMyService>();
        // services.AddScoped<IMyService, MockMyService>();

        // Example: Add additional test services
        // services.AddSingleton<ITestHelper, TestHelper>();

        // Example: Override configuration
        // services.Configure<MyOptions>(options => { options.Value = "test"; });
    }
}
