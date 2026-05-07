namespace OilAutoService.Models;

/// <summary>
/// Bảng tracking đơn hàng đã xử lý (lưu trên Server33/BB)
/// </summary>
public class ProcessedOrder
{
    public int Id { get; set; }
    public string MachineName { get; set; } = string.Empty;
    public int GroupLotId { get; set; }
    public string? PlanId { get; set; }
    public string? RecipeCode { get; set; }
    public int InsertedRows { get; set; }
    public DateTime ProcessedAt { get; set; }
}
