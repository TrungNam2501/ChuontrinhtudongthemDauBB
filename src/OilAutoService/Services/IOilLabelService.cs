using OilAutoService.Models;

namespace OilAutoService.Services;

public interface IOilLabelService
{
    /// <summary>
    /// Kiểm tra đơn hàng đã được xử lý chưa (tránh insert trùng)
    /// </summary>
    Task<bool> IsOrderProcessedAsync(string machineName, int groupLotId, string planId, CancellationToken ct);

    /// <summary>
    /// Insert tem dầu vào bảng Ppt_BarCodeRep trên máy
    /// </summary>
    Task<int> InsertOilLabelsAsync(
        string machineConnectionString,
        PptGroupLot order,
        List<PmtWeigh> oilMaterials,
        List<PptWeigh> weighData,
        CancellationToken ct);

    /// <summary>
    /// Đánh dấu đơn hàng đã xử lý xong (lưu vào Server33/BB)
    /// </summary>
    Task MarkOrderProcessedAsync(string machineName, int groupLotId, string? planId, string? mesPlanId, string? recipeCode, DateTime? endDatetime, int insertedRows, CancellationToken ct);

    /// <summary>
    /// Lấy MAX(EndDatetime) đã xử lý cho 1 máy (watermark). Trả về null nếu chưa có record.
    /// </summary>
    Task<DateTime?> GetLastProcessedEndDatetimeAsync(string machineName, CancellationToken ct);
}
