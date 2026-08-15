using Jellyfin.HotCache.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Prometheus;

var builder = Host.CreateApplicationBuilder(args);
var connectionString = builder.Configuration["ConnectionStrings:HotCache"]
    ?? builder.Configuration["Jellyfin:HotCache:ConnectionString"];
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("A HotCache PostgreSQL connection string is required.");
}

builder.Services.AddSingleton(new NpgsqlDataSourceBuilder(connectionString).Build());
builder.Services.AddSingleton(new HotCacheOptions
{
    CanonicalRoot = builder.Configuration["Jellyfin:HotCache:CanonicalRoot"] ?? throw new InvalidOperationException("Canonical root is required."),
    HotRoot = builder.Configuration["Jellyfin:HotCache:HotRoot"] ?? throw new InvalidOperationException("Hot root is required."),
    Backend = builder.Configuration["Jellyfin:HotCache:Backend"] ?? "unraid-temp",
    HighWatermark = double.TryParse(builder.Configuration["Jellyfin:HotCache:HighWatermark"], out var high) ? high : .90,
    LowWatermark = double.TryParse(builder.Configuration["Jellyfin:HotCache:LowWatermark"], out var low) ? low : .75,
    ObserveOnly = !bool.TryParse(builder.Configuration["Jellyfin:HotCache:ObserveOnly"], out var observeOnly) || observeOnly,
});
builder.Services.AddSingleton<PostgreSqlHotCacheJobStore>();
builder.Services.AddSingleton<IHotCacheJobStore>(sp => sp.GetRequiredService<PostgreSqlHotCacheJobStore>());
builder.Services.AddSingleton<IFileOperations, PhysicalFileOperations>();
builder.Services.AddSingleton<HotCacheWorker>();
builder.Services.AddHostedService<HotCacheSchemaMigrationService>();
builder.Services.AddHostedService<HotCacheHostedService>();
var metricsPort = int.TryParse(builder.Configuration["Jellyfin:HotCache:MetricsPort"], out var configuredMetricsPort) ? configuredMetricsPort : 9109;
var metricServer = new MetricServer(port: metricsPort);
metricServer.Start();
await builder.Build().RunAsync();
