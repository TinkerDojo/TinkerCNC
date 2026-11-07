using TinkerCNC.History;
using TinkerCNC.Programs;
using TinkerCNC.Queue;
using TinkerCNC.Storage;
using TinkerCNC.Host.Notifications;

namespace TinkerCNC.Host;

public static class HostServices
{
    /// <summary>Registers the machine services every front end shares.</summary>
    public static IServiceCollection AddTinkerCNCHost(this IServiceCollection services)
    {
        services.AddSingleton<AppPaths>();
        services.AddSingleton<FrameHub>();
        services.AddSingleton<RecordingStore>();
        services.AddSingleton<MachineDirectory>();
        services.AddSingleton<MachineSessionManager>();
        services.AddSingleton<CameraService>();
        services.AddSingleton<TimelapseService>();
        services.AddSingleton<GCodeCache>();
        services.AddSingleton(sp => new AppDatabase(sp.GetRequiredService<AppPaths>().HistoryDatabase));
        services.AddSingleton(sp => new OperationHistory(sp.GetRequiredService<AppDatabase>()));
        services.AddSingleton(sp => new SetupRepository(sp.GetRequiredService<AppDatabase>()));
        services.AddSingleton<SetupLibrary>();
        services.AddSingleton(sp => new QueueRepository(sp.GetRequiredService<AppDatabase>()));
        services.AddSingleton<JobTrackers>();
        services.AddSingleton<JobOptionsStore>();
        services.AddSingleton<JobControls>();
        services.AddSingleton<JobQueues>();
        services.AddSingleton<NotificationCenter>();
        services.AddSingleton<DiscoveryService>();
        services.AddHostedService(sp => sp.GetRequiredService<DiscoveryService>());
        services.AddHostedService<MachineWatchers>();
        return services;
    }
}

/// <summary>
/// Creates the per-machine watchers at start-up, so operations are timed and notifications collected for every
/// connected machine whether or not anyone has a dashboard open.
/// </summary>
internal sealed class MachineWatchers(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        services.GetRequiredService<JobTrackers>();
        services.GetRequiredService<NotificationCenter>();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
