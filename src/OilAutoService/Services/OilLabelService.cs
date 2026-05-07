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

        // Bảng tracking được lazy-create trong MarkOrderProcessedAsync;
        // ở lần chạy đầu tiên bảng có thể chưa tồn tại -> coi như chưa xử lý.
        using var cmd = new SqlCommand(@"
            IF OBJECT_ID(N'[BB].[dbo].[bb_Oil_AutoProcessed]', N'U') IS NULL
                SELECT 0
            ELSE
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

        using var machineConnection = new SqlConnection(machineConnectionString);
        await machineConnection.OpenAsync(ct);

        using var bbConnection = new SqlConnection(_server33ConnectionString);
        await bbConnection.OpenAsync(ct);

        int insertedCount = 0;

        // Sort theo weigh_time ASC để xử lý đúng thứ tự khi 1 plan có nhiều dòng
        // (ví dụ cùng mater_code nhưng tách 2 dòng - cần update sokgsudung tuần tự)
        var sortedWeighData = weighData
            .OrderBy(w => w.WeighTime ?? DateTime.MinValue)
            .ToList();

        foreach (var weigh in sortedWeighData)
        {
            // Kiểm tra đã tồn tại chưa - dùng (Plan_ID, Barcode, Mater_Type)
            // vì cặp (barcode, weight_id) là duy nhất trên ppt_weigh
            using var cmdCheck = new SqlCommand(@"
                SELECT COUNT(1)
                FROM [dbo].[Ppt_BarCodeRep]
                WHERE [Plan_ID] = @planId
                  AND [Barcode] = @barcode
                  AND [Mater_Type] = @materType", machineConnection);

            cmdCheck.Parameters.AddWithValue("@planId", order.PlanId ?? "");
            cmdCheck.Parameters.AddWithValue("@barcode", weigh.Barcode ?? "");
            cmdCheck.Parameters.AddWithValue("@materType", weigh.WeightId);

            var exists = (int)(await cmdCheck.ExecuteScalarAsync(ct) ?? 0);
            if (exists > 0)
            {
                _logger.LogDebug("Tem dầu đã tồn tại: PlanId={PlanId}, Barcode={Barcode}, MaterType={MaterType}",
                    order.PlanId, weigh.Barcode, weigh.WeightId);
                continue;
            }

            // Tìm tem dầu còn kg trên bb_Oil_Nhaptay (FIFO - tem cũ trước)
            var label = await FindAvailableOilLabelAsync(bbConnection, weigh.MaterCode ?? "", ct);
            if (label is null)
            {
                _logger.LogWarning(
                    "Không tìm thấy tem dầu khả dụng cho MaterCode={MaterCode}, PlanId={PlanId}, Barcode={Barcode}. " +
                    "Vẫn insert Ppt_BarCodeRep với Mater_Barcode rỗng để ghi nhận.",
                    weigh.MaterCode, order.PlanId, weigh.Barcode);
            }

            // Parse Equip_ID từ equip_code (vd "03" -> 3)
            int equipId = 0;
            if (!string.IsNullOrWhiteSpace(weigh.EquipCode)
                && int.TryParse(weigh.EquipCode.Trim(), out var parsedEquip))
            {
                equipId = parsedEquip;
            }
            else
            {
                _logger.LogWarning("Không parse được Equip_ID từ equip_code='{Equip}' (PlanId={PlanId})",
                    weigh.EquipCode, order.PlanId);
            }

            // Parse Serial_Num từ 3 ký tự cuối barcode (vd "...001" -> 1, "...012" -> 12)
            int serialNum = ExtractSerialNum(weigh.Barcode);

            string materCode = (weigh.MaterCode ?? "").Trim();

            // INSERT vào Ppt_BarCodeRep (theo spec đã thống nhất)
            using var cmdInsert = new SqlCommand(@"
                INSERT INTO [dbo].[Ppt_BarCodeRep]
                    ([SaveTime], [Barcode], [Equip_ID], [Plan_ID], [Recipe_Code],
                     [Recipe_Name], [Set_Num], [Serial_Num], [Mater_Code],
                     [Mater_Name], [Mater_Type], [Mater_Barcode], [Flg])
                VALUES
                    (@saveTime, @barcode, @equipId, @planId, @recipeCode,
                     @recipeName, @setNum, @serialNum, @materCode,
                     @materName, @materType, @materBarcode, @flg)", machineConnection);

            cmdInsert.Parameters.AddWithValue("@saveTime",
                weigh.WeighTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            cmdInsert.Parameters.AddWithValue("@barcode", weigh.Barcode ?? "");
            cmdInsert.Parameters.AddWithValue("@equipId", equipId);
            cmdInsert.Parameters.AddWithValue("@planId", order.PlanId ?? "");
            cmdInsert.Parameters.AddWithValue("@recipeCode", order.RecipeCode ?? "");
            cmdInsert.Parameters.AddWithValue("@recipeName", order.RecipeName ?? "");
            cmdInsert.Parameters.AddWithValue("@setNum", order.SetNumber ?? 0);
            cmdInsert.Parameters.AddWithValue("@serialNum", serialNum);
            cmdInsert.Parameters.AddWithValue("@materCode", materCode);
            // Theo spec: Mater_Code và Mater_Name đều = a.mater_code
            cmdInsert.Parameters.AddWithValue("@materName", materCode);
            cmdInsert.Parameters.AddWithValue("@materType", weigh.WeightId);
            cmdInsert.Parameters.AddWithValue("@materBarcode", label?.HmiBarcode ?? "");
            cmdInsert.Parameters.AddWithValue("@flg", "N");

            await cmdInsert.ExecuteNonQueryAsync(ct);
            insertedCount++;

            // Sau khi insert -> update sokgsudung trên bb_Oil_Nhaptay
            if (label is not null && weigh.RealWeight.HasValue)
            {
                await UpdateSokgsudungAsync(bbConnection, label.Id, weigh.RealWeight.Value, ct);

                _logger.LogInformation(
                    "Insert tem dầu PlanId={PlanId}, Barcode={Barcode}, MaterCode={MaterCode}, " +
                    "RealWeight={RealWeight}, MaterBarcode(HMI)={Hmi}, LabelId={LabelId}",
                    order.PlanId, weigh.Barcode, materCode, weigh.RealWeight, label.HmiBarcode, label.Id);
            }
        }

        _logger.LogInformation("Đã insert {Count} tem dầu cho PlanId={PlanId}, RecipeCode={RecipeCode}",
            insertedCount, order.PlanId, order.RecipeCode);

        return insertedCount;
    }

    /// <summary>
    /// Lấy 3 ký tự cuối của barcode và parse thành số (vd "2605071114012" -> 12).
    /// Nếu không parse được thì trả về 0 (kèm log).
    /// </summary>
    private int ExtractSerialNum(string? barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode))
        {
            return 0;
        }

        var trimmed = barcode.Trim();
        if (trimmed.Length < 3)
        {
            _logger.LogWarning("Barcode '{Barcode}' ngắn hơn 3 ký tự, không lấy được Serial_Num", barcode);
            return 0;
        }

        var tail = trimmed[^3..];
        if (int.TryParse(tail, out var serial))
        {
            return serial;
        }

        _logger.LogWarning("Không parse được Serial_Num từ 3 ký tự cuối '{Tail}' của barcode '{Barcode}'",
            tail, barcode);
        return 0;
    }

    /// <summary>
    /// Tìm tem dầu khả dụng (active=1, còn kg) cho mater_code, FIFO theo ID.
    /// </summary>
    private static async Task<BbOilNhaptay?> FindAvailableOilLabelAsync(
        SqlConnection bbConnection,
        string materCode,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(materCode))
        {
            return null;
        }

        using var cmd = new SqlCommand(@"
            SELECT TOP 1 [ID], [HMI_Barcode], [Sokgtem], [sokgsudung]
            FROM [BB].[dbo].[bb_Oil_Nhaptay]
            WHERE LTRIM(RTRIM([HMI_Barcode])) = LTRIM(RTRIM(@materCode))
              AND [active] = 1
              AND ([Sokgtem] - [sokgsudung]) > 0
            ORDER BY [ID] ASC", bbConnection);

        cmd.Parameters.AddWithValue("@materCode", materCode.Trim());

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new BbOilNhaptay
        {
            Id = reader.GetInt32(0),
            HmiBarcode = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
            Sokgtem = reader.IsDBNull(2) ? 0 : reader.GetDecimal(2),
            Sokgsudung = reader.IsDBNull(3) ? 0 : reader.GetDecimal(3)
        };
    }

    /// <summary>
    /// Cộng real_weight vào sokgsudung của tem (theo Id).
    /// </summary>
    private static async Task UpdateSokgsudungAsync(
        SqlConnection bbConnection,
        int labelId,
        decimal realWeight,
        CancellationToken ct)
    {
        using var cmd = new SqlCommand(@"
            UPDATE [BB].[dbo].[bb_Oil_Nhaptay]
            SET [sokgsudung] = ISNULL([sokgsudung], 0) + @realWeight
            WHERE [ID] = @id", bbConnection);

        cmd.Parameters.AddWithValue("@realWeight", realWeight);
        cmd.Parameters.AddWithValue("@id", labelId);

        await cmd.ExecuteNonQueryAsync(ct);
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
