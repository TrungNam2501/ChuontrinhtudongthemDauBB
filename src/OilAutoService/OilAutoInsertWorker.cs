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

            // 0. Lấy watermark End_datetime đã xử lý cho máy này từ tracking table
            var lastEnd = await labelService.GetLastProcessedEndDatetimeAsync(machine.Name, ct);

            // 1. Lấy đơn hàng hoàn thành mới hơn watermark (fallback 7 ngày nếu null)
            var completedOrders = await orderService.GetCompletedOrdersAsync(
                machine.ConnectionString, lastEnd, lookbackDays: 7, ct);
            _logger.LogInformation(
                "[{Machine}] Watermark End_datetime={LastEnd}, tìm thấy {Count} đơn hoàn thành",
                machine.Name, lastEnd?.ToString("yyyy-MM-dd HH:mm:ss") ?? "(null - fallback 7 ngày)", completedOrders.Count);

            int processedCount = 0;
            int skippedCount = 0;
            int noOilCount = 0;

            foreach (var order in completedOrders)
            {
                if (string.IsNullOrWhiteSpace(order.PlanId) || string.IsNullOrWhiteSpace(order.RecipeCode))
                {
                    _logger.LogWarning("[{Machine}] Đơn hàng Id={Id} (MesPlanId={MesPlanId}) thiếu PlanId hoặc RecipeCode, bỏ qua",
                        machine.Name, order.Id, order.MesPlanId);
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
                    await labelService.MarkOrderProcessedAsync(machine.Name, order.Id, order.PlanId, order.MesPlanId, order.RecipeCode, order.EndDatetime, 0, ct);
                    noOilCount++;
                    continue;
                }

                _logger.LogInformation("[{Machine}] Đơn {PlanId} (MesPlanId={MesPlanId}) có {Count} loại dầu: {Oils}",
                    machine.Name, order.PlanId, order.MesPlanId, oilMaterials.Count,
                    string.Join(", ", oilMaterials.Select(m => m.ChildCode)));

                // 4. Lấy dữ liệu cân thực tế
                var weighData = await orderService.GetOilWeighDataAsync(machine.ConnectionString, order.PlanId, ct);

                if (weighData.Count == 0)
                {
                    _logger.LogWarning("[{Machine}] Đơn {PlanId} (MesPlanId={MesPlanId}) có dầu nhưng chưa có dữ liệu cân, bỏ qua",
                        machine.Name, order.PlanId, order.MesPlanId);
                    continue;
                }

                // 5. INSERT tem dầu vào Ppt_BarCodeRep
                var insertedRows = await labelService.InsertOilLabelsAsync(
                    machine.ConnectionString, order, oilMaterials, weighData, ct);

                // 6. Đánh dấu đã xử lý
                await labelService.MarkOrderProcessedAsync(machine.Name, order.Id, order.PlanId, order.MesPlanId, order.RecipeCode, order.EndDatetime, insertedRows, ct);
                processedCount++;

                _logger.LogInformation("[{Machine}] Đơn {PlanId} (MesPlanId={MesPlanId}): insert {Rows} tem dầu thành công",
                    machine.Name, order.PlanId, order.MesPlanId, insertedRows);
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
