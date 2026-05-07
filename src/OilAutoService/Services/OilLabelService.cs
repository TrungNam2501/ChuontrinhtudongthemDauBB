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

        using var connection = new SqlConnection(machineConnectionString);
        await connection.OpenAsync(ct);

        int insertedCount = 0;
        var now = DateTime.Now;

        foreach (var weigh in weighData)
        {
            // Kiểm tra đã tồn tại chưa (tránh trùng)
            using var cmdCheck = new SqlCommand(@"
                SELECT COUNT(1)
                FROM [dbo].[Ppt_BarCodeRep]
                WHERE [Plan_ID] = @planId
                  AND [Mater_Code] = @materCode
                  AND [Serial_Num] = @serialNum
                  AND [Mater_Barcode] = @materBarcode", connection);

            cmdCheck.Parameters.AddWithValue("@planId", order.PlanId ?? "");
            cmdCheck.Parameters.AddWithValue("@materCode", weigh.MaterCode ?? "");
            cmdCheck.Parameters.AddWithValue("@serialNum", weigh.WeightId);
            cmdCheck.Parameters.AddWithValue("@materBarcode", weigh.Barcode ?? "");

            var exists = (int)(await cmdCheck.ExecuteScalarAsync(ct) ?? 0);
            if (exists > 0)
            {
                _logger.LogDebug("Tem dầu đã tồn tại: PlanId={PlanId}, MaterCode={MaterCode}, WeightId={WeightId}",
                    order.PlanId, weigh.MaterCode, weigh.WeightId);
                continue;
            }

            // Tìm thông tin material name từ oilMaterials
            var material = oilMaterials.FirstOrDefault(m =>
                m.ChildCode?.Trim() == weigh.MaterCode?.Trim());

            // INSERT vào Ppt_BarCodeRep
            using var cmdInsert = new SqlCommand(@"
                INSERT INTO [dbo].[Ppt_BarCodeRep]
                    ([SaveTime], [Barcode], [Equip_ID], [Plan_ID], [Recipe_Code],
                     [Recipe_Name], [Set_Num], [Serial_Num], [Mater_Code],
                     [Mater_Name], [Mater_Type], [Mater_Barcode], [Flg])
                VALUES
                    (@saveTime, @barcode, @equipId, @planId, @recipeCode,
                     @recipeName, @setNum, @serialNum, @materCode,
                     @materName, @materType, @materBarcode, @flg)", connection);

            cmdInsert.Parameters.AddWithValue("@saveTime", now.ToString("yyyy-MM-dd HH:mm:ss"));
            cmdInsert.Parameters.AddWithValue("@barcode", weigh.Barcode ?? "");
            cmdInsert.Parameters.AddWithValue("@equipId", 0); // TODO: xác định equip_id
            cmdInsert.Parameters.AddWithValue("@planId", order.PlanId ?? "");
            cmdInsert.Parameters.AddWithValue("@recipeCode", order.RecipeCode ?? "");
            cmdInsert.Parameters.AddWithValue("@recipeName", order.RecipeName ?? "");
            cmdInsert.Parameters.AddWithValue("@setNum", order.SetNumber ?? 0);
            cmdInsert.Parameters.AddWithValue("@serialNum", weigh.WeightId);
            cmdInsert.Parameters.AddWithValue("@materCode", weigh.MaterCode ?? "");
            cmdInsert.Parameters.AddWithValue("@materName", material?.ChildName ?? "");
            cmdInsert.Parameters.AddWithValue("@materType", 0); // TODO: xác định mater_type
            cmdInsert.Parameters.AddWithValue("@materBarcode", weigh.Barcode ?? "");
            cmdInsert.Parameters.AddWithValue("@flg", "N");

            await cmdInsert.ExecuteNonQueryAsync(ct);
            insertedCount++;
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
}
