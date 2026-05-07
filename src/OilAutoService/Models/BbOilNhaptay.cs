namespace OilAutoService.Models;

/// <summary>
/// Tem dầu trên Server33/BB.dbo.bb_Oil_Nhaptay - dùng để lấy Mater_Barcode
/// và update sokgsudung sau mỗi lần cân.
/// </summary>
public class BbOilNhaptay
{
    public int Id { get; set; }
    public string HmiBarcode { get; set; } = string.Empty;
    public decimal Sokgtem { get; set; }
    public decimal Sokgsudung { get; set; }
}
