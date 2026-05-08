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

        // Đảm bảo bảng tracking tồn tại (auto-create trên Server33/BB nếu lần đầu chạy)
        await EnsureTrackingTableExistsAsync(connection, ct);

        using var cmd = new SqlCommand(@"
            SELECT COUNT(1)
            FROM [dbo].[bb_Oil_AutoProcessed]
            WHERE [MachineName] = @machineName
              AND [GroupLotId] = @groupLotId
              AND [PlanId] = @planId", connection);

        cmd.Parameters.AddWithValue("@machineName", machineName);
        cmd.Parameters.AddWithValue("@groupLotId", groupLotId);
        cmd.Parameters.AddWithValue("@planId", planId);

        var count = (int)(await cmd.ExecuteScalarAsync(ct) ?? 0);
        return count > 0;
    }

    /// <summary>
    /// Tạo bảng [BB].[dbo].[bb_Oil_AutoProcessed] và unique index nếu chưa tồn tại.
    /// IF NOT EXISTS check rẻ nên có thể gọi mỗi cycle.
    /// </summary>
    private static async Task EnsureTrackingTableExistsAsync(SqlConnection connection, CancellationToken ct)
    {
        using var cmd = new SqlCommand(@"
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'bb_Oil_AutoProcessed' AND schema_id = SCHEMA_ID('dbo'))
            BEGIN
                CREATE TABLE [dbo].[bb_Oil_AutoProcessed](
                    [Id] [int] IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [MachineName] [varchar](10) NOT NULL,
                    [GroupLotId] [int] NOT NULL,
                    [PlanId] [varchar](30) NULL,
                    [MesPlanId] [varchar](30) NULL,
                    [RecipeCode] [varchar](30) NULL,
                    [EndDatetime] [datetime] NULL,
                    [InsertedRows] [int] NOT NULL DEFAULT(0),
                    [ProcessedAt] [datetime] NOT NULL DEFAULT(GETDATE())
                );
                CREATE UNIQUE INDEX UQ_Machine_GroupLot_Plan
                    ON [dbo].[bb_Oil_AutoProcessed]([MachineName], [GroupLotId], [PlanId]);
            END", connection);
        await cmd.ExecuteNonQueryAsync(ct);
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
            _logger.LogWarning("Không có dữ liệu cân dầu cho PlanId={PlanId}, MesPlanId={MesPlanId}",
                order.PlanId, order.MesPlanId);
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
                _logger.LogDebug("Tem dầu đã tồn tại: PlanId={PlanId}, MesPlanId={MesPlanId}, Barcode={Barcode}, MaterType={MaterType}",
                    order.PlanId, order.MesPlanId, weigh.Barcode, weigh.WeightId);
                continue;
            }

            // Tìm tem dầu còn kg trên bb_Oil_Nhaptay (FIFO - tem cũ trước)
            var label = await FindAvailableOilLabelAsync(bbConnection, weigh.MaterCode ?? "", ct);
            if (label is null)
            {
                _logger.LogWarning(
                    "Không tìm thấy tem dầu khả dụng cho MaterCode={MaterCode}, PlanId={PlanId}, MesPlanId={MesPlanId}, Barcode={Barcode}. " +
                    "Vẫn insert Ppt_BarCodeRep với Mater_Barcode rỗng để ghi nhận.",
                    weigh.MaterCode, order.PlanId, order.MesPlanId, weigh.Barcode);
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
                _logger.LogWarning("Không parse được Equip_ID từ equip_code='{Equip}' (PlanId={PlanId}, MesPlanId={MesPlanId})",
                    weigh.EquipCode, order.PlanId, order.MesPlanId);
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
                    "Insert tem dầu PlanId={PlanId}, MesPlanId={MesPlanId}, Barcode={Barcode}, MaterCode={MaterCode}, " +
                    "RealWeight={RealWeight}, MaterBarcode(HMI)={Hmi}, LabelId={LabelId}",
                    order.PlanId, order.MesPlanId, weigh.Barcode, materCode, weigh.RealWeight, label.HmiBarcode, label.Id);
            }
        }

        _logger.LogInformation("Đã insert {Count} tem dầu cho PlanId={PlanId}, MesPlanId={MesPlanId}, RecipeCode={RecipeCode}",
            insertedCount, order.PlanId, order.MesPlanId, order.RecipeCode);

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
    /// Tìm tem dầu khả dụng cho mater_code, FIFO theo ID.
    /// active là nvarchar với 3 giá trị:
    ///   - 'mokhoa'    = đang mở, dùng được (cần còn kg)
    ///   - 'khoa'      = đã đóng, không dùng nữa
    ///   - 'boquakhoa' = user override, vẫn dùng được dù hết kg
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
            WHERE LTRIM(RTRIM([Barcode_left_7bit])) = LTRIM(RTRIM(@materCode))
              AND (
                    LTRIM(RTRIM([active])) = 'boquakhoa'
                 OR (LTRIM(RTRIM([active])) = 'mokhoa'
                      AND (ISNULL([Sokgtem], 0) - ISNULL([sokgsudung], 0)) > 0)
              )
            ORDER BY [Indat] ASC, [ID] ASC", bbConnection);

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
            // Sokgtem/sokgsudung trên DB có thể là float (System.Double) chứ không phải decimal,
            // dùng Convert.ToDecimal để chấp nhận cả float/decimal/numeric/int.
            Sokgtem = reader.IsDBNull(2) ? 0m : Convert.ToDecimal(reader.GetValue(2)),
            Sokgsudung = reader.IsDBNull(3) ? 0m : Convert.ToDecimal(reader.GetValue(3))
        };
    }

    /// <summary>
    /// Cộng real_weight vào sokgsudung của tem.
    /// Nếu sau khi cộng tem hết kg (sokgsudung >= Sokgtem) và active KHÔNG phải
    /// 'boquakhoa' thì tự động set active = 'khoa'. Nếu là 'boquakhoa' (user
    /// override) thì giữ nguyên - không tự đổi về 'khoa'.
    /// </summary>
    private static async Task UpdateSokgsudungAsync(
        SqlConnection bbConnection,
        int labelId,
        decimal realWeight,
        CancellationToken ct)
    {
        using var cmd = new SqlCommand(@"
            UPDATE [BB].[dbo].[bb_Oil_Nhaptay]
            SET [sokgsudung] = ISNULL([sokgsudung], 0) + @realWeight,
                [active] = CASE
                    WHEN LTRIM(RTRIM([active])) = 'boquakhoa' THEN [active]
                    WHEN (ISNULL([sokgsudung], 0) + @realWeight) >= ISNULL([Sokgtem], 0) THEN 'khoa'
                    ELSE [active]
                END
            WHERE [ID] = @id", bbConnection);

        cmd.Parameters.AddWithValue("@realWeight", realWeight);
        cmd.Parameters.AddWithValue("@id", labelId);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkOrderProcessedAsync(string machineName, int groupLotId, string? planId, string? mesPlanId, string? recipeCode, DateTime? endDatetime, int insertedRows, CancellationToken ct)
    {
        using var connection = new SqlConnection(_server33ConnectionString);
        await connection.OpenAsync(ct);

        // Đảm bảo bảng tồn tại
        await EnsureTrackingTableExistsAsync(connection, ct);

        // Insert record. Các cột [MesPlanId]/[EndDatetime] có thể chưa tồn tại trên DB cũ;
        // nếu thiếu thì user phải chạy ALTER TABLE để add các cột (xem PR description).
        using var cmdInsert = new SqlCommand(@"
            IF NOT EXISTS (
                SELECT 1 FROM [dbo].[bb_Oil_AutoProcessed]
                WHERE [MachineName] = @machineName AND [GroupLotId] = @groupLotId AND [PlanId] = @planId
            )
            BEGIN
                INSERT INTO [dbo].[bb_Oil_AutoProcessed]
                    ([MachineName], [GroupLotId], [PlanId], [MesPlanId], [RecipeCode], [EndDatetime], [InsertedRows])
                VALUES
                    (@machineName, @groupLotId, @planId, @mesPlanId, @recipeCode, @endDatetime, @insertedRows)
            END", connection);

        cmdInsert.Parameters.AddWithValue("@machineName", machineName);
        cmdInsert.Parameters.AddWithValue("@groupLotId", groupLotId);
        cmdInsert.Parameters.AddWithValue("@planId", (object?)planId ?? DBNull.Value);
        cmdInsert.Parameters.AddWithValue("@mesPlanId", (object?)mesPlanId ?? DBNull.Value);
        cmdInsert.Parameters.AddWithValue("@recipeCode", (object?)recipeCode ?? DBNull.Value);
        cmdInsert.Parameters.AddWithValue("@endDatetime", (object?)endDatetime ?? DBNull.Value);
        cmdInsert.Parameters.AddWithValue("@insertedRows", insertedRows);

        await cmdInsert.ExecuteNonQueryAsync(ct);
    }

    public async Task<DateTime?> GetLastProcessedEndDatetimeAsync(string machineName, CancellationToken ct)
    {
        using var connection = new SqlConnection(_server33ConnectionString);
        await connection.OpenAsync(ct);

        // Đảm bảo bảng tồn tại (lần đầu chạy trên DB sạch)
        await EnsureTrackingTableExistsAsync(connection, ct);

        using var cmd = new SqlCommand(@"
            SELECT MAX([EndDatetime])
            FROM [dbo].[bb_Oil_AutoProcessed]
            WHERE [MachineName] = @machineName", connection);

        cmd.Parameters.AddWithValue("@machineName", machineName);

        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is null || result is DBNull)
        {
            return null;
        }

        return (DateTime)result;
    }
}
