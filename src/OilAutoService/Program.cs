using OilAutoService;
using OilAutoService.Configuration;
using OilAutoService.Services;
using Serilog;

var exeDir = Path.GetDirectoryName(AppContext.BaseDirectory) ?? AppContext.BaseDirectory;
var logPath = Path.Combine(exeDir, "logs", "oil-auto-service-.log");

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File(logPath,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30)
    .CreateLogger();

try
{
    Log.Information("OilAutoService starting...");

    var builder = Host.CreateApplicationBuilder(args);

    // Windows Service support
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "OilAutoInsertService";
    });

    // Serilog
    builder.Services.AddSerilog();

    // Configuration
    builder.Services.Configure<ServiceSettings>(
        builder.Configuration.GetSection("ServiceSettings"));

    // Services (Scoped để mỗi machine cycle có instance riêng)
    builder.Services.AddScoped<IMachineOrderService, MachineOrderService>();
    builder.Services.AddScoped<IOilLabelService, OilLabelService>();

    // Worker
    builder.Services.AddHostedService<OilAutoInsertWorker>();

    var host = builder.Build();
    host.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "OilAutoService terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
