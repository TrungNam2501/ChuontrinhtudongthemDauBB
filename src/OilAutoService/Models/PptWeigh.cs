namespace OilAutoService.Models;

/// <summary>
/// Dữ liệu cân thực tế từ bảng ppt_weigh - lấy data mẻ dầu đã sử dụng
/// </summary>
public class PptWeigh
{
    public string? Barcode { get; set; }
    public int WeightId { get; set; }
    public string? MaterCode { get; set; }
    public string? EquipCode { get; set; }
    public string? EdtCode { get; set; }
    public string? ShiftClass { get; set; }
    public string? Shift { get; set; }
    public decimal? SetWeight { get; set; }
    public decimal? RealWeight { get; set; }
    public decimal? ErrorOut { get; set; }
    public string? WarningSgn { get; set; }
    public DateTime? WeighTime { get; set; }
    public decimal? ErrorAllow { get; set; }
    public string? UnitName { get; set; }
    public string? WeighType { get; set; }
    public string? Flg { get; set; }
    public string? UserPlanId { get; set; }
}
