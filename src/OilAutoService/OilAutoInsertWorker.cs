using Microsoft.Extensions.Options;
using OilAutoService.Configuration;
using OilAutoService.Services;

namespace OilAutoService;

public class OilAutoInsertWorker : BackgroundService
{
    private readonly ILogger<OilAutoInsertWorker> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ServiceSettings _settings;
    private readonly List<MachineConfig> _machines;

    public OilAutoInsertWorker(
        ILogger<OilAutoInsertWorker> logger,
        IServiceProvider serviceProvider,
        IOptions<ServiceSettings> settings,
        IConfiguration configuration)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _settings = settings.Value;
        _machines = configuration.GetSection("Machines").Get<List<MachineConfig>>() ?? new();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OilAutoInsertWorker started. Interval: {Interval} minutes, Machines: {Count}",
            _settings.CheckIntervalMinutes, _machines.Count);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessAllMachinesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi không mong muốn trong chu trình xử lý");
            }

            await Task.Delay(TimeSpan.FromMinutes(_settings.CheckIntervalMinutes), stoppingToken);
        }

        _logger.LogInformation("OilAutoInsertWorker stopped.");
    }

    private async Task ProcessAllMachinesAsync(CancellationToken ct)
    {
        _logger.LogInformation("=== Bắt đầu kiểm tra {Count} máy ===", _machines.Count);

        var semaphore = new SemaphoreSlim(_settings.MaxParallelMachines);
        var tasks = _machines.Select(machine => ProcessMachineAsync(machine, semaphore, ct));

        await Task.WhenAll(tasks);

        _logger.LogInformation("=== Hoàn thành kiểm tra tất cả máy ===");
    }

    private async Task ProcessMachineAsync(MachineConfig machine, SemaphoreSlim semaphore, CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            _logger.LogInformation("[{Machine}] Bắt đầu kiểm tra...", machine.Name);

            using var scope = _serviceProvider.CreateScope();
            var orderService = scope.ServiceProvider.GetRequiredService<IMachineOrderService>();
            var labelService = scope.ServiceProvider.GetRequiredService<IOilLabelService>();

            // 1. Lấy đơn hàng hoàn thành trong ngày
            var completedOrders = await orderService.GetCompletedOrdersAsync(machine.ConnectionString, ct);
            _logger.LogInformation("[{Machine}] Tìm thấy {Count} đơn hàng hoàn thành", machine.Name, completedOrders.Count);

            int processedCount = 0;
            int skippedCount = 0;
            int noOilCount = 0;

            foreach (var order in completedOrders)
            {
                if (string.IsNullOrWhiteSpace(order.PlanId) || string.IsNullOrWhiteSpace(order.RecipeCode))
                {
                    _logger.LogWarning("[{Machine}] Đơn hàng Id={Id} thiếu PlanId hoặc RecipeCode, bỏ qua",
                        machine.Name, order.Id);
                    continue;
                }

                // 2. Kiểm tra đã xử lý chưa
                var isProcessed = await labelService.IsOrderProcessedAsync(machine.Name, order.Id, order.PlanId, ct);
                if (isProcessed)
                {
                    skippedCount++;
                    continue;
                }

                // 3. Kiểm tra tiêu chuẩn có dùng dầu không
                var oilMaterials = await orderService.GetOilMaterialsAsync(machine.ConnectionString, order.RecipeCode, ct);
                if (oilMaterials.Count == 0)
                {
                    // Không dùng dầu → đánh dấu đã xử lý (0 rows) để không check lại
                    await labelService.MarkOrderProcessedAsync(machine.Name, order.Id, order.PlanId, order.RecipeCode, 0, ct);
                    noOilCount++;
                    continue;
                }

                _logger.LogInformation("[{Machine}] Đơn {PlanId} có {Count} loại dầu: {Oils}",
                    machine.Name, order.PlanId, oilMaterials.Count,
                    string.Join(", ", oilMaterials.Select(m => m.ChildCode)));

                // 4. Lấy dữ liệu cân thực tế
                var weighData = await orderService.GetOilWeighDataAsync(machine.ConnectionString, order.PlanId, ct);

                if (weighData.Count == 0)
                {
                    _logger.LogWarning("[{Machine}] Đơn {PlanId} có dầu nhưng chưa có dữ liệu cân, bỏ qua",
                        machine.Name, order.PlanId);
                    continue;
                }

                // 5. INSERT tem dầu vào Ppt_BarCodeRep
                var insertedRows = await labelService.InsertOilLabelsAsync(
                    machine.ConnectionString, order, oilMaterials, weighData, ct);

                // 6. Đánh dấu đã xử lý
                await labelService.MarkOrderProcessedAsync(machine.Name, order.Id, order.PlanId, order.RecipeCode, insertedRows, ct);
                processedCount++;

                _logger.LogInformation("[{Machine}] Đơn {PlanId}: insert {Rows} tem dầu thành công",
                    machine.Name, order.PlanId, insertedRows);
            }

            _logger.LogInformation("[{Machine}] Kết quả: Xử lý={Processed}, Bỏ qua(đã xử lý)={Skipped}, Không dầu={NoOil}",
                machine.Name, processedCount, skippedCount, noOilCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Machine}] Lỗi khi xử lý máy", machine.Name);
        }
        finally
        {
            semaphore.Release();
        }
    }
}
