using ClubOS.Service;
using ClubOS.Service.Sync;
using ClubOS.Service.Update;
using ClubOS.Shared;

// Режим помощника обновления: служба запускает саму себя из папки прежней версии, чтобы переключить версию (см. Update/UpdateApplier.cs).
if (args.Length == 2 && args[0] == "--apply-update") return await UpdateApplierEntry.RunAsync(args[1]);

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "ClubOSAgent"); // под управлением SCM работает как служба, из консоли — как обычная программа

var cfg = AgentConfig.Load();
if (cfg.Server.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
    && !new Uri(cfg.Server).IsLoopback)
{
    Console.Error.WriteLine("ВНИМАНИЕ: сервер указан по http://, ключ ПК и пароли пойдут открытым текстом. Используйте https://.");
}

builder.Services.AddSingleton(cfg);
builder.Services.AddSingleton(_ => new HttpClient { BaseAddress = new Uri(cfg.Server), Timeout = TimeSpan.FromSeconds(10) });
// Ключ ПК появится позже, если ПК подключается по коду (см. EnrollmentManager); отпечаток железа сервер использует, чтобы ключ работал только на этом компьютере.
var machineId = MachineIdentity.Detect();
builder.Services.AddSingleton(sp => new ServerApi(sp.GetRequiredService<HttpClient>(), null, machineId));
builder.Services.AddSingleton<IIdentityStore>(sp =>
{
    var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Identity");
    return new DpapiIdentityStore(AgentConfig.IdentityPath, m => log.LogWarning("{Message}", m));
});
builder.Services.AddSingleton(sp =>
{
    var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Enrollment");
    return new EnrollmentManager(sp.GetRequiredService<ServerApi>(), cfg, sp.GetRequiredService<IIdentityStore>(), machineId, Environment.MachineName,
        TimeProvider.System, m => log.LogInformation("{Message}", m));
});
builder.Services.AddSingleton<ISystemPower>(_ => DryRunSystemPower.Enabled ? new DryRunSystemPower() : new WindowsSystemPower());
builder.Services.AddSingleton<ICommandJournal>(sp =>
{
    var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Commands");
    return new FileCommandJournal(AgentConfig.CommandJournalPath, null, m => log.LogWarning("{Message}", m));
});
builder.Services.AddSingleton(sp =>
{
    var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Commands");
    return new CommandRunner(sp.GetRequiredService<ServerApi>(), sp.GetRequiredService<ISystemPower>(), sp.GetRequiredService<ICommandJournal>(),
        m => log.LogInformation("{Message}", m));
});
builder.Services.AddSingleton<ITokenStore>(sp =>
{
    var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("TokenStore");
    return new DpapiTokenStore(DpapiTokenStore.DefaultPath, m => log.LogWarning("{Message}", m));
});
builder.Services.AddSingleton(sp =>
{
    var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Ads");
    AdImageCache.Protect(AgentConfig.AdsDir, m => log.LogWarning("{Message}", m));
    return new AdImageCache(sp.GetRequiredService<ServerApi>(), AgentConfig.AdsDir, log: m => log.LogInformation("{Message}", m));
});
builder.Services.AddSingleton(sp =>
{
    var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Covers");
    AdImageCache.Protect(AgentConfig.CoversDir, m => log.LogWarning("{Message}", m));
    return new CoverCache(sp.GetRequiredService<ServerApi>(), AgentConfig.CoversDir, log: m => log.LogInformation("{Message}", m));
});
builder.Services.AddSingleton<AgentCore>();
builder.Services.AddHostedService<PollWorker>();
// Автообновление работает, только если программа стоит в папке versions\<версия> и в неё вшит ключ подписи; иначе рабочий просто сообщает, что выключен.
builder.Services.AddHostedService(sp =>
{
    var layout = InstallLayout.FromBaseDirectory(AppContext.BaseDirectory);
    if (layout is not null && layout.CurrentVersion != AppVersion.Current) layout = null;     // папка названа не по версии программы: не рискуем
    var key = layout is null ? null : ReleaseKey.Load();
    var api = sp.GetRequiredService<ServerApi>();
    var updates = AgentConfig.UpdatesDir;
    var bad = new BadVersions(Path.Combine(updates, "bad-versions.json"));
    var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Update");
    UpdateInstaller? installer = layout is not null && key is not null
        ? new UpdateInstaller(api, key, layout, Path.Combine(updates, "downloads"), bad, null, null, m => logger.LogInformation("{Message}", m)) : null;
    var reconciler = layout is null ? null : new UpdateReconciler(api, new UpdateStateStore(Path.Combine(updates, "update-state.json")), bad, layout,
        Path.Combine(updates, "health.json"), AppVersion.Current, null, m => logger.LogInformation("{Message}", m));
    return new UpdateWorker(sp.GetRequiredService<AgentCore>(), api, installer, reconciler, new ProcessApplierLauncher(), layout, updates, AppVersion.Current,
        null, sp.GetRequiredService<ILogger<UpdateWorker>>());
});
builder.Services.AddHostedService<HardwareReporter>();
builder.Services.AddHostedService(sp => new ProgramsReporter(sp.GetRequiredService<ServerApi>(), sp.GetRequiredService<AgentCore>(), InstalledPrograms.ScanDefault,
    TimeProvider.System, sp.GetRequiredService<ILogger<ProgramsReporter>>()));
