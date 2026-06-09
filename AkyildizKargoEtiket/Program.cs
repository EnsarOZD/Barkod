using AkyildizKargoEtiket;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "AkyildizKargoEtiket");

builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));
builder.Services.AddHttpClient<PrintAgentService>();
builder.Services.AddHostedService<PrintAgentWorker>();

var host = builder.Build();
host.Run();
