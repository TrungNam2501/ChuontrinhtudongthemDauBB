using Microsoft.Data.SqlClient;
using OilAutoService.Models;

namespace OilAutoService.Services;

public class MachineOrderService : IMachineOrderService
{
    private readonly ILogger<MachineOrderService> _logger;

    public MachineOrderService(ILogger<MachineOrderService> logger)
    {
        _logger = logger;
    }

    public async Task<List<PptGroupLot>> GetCompletedOrdersAsync(string connectionString, CancellationToken ct)
    {
        var orders = new List<PptGroupLot>();

        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);

        // Lấy đơn hàng trong ngày (từ 6:30 sáng hôm nay đến 6:30 sáng hôm sau)
        // Nếu hiện tại < 6:30 sáng → tính là ngày hôm trước
        using var cmd = new SqlCommand(@"
            DECLARE @Now DATETIME = GETDATE()
            DECLARE @Today DATE = CAST(@Now AS DATE)

            -- Nếu trước 6:30 sáng, tính là ngày hôm trước
            DECLARE @StartDate DATETIME = 
                CASE 
                    WHEN CAST(@Now AS TIME) < '06:30:00' 
                    THEN DATEADD(MINUTE, 390, CAST(DATEADD(DAY, -1, @Today) AS DATETIME))
                    ELSE DATEADD(MINUTE, 390, CAST(@Today AS DATETIME))
                END

            DECLARE @EndDate DATETIME = DATEADD(DAY, 1, @StartDate)

            SELECT [Id], [Shift], [Shift_Class], [RecipeCode], [RecipeName],
                   [SetNumber], [Start_datetime], [End_datetime],
                   [FinishTag], [FinishNum], [Plan_ID], [UserPlanID], [MesPlanID]
            FROM [dbo].[Ppt_GroupLot]
            WHERE [Start_datetime] >= @StartDate
              AND [Start_datetime] < @EndDate
              AND [FinishTag] != 0
              AND [FinishNum] != 0
            ORDER BY [Id] DESC", connection);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            orders.Add(new PptGroupLot
            {
                Id = reader.GetInt32(0),
                Shift = reader.IsDBNull(1) ? null : reader.GetByte(1),
                ShiftClass = reader.IsDBNull(2) ? null : reader.GetByte(2),
                RecipeCode = reader.IsDBNull(3) ? null : reader.GetString(3).Trim(),
                RecipeName = reader.IsDBNull(4) ? null : reader.GetString(4).Trim(),
                SetNumber = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                StartDatetime = reader.IsDBNull(6) ? null : reader.GetDateTime(6),
                EndDatetime = reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                FinishTag = reader.IsDBNull(8) ? null : reader.GetByte(8),
                FinishNum = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                PlanId = reader.IsDBNull(10) ? null : reader.GetString(10).Trim(),
                UserPlanId = reader.IsDBNull(11) ? null : reader.GetString(11).Trim(),
                MesPlanId = reader.IsDBNull(12) ? null : reader.GetString(12).Trim()
            });
        }

        return orders;
    }

    public async Task<List<PmtWeigh>> GetOilMaterialsAsync(string connectionString, string recipeCode, CancellationToken ct)
    {
        var materials = new List<PmtWeigh>();

        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);

        // Kiểm tra tiêu chuẩn có dùng dầu không (child_code bắt đầu bằng '68')
        using var cmd = new SqlCommand(@"
            SELECT [weight_id], [father_code], [equip_code], [edt_code],
                   [weigh_type], [act_code], [child_code], [child_name],
                   [set_weight], [error_allow], [unit_name], [mem_note]
            FROM [dbo].[pmt_weigh]
            WHERE RTRIM([father_code]) = @recipeCode
              AND [child_code] LIKE '68%'", connection);

        cmd.Parameters.AddWithValue("@recipeCode", recipeCode);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            materials.Add(new PmtWeigh
            {
                WeightId = reader.GetInt32(0),
                FatherCode = reader.GetString(1).Trim(),
                EquipCode = reader.GetString(2).Trim(),
                EdtCode = reader.GetString(3).Trim(),
                WeighType = reader.IsDBNull(4) ? null : ReadStringOrByte(reader, 4),
                ActCode = reader.IsDBNull(5) ? null : reader.GetString(5).Trim(),
                ChildCode = reader.IsDBNull(6) ? null : reader.GetString(6).Trim(),
                ChildName = reader.IsDBNull(7) ? null : reader.GetString(7).Trim(),
                SetWeight = reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                ErrorAllow = reader.IsDBNull(9) ? null : reader.GetDecimal(9),
                UnitName = reader.IsDBNull(10) ? null : reader.GetString(10).Trim(),
                MemNote = reader.IsDBNull(11) ? null : reader.GetString(11).Trim()
            });
        }

        return materials;
    }

    public async Task<List<PptWeigh>> GetOilWeighDataAsync(string connectionString, string planId, CancellationToken ct)
    {
        var weighData = new List<PptWeigh>();

        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);

        // Lấy dữ liệu cân thực tế cho mã dầu (mater_code LIKE '68%')
        using var cmd = new SqlCommand(@"
            SELECT [barcode], [weight_id], [mater_code], [equip_code], [edt_code],
                   [shift_class], [shift], [set_weight], [real_weight], [error_out],
                   [warning_sgn], [weigh_time], [error_allow], [unit_name],
                   [weigh_type], [Flg], [UserPlanID]
            FROM [dbo].[ppt_weigh]
            WHERE [barcode] LIKE @planIdPattern
              AND [mater_code] LIKE '68%'", connection);

        cmd.Parameters.AddWithValue("@planIdPattern", planId + "%");

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            weighData.Add(new PptWeigh
            {
                Barcode = reader.IsDBNull(0) ? null : reader.GetString(0).Trim(),
                WeightId = reader.GetInt32(1),
                MaterCode = reader.IsDBNull(2) ? null : reader.GetString(2).Trim(),
                EquipCode = reader.IsDBNull(3) ? null : reader.GetString(3).Trim(),
                EdtCode = reader.IsDBNull(4) ? null : reader.GetString(4).Trim(),
                ShiftClass = reader.IsDBNull(5) ? null : ReadStringOrByte(reader, 5),
                Shift = reader.IsDBNull(6) ? null : ReadStringOrByte(reader, 6),
                SetWeight = reader.IsDBNull(7) ? null : reader.GetDecimal(7),
                RealWeight = reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                ErrorOut = reader.IsDBNull(9) ? null : reader.GetDecimal(9),
                WarningSgn = reader.IsDBNull(10) ? null : ReadStringOrByte(reader, 10),
                WeighTime = reader.IsDBNull(11) ? null : reader.GetDateTime(11),
                ErrorAllow = reader.IsDBNull(12) ? null : reader.GetDecimal(12),
                UnitName = reader.IsDBNull(13) ? null : reader.GetString(13).Trim(),
                WeighType = reader.IsDBNull(14) ? null : ReadStringOrByte(reader, 14),
                Flg = reader.IsDBNull(15) ? null : reader.GetString(15).Trim(),
                UserPlanId = reader.IsDBNull(16) ? null : reader.GetString(16).Trim()
            });
        }

        return weighData;
    }

    /// <summary>
    /// Đọc cột có thể là CHAR/VARCHAR/TINYINT/BIT thành string (trim whitespace).
    /// Một số DB lưu shift_class, shift, weigh_type, warning_sgn dưới dạng char(...)
    /// (vd "1 ", "油料") thay vì tinyint, nên dùng GetValue để cover cả 2 case.
    /// </summary>
    private static string? ReadStringOrByte(System.Data.Common.DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetValue(ordinal);
        return value?.ToString()?.Trim();
    }
}
