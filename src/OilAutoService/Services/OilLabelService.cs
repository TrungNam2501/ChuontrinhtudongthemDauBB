using Microsoft.Data.SqlClient;
using OilAutoService.Models;

namespace OilAutoService.Services;

public class OilLabelService : IOilLabelService
{
    private readonly ILogger<OilLabelService> _logger;
    private readonly string _server33ConnectionString;

    public OilLabelService(ILogger<OilLabelService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _server33ConnectionString = configuration.GetConnectionString("Server33")
            ?? throw new InvalidOperationException("Connection string 'Server33' not found.");
    }

    public async Task<bool> IsOrderProcessedAsync(string machineName, int groupLotId, string planId, CancellationToken ct)
    {
        using var connection = new SqlConnection(_server33ConnectionString);
        await connection.OpenAsync(ct);

        using var cmd = new SqlCommand(@"
            SELECT COUNT(1)
            FROM [BB].[dbo].[bb_Oil_AutoProcessed]
            WHERE [MachineName] = @machineName
              AND [GroupLotId] = @groupLotId
              AND [PlanId] = @planId", connection);

        cmd.Parameters.AddWithValue("@machineName", machineName);
        cmd.Parameters.AddWithValue("@groupLotId", groupLotId);
        cmd.Parameters.AddWithValue("@planId", planId);

        var count = (int)(await cmd.ExecuteScalarAsync(ct) ?? 0);
        return count > 0;
    }

    public async Task<int> InsertOilLabelsAsync(
        string machineConnectionString,
        PptGroupLot order,
        List<PmtWeigh> oilMaterials,
        List<PptWeigh> weighData,
        CancellationToken ct)
    {
        if (weighData.Count == 0)
        {
            _logger.LogWarning("Không có dữ liệu cân dầu cho PlanId={PlanId}", order.PlanId);
            return 0;
        }

        using var machineConn = new SqlConnection(machineConnectionString);
        await machineConn.OpenAsync(ct);

        int insertedCount = 0;

        foreach (var weigh in weighData)
        {
            string trimmedBarcode = weigh.Barcode?.Trim() ?? "";
            string trimmedMaterCode = weigh.MaterCode?.Trim() ?? "";

            // Serial_Num = 3 số cuối barcode, convert thành int (ví dụ "012" → 12)
            int serialNum = 0;
            if (trimmedBarcode.Length >= 3)
            {
                int.TryParse(trimmedBarcode[^3..], out serialNum);
            }

            // Equip_ID = equip_code parse thành int
            int equipId = 0;
            if (!string.IsNullOrWhiteSpace(weigh.EquipCode))
            {
                int.TryParse(weigh.EquipCode.Trim(), out equipId);
            }

            // Kiểm tra trùng lặp (barcode + mater_code + weight_id xác định duy nhất)
            using var cmdCheck = new SqlCommand(@"
                SELECT COUNT(1)
                FROM [dbo].[Ppt_BarCodeRep]
                WHERE [Plan_ID] = @planId
                  AND [Barcode] = @barcode
                  AND [Mater_Code] = @materCode
                  AND [Mater_Type] = @materType", machineConn);

            cmdCheck.Parameters.AddWithValue("@planId", order.PlanId ?? "");
            cmdCheck.Parameters.AddWithValue("@barcode", trimmedBarcode);
            cmdCheck.Parameters.AddWithValue("@materCode", trimmedMaterCode);
            cmdCheck.Parameters.AddWithValue("@materType", weigh.WeightId);

            var exists = (int)(await cmdCheck.ExecuteScalarAsync(ct) ?? 0);
            if (exists > 0)
            {
                _logger.LogDebug("Tem dầu đã tồn tại: PlanId={PlanId}, Barcode={Barcode}, MaterCode={MaterCode}, WeightId={WeightId}",
                    order.PlanId, trimmedBarcode, trimmedMaterCode, weigh.WeightId);
                continue;
            }

            // Tìm tem dầu khả dụng từ bb_Oil_Nhaptay trên Server33
            var oilDrum = await GetAvailableOilDrumAsync(trimmedMaterCode, ct);
            if (oilDrum == null)
            {
                _logger.LogWarning("Không tìm thấy tem dầu khả dụng cho MaterCode={MaterCode} trong bb_Oil_Nhaptay",
                    trimmedMaterCode);
                continue;
            }

            // SaveTime = weigh_time
            string saveTime = weigh.WeighTime?.ToString("yyyy-MM-dd HH:mm:ss")
                              ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            // INSERT vào Ppt_BarCodeRep
            using var cmdInsert = new SqlCommand(@"
                INSERT INTO [dbo].[Ppt_BarCodeRep]
                    ([SaveTime], [Barcode], [Equip_ID], [Plan_ID], [Recipe_Code],
                     [Recipe_Name], [Set_Num], [Serial_Num], [Mater_Code],
                     [Mater_Name], [Mater_Type], [Mater_Barcode], [Flg])
                VALUES
                    (@saveTime, @barcode, @equipId, @planId, @recipeCode,
                     @recipeName, @setNum, @serialNum, @materCode,
                     @materName, @materType, @materBarcode, @flg)", machineConn);

            cmdInsert.Parameters.AddWithValue("@saveTime", saveTime);
            cmdInsert.Parameters.AddWithValue("@barcode", trimmedBarcode);
            cmdInsert.Parameters.AddWithValue("@equipId", equipId);
            cmdInsert.Parameters.AddWithValue("@planId", order.PlanId ?? "");
            cmdInsert.Parameters.AddWithValue("@recipeCode", order.RecipeCode ?? "");
            cmdInsert.Parameters.AddWithValue("@recipeName", order.RecipeName ?? "");
            cmdInsert.Parameters.AddWithValue("@setNum", order.SetNumber ?? 0);
            cmdInsert.Parameters.AddWithValue("@serialNum", serialNum);
            cmdInsert.Parameters.AddWithValue("@materCode", trimmedMaterCode);
            cmdInsert.Parameters.AddWithValue("@materName", trimmedMaterCode);
            cmdInsert.Parameters.AddWithValue("@materType", weigh.WeightId);
            cmdInsert.Parameters.AddWithValue("@materBarcode", oilDrum.HmiBarcode);
            cmdInsert.Parameters.AddWithValue("@flg", "N");

            await cmdInsert.ExecuteNonQueryAsync(ct);
            insertedCount++;

            // Cập nhật số kg đã sử dụng trên bb_Oil_Nhaptay
            if (weigh.RealWeight.HasValue && weigh.RealWeight.Value > 0)
            {
                await UpdateOilDrumUsageAsync(oilDrum.Id, weigh.RealWeight.Value, ct);
            }

            _logger.LogDebug("Inserted tem: Barcode={Barcode}, MaterCode={MaterCode}, MaterBarcode={MaterBarcode}, WeightId={WeightId}, RealWeight={RealWeight}",
                trimmedBarcode, trimmedMaterCode, oilDrum.HmiBarcode, weigh.WeightId, weigh.RealWeight);
        }

        _logger.LogInformation("Đã insert {Count} tem dầu cho PlanId={PlanId}, RecipeCode={RecipeCode}",
            insertedCount, order.PlanId, order.RecipeCode);

        return insertedCount;
    }

    public async Task MarkOrderProcessedAsync(string machineName, int groupLotId, string? planId, string? recipeCode, int insertedRows, CancellationToken ct)
    {
        using var connection = new SqlConnection(_server33ConnectionString);
        await connection.OpenAsync(ct);

        // Tạo bảng nếu chưa có
        using var cmdCreate = new SqlCommand(@"
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'bb_Oil_AutoProcessed' AND schema_id = SCHEMA_ID('dbo'))
            BEGIN
                CREATE TABLE [dbo].[bb_Oil_AutoProcessed](
                    [Id] [int] IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [MachineName] [varchar](10) NOT NULL,
                    [GroupLotId] [int] NOT NULL,
                    [PlanId] [varchar](30) NULL,
                    [RecipeCode] [varchar](30) NULL,
                    [InsertedRows] [int] NOT NULL DEFAULT(0),
                    [ProcessedAt] [datetime] NOT NULL DEFAULT(GETDATE())
                );
                CREATE UNIQUE INDEX UQ_Machine_GroupLot_Plan 
                    ON [dbo].[bb_Oil_AutoProcessed]([MachineName], [GroupLotId], [PlanId]);
            END", connection);
        await cmdCreate.ExecuteNonQueryAsync(ct);

        // Insert record
        using var cmdInsert = new SqlCommand(@"
            IF NOT EXISTS (
                SELECT 1 FROM [dbo].[bb_Oil_AutoProcessed]
                WHERE [MachineName] = @machineName AND [GroupLotId] = @groupLotId AND [PlanId] = @planId
            )
            BEGIN
                INSERT INTO [dbo].[bb_Oil_AutoProcessed]
                    ([MachineName], [GroupLotId], [PlanId], [RecipeCode], [InsertedRows])
                VALUES
                    (@machineName, @groupLotId, @planId, @recipeCode, @insertedRows)
            END", connection);

        cmdInsert.Parameters.AddWithValue("@machineName", machineName);
        cmdInsert.Parameters.AddWithValue("@groupLotId", groupLotId);
        cmdInsert.Parameters.AddWithValue("@planId", (object?)planId ?? DBNull.Value);
        cmdInsert.Parameters.AddWithValue("@recipeCode", (object?)recipeCode ?? DBNull.Value);
        cmdInsert.Parameters.AddWithValue("@insertedRows", insertedRows);

        await cmdInsert.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Tìm tem dầu còn kg sử dụng từ bb_Oil_Nhaptay trên Server33.
    /// Match: RTRIM(LTRIM(HMI_Barcode)) = materCode, còn kg (Sokgtem > sokgsudung), còn active.
    /// </summary>
    private async Task<OilDrumInfo?> GetAvailableOilDrumAsync(string materCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(materCode))
            return null;

        using var connection = new SqlConnection(_server33ConnectionString);
        await connection.OpenAsync(ct);

        using var cmd = new SqlCommand(@"
            SELECT TOP 1 [ID], [HMI_Barcode]
            FROM [BB].[dbo].[bb_Oil_Nhaptay]
            WHERE RTRIM(LTRIM([HMI_Barcode])) = @materCode
              AND ISNULL([Sokgtem], 0) > ISNULL([sokgsudung], 0)
              AND [active] IS NOT NULL
              AND RTRIM(LTRIM([active])) != ''
            ORDER BY [ID] DESC", connection);

        cmd.Parameters.AddWithValue("@materCode", materCode);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var id = reader.GetInt32(0);
            var hmiBarcode = reader.IsDBNull(1) ? "" : reader.GetString(1).Trim();
            return new OilDrumInfo(id, hmiBarcode);
        }

        return null;
    }

    /// <summary>
    /// Cập nhật số kg đã sử dụng (sokgsudung) trên bb_Oil_Nhaptay sau khi insert tem.
    /// </summary>
    private async Task UpdateOilDrumUsageAsync(int oilDrumId, decimal realWeight, CancellationToken ct)
    {
        using var connection = new SqlConnection(_server33ConnectionString);
        await connection.OpenAsync(ct);

        using var cmd = new SqlCommand(@"
            UPDATE [BB].[dbo].[bb_Oil_Nhaptay]
            SET [sokgsudung] = ISNULL([sokgsudung], 0) + @realWeight
            WHERE [ID] = @id", connection);

        cmd.Parameters.AddWithValue("@id", oilDrumId);
        cmd.Parameters.AddWithValue("@realWeight", realWeight);

        var rows = await cmd.ExecuteNonQueryAsync(ct);
        if (rows > 0)
        {
            _logger.LogDebug("Cập nhật sokgsudung cho Oil Drum ID={Id}, thêm {Weight}kg", oilDrumId, realWeight);
        }
    }

    private record OilDrumInfo(int Id, string HmiBarcode);
}
