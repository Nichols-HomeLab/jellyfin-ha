using Jellyfin.HotCache.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

var builder = Host.CreateApplicationBuilder(args);
var connectionString = builder.Configuration["ConnectionStrings:HotCache"]
    ?? builder.Configuration["Jellyfin__HotCache__ConnectionString"];
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("A HotCache PostgreSQL connection string is required.");
}

builder.Services.AddSingleton(new NpgsqlDataSourceBuilder(connectionString).Build());
builder.Services.AddSingleton<PostgreSqlHotCacheJobStore>();
builder.Services.AddSingleton<IHotCacheJobStore>(sp => sp.GetRequiredService<PostgreSqlHotCacheJobStore>());
builder.Services.AddSingleton<IFileOperations, PhysicalFileOperations>();
builder.Services.AddSingleton<HotCacheWorker>();
builder.Services.AddHostedService<HotCacheHostedService>();
await builder.Build().RunAsync();
