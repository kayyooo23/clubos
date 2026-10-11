using ClubOS.Service;
using ClubOS.Shared;

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
builder.Services.AddSingleton(sp => new ServerApi(sp.GetRequiredService<HttpClient>(), cfg.PcKey));
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

builder.Build().Run();
