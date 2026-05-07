using OilAutoService.Models;

namespace OilAutoService.Services;

public interface IMachineOrderService
{
    /// <summary>
    /// Lấy danh sách đơn hàng hoàn thành trong ngày từ máy
    /// </summary>
    Task<List<PptGroupLot>> GetCompletedOrdersAsync(string connectionString, CancellationToken ct);

    /// <summary>
    /// Kiểm tra đơn hàng có sử dụng dầu không (pmt_weigh có child_code LIKE '68%')
    /// </summary>
    Task<List<PmtWeigh>> GetOilMaterialsAsync(string connectionString, string recipeCode, CancellationToken ct);

    /// <summary>
    /// Lấy dữ liệu cân thực tế của mẻ dầu (ppt_weigh)
    /// </summary>
    Task<List<PptWeigh>> GetOilWeighDataAsync(string connectionString, string planId, CancellationToken ct);
}