builder.Services.AddSingleton<ClubOS.Service.Cleanup.ISystemOps, ClubOS.Service.Cleanup.WindowsSystemOps>();
builder.Services.AddSingleton(sp => new ClubOS.Service.Cleanup.SessionCleaner(
    sp.GetRequiredService<ClubOS.Service.Cleanup.ISystemOps>(),
    new ClubOS.Service.Cleanup.CleanupOptions(
        cfg.Cleanup switch { "on" => ClubOS.Service.Cleanup.CleanupMode.On, "off" => ClubOS.Service.Cleanup.CleanupMode.Off, _ => ClubOS.Service.Cleanup.CleanupMode.DryRun },
        cfg.GuestUser, ClubOS.Service.Cleanup.CleanupOptions.DefaultProfilesRoot, cfg.CleanupPaths ?? [], cfg.CleanupKeep ?? []),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<ClubOS.Service.Cleanup.SessionCleaner>()));
builder.Services.AddHostedService<ClubOS.Service.Cleanup.CleanupWorker>();
builder.Services.AddHostedService(sp => new PipeServer(
    sp.GetRequiredService<AgentCore>(), AgentConfig.PipeName, sp.GetRequiredService<ILogger<PipeServer>>()));

// Синхронизация игр между ПК клуба: только если на этом ПК задана игровая папка (games_root в agent.json или CLUBOS_GAMES_ROOT).
if (!string.IsNullOrWhiteSpace(cfg.GamesRoot))
{
    var gamesRoot = cfg.GamesRoot!;
    builder.Services.AddSingleton(sp =>
    {
        var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Sync");
        var bind = Environment.GetEnvironmentVariable("CLUBOS_SYNC_BIND") is { Length: > 0 } b ? b : "+";   // «+»: все адреса сети (служба работает от системы, прав хватает)
        return new SyncServer(gamesRoot, bind, cfg.SyncPort, m => log.LogWarning("{Message}", m));
    });
    builder.Services.AddSingleton(sp =>
    {
        var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Sync");
        var core = sp.GetRequiredService<AgentCore>();
        var peers = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2), MaxConnectionsPerServer = 8 }) { Timeout = TimeSpan.FromSeconds(60) };
        // адрес, по которому другие ПК достанут файлы у этого: из CLUBOS_SYNC_ADVERTISE (разработка) или адрес в локальной сети
        string? Advertise() => Environment.GetEnvironmentVariable("CLUBOS_SYNC_ADVERTISE") is { Length: > 0 } fixedUrl ? fixedUrl
            : HardwareCollector.Network().Ip is { } ip ? $"http://{ip}:{cfg.SyncPort}" : null;
        return new SyncCoordinator(sp.GetRequiredService<ServerApi>(), peers, sp.GetRequiredService<SyncServer>(), gamesRoot, AgentConfig.SyncDir,
            () => UpdateWorker.IsIdle(core.Snapshot()), Advertise, null, m => log.LogInformation("{Message}", m));
    });
    builder.Services.AddHostedService(sp => new SyncWorker(sp.GetRequiredService<AgentCore>(), sp.GetRequiredService<SyncCoordinator>(), sp.GetRequiredService<SyncServer>(),
        sp.GetRequiredService<ILogger<SyncWorker>>()));
    builder.Services.AddHostedService(sp => new GamesReporter(sp.GetRequiredService<AgentCore>(), sp.GetRequiredService<ServerApi>(), gamesRoot, TimeProvider.System,
        sp.GetRequiredService<ILogger<GamesReporter>>()));
}

builder.Build().Run();
return 0;
