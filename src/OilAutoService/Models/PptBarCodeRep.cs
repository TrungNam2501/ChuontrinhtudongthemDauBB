namespace OilAutoService.Models;

/// <summary>
/// Bảng Ppt_BarCodeRep - nơi INSERT tem dầu
/// </summary>
public class PptBarCodeRep
{
    public string? SaveTime { get; set; }
    public string? Barcode { get; set; }
    public int? EquipId { get; set; }
    public string? PlanId { get; set; }
    public string? RecipeCode { get; set; }
    public string? RecipeName { get; set; }
    public int? SetNum { get; set; }
    public int? SerialNum { get; set; }
    public string? MaterCode { get; set; }
    public string? MaterName { get; set; }
    public int? MaterType { get; set; }
    public string? MaterBarcode { get; set; }
    public string? Flg { get; set; }
}
