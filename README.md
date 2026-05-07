# Chương trình tự động thêm dầu BB (OilAutoService)

.NET Worker Service tự động kiểm tra đơn hàng hoàn thành trên 8 máy BB và insert tem mã vạch dầu.

## Kiến trúc

```
Timer (mỗi 5 phút) → Duyệt 8 máy song song:
  → Query Ppt_GroupLot (đơn hoàn thành trong ngày)
  → Check pmt_weigh (tiêu chuẩn có dùng dầu?)
  → Query ppt_weigh (data cân thực tế)
  → INSERT Ppt_BarCodeRep (tem dầu)
  → Lưu tracking vào Server33/BB
```

## Database

| Server | Database | Mô tả |
|--------|----------|--------|
| 198.1.10.33 | BB | Bảng bb_Oil_Nhaptay (4 thùng dầu), bb_Oil_AutoProcessed (tracking) |
| 198.1.8.21-24, 35-38 | mfns | 8 máy BB01-BB08: Ppt_GroupLot, pmt_weigh, ppt_weigh, Ppt_BarCodeRep |

## Cài đặt & Chạy

### Development (chạy thử)
```bash
cd src/OilAutoService
dotnet run
```

### Publish & Deploy Windows Service
```bash
dotnet publish src/OilAutoService -c Release -r win-x64 --self-contained -o publish

# Trên Windows Server:
sc create OilAutoInsertService binPath="C:\Services\OilAutoService\OilAutoService.exe"
sc config OilAutoInsertService start=auto
sc start OilAutoInsertService
```

### Dừng / Gỡ service
```bash
sc stop OilAutoInsertService
sc delete OilAutoInsertService
```

## Cấu hình

Chỉnh `appsettings.json`:
- `ServiceSettings.CheckIntervalMinutes`: Khoảng thời gian check (phút)
- `ServiceSettings.MaxParallelMachines`: Số máy xử lý song song tối đa
- `Machines[]`: Danh sách 8 máy với connection string

## Log

- Console output (khi chạy thủ công)
- File: `logs/oil-auto-service-yyyyMMdd.log` (giữ 30 ngày)

## Cấu trúc dự án

```
src/OilAutoService/
├── Program.cs                          # Entry point + DI
├── OilAutoInsertWorker.cs              # Background worker chính
├── Configuration/
│   └── ServiceSettings.cs             # Settings model
├── Models/
│   ├── PptGroupLot.cs                 # Đơn hàng
│   ├── PmtWeigh.cs                    # Tiêu chuẩn cân
│   ├── PptWeigh.cs                    # Data cân thực tế
│   ├── PptBarCodeRep.cs               # Bảng insert tem
│   └── ProcessedOrder.cs              # Tracking đã xử lý
└── Services/
    ├── IMachineOrderService.cs        # Interface query máy
    ├── MachineOrderService.cs         # Implement query
    ├── IOilLabelService.cs            # Interface insert tem
    └── OilLabelService.cs             # Implement insert + tracking
```
