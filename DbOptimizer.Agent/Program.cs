using DbOptimizer.Agent.Configuration;
using DbOptimizer.Agent.Crawling;
using DbOptimizer.Agent.Http;
using DbOptimizer.Agent.Worker;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService();

builder.Services.Configure<AgentConfiguration>(
    builder.Configuration.GetSection(AgentConfiguration.SectionName));

builder.Services.AddHttpClient<BackendApiClient>();

builder.Services.AddSingleton<SqlServerCrawler>(sp =>
{
    var config = sp.GetRequiredService<IOptions<AgentConfiguration>>().Value;
    var logger = sp.GetRequiredService<ILogger<SqlServerCrawler>>();
    return new SqlServerCrawler(config.SqlConnectionString, logger);
});

builder.Services.AddSingleton<SqlObjectExecutor>(sp =>
{
    var config = sp.GetRequiredService<IOptions<AgentConfiguration>>().Value;
    var logger = sp.GetRequiredService<ILogger<SqlObjectExecutor>>();
    return new SqlObjectExecutor(config, logger);
});

builder.Services.AddHostedService<AgentWorker>();

var host = builder.Build();
host.Run();
