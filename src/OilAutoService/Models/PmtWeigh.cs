namespace OilAutoService.Models;

/// <summary>
/// Tiêu chuẩn cân từ bảng pmt_weigh - kiểm tra đơn hàng có dùng dầu không
/// </summary>
public class PmtWeigh
{
    public int WeightId { get; set; }
    public string FatherCode { get; set; } = string.Empty;
    public string EquipCode { get; set; } = string.Empty;
    public string EdtCode { get; set; } = string.Empty;
    public byte WeighType { get; set; }
    public string? ActCode { get; set; }
    public string? ChildCode { get; set; }
    public string? ChildName { get; set; }
    public decimal? SetWeight { get; set; }
    public decimal? ErrorAllow { get; set; }
    public string? UnitName { get; set; }
    public string? MemNote { get; set; }
}
