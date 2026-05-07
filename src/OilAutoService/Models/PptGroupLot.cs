namespace OilAutoService.Models;

/// <summary>
/// Đơn hàng từ bảng Ppt_GroupLot trên mỗi máy (mfns database)
/// </summary>
public class PptGroupLot
{
    public int Id { get; set; }
    public byte? Shift { get; set; }
    public byte? ShiftClass { get; set; }
    public string? RecipeCode { get; set; }
    public string? RecipeName { get; set; }
    public int? SetNumber { get; set; }
    public DateTime? StartDatetime { get; set; }
    public DateTime? EndDatetime { get; set; }
    public byte? FinishTag { get; set; }
    public int? FinishNum { get; set; }
    public string? PlanId { get; set; }
    public string? UserPlanId { get; set; }
    public string? MesPlanId { get; set; }
}
