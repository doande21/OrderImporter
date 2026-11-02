using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderImporter.Data;
using OrderImporter.Entities;
using OrderImporter.Options;

namespace OrderImporter.Services
{
    public class OrderImportWorker : BackgroundService
    {
        private readonly ImporterOptions _options;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<OrderImportWorker> _logger;

        // Đếm thống kê toàn phiên (F6)
        private int _successfulFilesCount = 0;
        private int _failedFilesCount = 0;
        private int _totalOrdersImported = 0;

        public OrderImportWorker(
            IOptions<ImporterOptions> options,
            IServiceScopeFactory scopeFactory,
            ILogger<OrderImportWorker> logger)
        {
            _options = options.Value;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("OrderImportWorker đã bắt đầu chạy nền.");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ProcessInboxFilesAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Lỗi hệ thống không mong muốn trong chu kỳ quét!");
                }

                int delaySeconds = _options.ScanIntervalSeconds > 0 ? _options.ScanIntervalSeconds : 10;
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
            }
        }
        private async Task ProcessInboxFilesAsync(CancellationToken stoppingToken)
        {
            if (!Directory.Exists(_options.InboxFolder)) return;

            // Lấy danh sách file *.csv trong Inbox sắp xếp theo thời gian tạo file (F2)
            var csvFiles = Directory.GetFiles(_options.InboxFolder, "*.csv")
                                    .Select(f => new FileInfo(f))
                                    .OrderBy(f => f.CreationTimeUtc)
                                    .ToList();

            if (!csvFiles.Any()) return;

            int batchImportedOrders = 0;
            int batchSuccessFiles = 0;
            int batchErrorFiles = 0;

            foreach (var fileInfo in csvFiles)
            {
                if (stoppingToken.IsCancellationRequested) break;

                _logger.LogInformation("Đang xử lý file: {FileName}", fileInfo.Name);

                try
                {
                    // Kiểm tra xem file có bị khóa không (System Error)
                    if (IsFileLocked(fileInfo.FullName))
                    {
                        _logger.LogWarning("File {FileName} đang bị khóa bởi tiến trình khác. Bỏ qua lượt này.", fileInfo.Name);
                        continue;
                    }
                    var result = await ProcessSingleCsvFileAsync(fileInfo, stoppingToken);

                    if (result.IsSuccess)
                    {
                        batchSuccessFiles++;
                        _successfulFilesCount++;
                        batchImportedOrders += result.ImportedCount;
                        _totalOrdersImported += result.ImportedCount;

                        // Di chuyển sang Processed (F4, F5)
                        string destPath = GetUniqueDestinationPath(_options.ProcessedFolder, fileInfo.Name);
                        File.Move(fileInfo.FullName, destPath);
                        _logger.LogInformation("Nhập thành công {Count} đơn hàng từ {FileName}. Đã chuyển file sang Processed.", result.ImportedCount, fileInfo.Name);
                    }
                    else
                    {
                        batchErrorFiles++;
                        _failedFilesCount++;

                        // Di chuyển file sang Error và ghi file .error.txt (F4, F5)
                        string destPath = GetUniqueDestinationPath(_options.ErrorFolder, fileInfo.Name);
                        File.Move(fileInfo.FullName, destPath);

                        string errorTxtPath = destPath + ".error.txt";
                        await File.WriteAllLinesAsync(errorTxtPath, result.ErrorMessages, Encoding.UTF8, stoppingToken);

                        _logger.LogWarning("File {FileName} chứa {ErrorCount} lỗi dữ liệu. Đã chuyển sang Error.", fileInfo.Name, result.ErrorMessages.Count);
                    }
                }
                catch (IOException ioEx)
                {
                    _logger.LogError(ioEx, "Lỗi hệ thống I/O khi truy cập file {FileName}. Giữ nguyên file tại Inbox để thử lại.", fileInfo.Name);
                }
                catch (Exception sysEx) when (sysEx is not OperationCanceledException)
                {
                    // Lỗi hệ thống (CSDL, Mạng...): Giữ nguyên file ở Inbox (F7)
                    _logger.LogError(sysEx, "Lỗi hệ thống khi xử lý file {FileName}. Giữ nguyên file tại Inbox để thử lại.", fileInfo.Name);
                }
            }

            // Ghi log tóm tắt sau lượt quét
            if (batchSuccessFiles > 0 || batchErrorFiles > 0)
            {
                _logger.LogInformation("Tóm tắt lượt quét: {SuccessFiles} file thành công, {ErrorFiles} file lỗi, nhập tổng cộng {OrdersCount} đơn hàng.",
                    batchSuccessFiles, batchErrorFiles, batchImportedOrders);
            }
        }

        private async Task<(bool IsSuccess, int ImportedCount, List<string> ErrorMessages)> ProcessSingleCsvFileAsync(FileInfo fileInfo, CancellationToken cancellationToken)
        {
            var lines = await File.ReadAllLinesAsync(fileInfo.FullName, Encoding.UTF8, cancellationToken);
            var errorMessages = new List<string>();
            var validOrdersToSave = new List<Order>();

            if (lines.Length <= 1)
            {
                errorMessages.Add("File rỗng hoặc chỉ có dòng tiêu đề.");
                return (false, 0, errorMessages);
            }
            var orderCodesInFile = new List<string>();
            for (int i = 1; i < lines.Length; i++)
            {
                var cols = lines[i].Split(',');
                if (cols.Length > 0 && !string.IsNullOrWhiteSpace(cols[0]))
                {
                    orderCodesInFile.Add(cols[0].Trim());
                }
            }

            // Tạo scope lấy OrderDbContext (Scoped) trong Singleton Worker (F4, DI Best Practice)
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<OrderDbContext>();

            // Lấy danh sách các OrderCode đã tồn tại trong CSDL
            var existingDbCodes = await dbContext.Orders
                .Where(o => orderCodesInFile.Contains(o.OrderCode))
                .Select(o => o.OrderCode)
                .ToHashSetAsync(cancellationToken);

            var seenInFileCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fileCodeToFirstLineMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            // Kiểm tra từng dòng (dòng 1 là tiêu đề, dữ liệu bắt đầu từ dòng 2)
            for (int lineIndex = 1; lineIndex < lines.Length; lineIndex++)
            {
                int lineNumber = lineIndex + 1; // Dòng hiển thị cho người dùng (1-based)
                string line = lines[lineIndex];
                if (string.IsNullOrWhiteSpace(line)) continue;

                var columns = line.Split(',');

                // F3-a: Kiểm tra số cột (đúng 6 cột)
                if (columns.Length != 6)
                {
                    errorMessages.Add($"Dòng {lineNumber}: Thiếu hoặc thừa cột (có {columns.Length}/6 cột).");
                    continue; // Tiếp tục kiểm tra các dòng khác
                }

                string orderCode = columns[0].Trim();
                string customerName = columns[1].Trim();
                string productName = columns[2].Trim();
                string quantityStr = columns[3].Trim();
                string unitPriceStr = columns[4].Trim();
                string orderDateStr = columns[5].Trim();

                // F3-b: Validate OrderCode
                if (string.IsNullOrWhiteSpace(orderCode) || !Regex.IsMatch(orderCode, @"^HD\d{4}$"))
                {
                    errorMessages.Add($"Dòng {lineNumber}: OrderCode '{orderCode}' không đúng định dạng (phải là HD + 4 chữ số).");
                }
                else
                {
                    // Trùng trong cùng file
                    if (seenInFileCodes.Contains(orderCode))
                    {
                        int previousLine = fileCodeToFirstLineMap[orderCode];
                        errorMessages.Add($"Dòng {lineNumber}: OrderCode '{orderCode}' trùng với dòng {previousLine} trong cùng file.");
                    }
                    else
                    {
                        seenInFileCodes.Add(orderCode);
                        fileCodeToFirstLineMap[orderCode] = lineNumber;
                    }

                    // Trùng với CSDL
                    if (existingDbCodes.Contains(orderCode))
                    {
                        errorMessages.Add($"Dòng {lineNumber}: OrderCode '{orderCode}' đã tồn tại trong CSDL.");
                    }
                }

                // F3-c: CustomerName & ProductName
                if (string.IsNullOrWhiteSpace(customerName))
                {
                    errorMessages.Add($"Dòng {lineNumber}: Thiếu CustomerName.");
                }
                else if (customerName.Length > 100)
                {
                    errorMessages.Add($"Dòng {lineNumber}: CustomerName dài quá 100 ký tự.");
                }

                if (string.IsNullOrWhiteSpace(productName))
                {
                    errorMessages.Add($"Dòng {lineNumber}: Thiếu ProductName.");
                }
                else if (productName.Length > 100)
                {
                    errorMessages.Add($"Dòng {lineNumber}: ProductName dài quá 100 ký tự.");
                }
                if (!int.TryParse(quantityStr, out int quantity) || quantity < 1 || quantity > 1000)
                {
                    errorMessages.Add($"Dòng {lineNumber}: Quantity phải là số nguyên từ 1 đến 1000.");
                }

                // F3-e: UnitPrice (>= 0)
                if (!decimal.TryParse(unitPriceStr, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal unitPrice) || unitPrice < 0)
                {
                    errorMessages.Add($"Dòng {lineNumber}: UnitPrice không hợp lệ hoặc nhỏ hơn 0.");
                }

                // F3-f: OrderDate (yyyy-MM-dd và <= hiện tại)
                if (!DateTime.TryParseExact(orderDateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime orderDate))
                {
                    errorMessages.Add($"Dòng {lineNumber}: OrderDate '{orderDateStr}' không hợp lệ (phải đúng dạng yyyy-MM-dd).");
                }
                else if (orderDate.Date > DateTime.Today)
                {
                    errorMessages.Add($"Dòng {lineNumber}: OrderDate {orderDateStr} lớn hơn ngày hiện tại.");
                }

                // Nếu dòng hiện tại không có lỗi nào, tạo object Order
                if (errorMessages.All(e => !e.StartsWith($"Dòng {lineNumber}:")))
                {
                    validOrdersToSave.Add(new Order
                    {
                        OrderCode = orderCode,
                        CustomerName = customerName,
                        ProductName = productName,
                        Quantity = quantity,
                        UnitPrice = unitPrice,
                        OrderDate = orderDate,
                        SourceFile = fileInfo.Name,
                        ImportedAt = DateTime.Now
                    });
                }
            }

            // F4: Nhập "Tất cả hoặc không" (All-or-Nothing)
            if (errorMessages.Any())
            {
                return (false, 0, errorMessages);
            }

            // Tất cả các dòng hợp lệ -> Lưu vào CSDL trong 1 Transaction
            using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                await dbContext.Orders.AddRangeAsync(validOrdersToSave, cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                return (true, validOrdersToSave.Count, new List<string>());
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(cancellationToken);
                _logger.LogError(ex, "Lỗi khi Commit Transaction vào CSDL cho file {FileName}", fileInfo.Name);
                throw; // Ném ra ngoài để xếp vào Lỗi Hệ Thống (F7)
            }
        }
        private string GetUniqueDestinationPath(string targetFolder, string originalFileName)
        {
            string destPath = Path.Combine(targetFolder, originalFileName);
            if (!File.Exists(destPath)) return destPath;

            string fileNameWithoutExt = Path.GetFileNameWithoutExtension(originalFileName);
            string ext = Path.GetExtension(originalFileName);
            string timeStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

            string newFileName = $"{fileNameWithoutExt}_{timeStamp}{ext}";
            return Path.Combine(targetFolder, newFileName);
        }

        private bool IsFileLocked(string filePath)
        {
            try
            {
                using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.None);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
        }

        // F6: Thống kê toàn phiên khi dừng dịch vụ
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("==================================================");
            _logger.LogInformation("OrderImporter STOPPED.");
            _logger.LogInformation("THỐNG KÊ TOÀN PHIÊN BẢO TRÌ/CHẠY:");
            _logger.LogInformation(" - Số file thành công  : {SuccessCount}", _successfulFilesCount);
            _logger.LogInformation(" - Số file lỗi         : {FailCount}", _failedFilesCount);
            _logger.LogInformation(" - Tổng số đơn đã nhập : {TotalOrders}", _totalOrdersImported);
            _logger.LogInformation("==================================================");

            await base.StopAsync(cancellationToken);
        }
    }
}