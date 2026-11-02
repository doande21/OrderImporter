using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OrderImporter.Data;
using OrderImporter.Options;
using OrderImporter.Services;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// 1. Cấu hình Options Pattern
builder.Services.Configure<ImporterOptions>(builder.Configuration.GetSection("Importer"));

// 2. Đăng ký DbContext (Scoped)
builder.Services.AddDbContext<OrderDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("OrderDb");
    options.UseSqlServer(connectionString);
});

// 3. Đăng ký 2 Hosted Services theo ĐÚNG THỨ TỰ bắt buộc:
// - FolderInitializer (IHostedService) chạy trước để tạo thư mục Inbox/Processed/Error
builder.Services.AddHostedService<FolderInitializer>();

// - OrderImportWorker (BackgroundService) chạy sau để quét định kỳ
builder.Services.AddHostedService<OrderImportWorker>();

// 4. Đăng ký dịch vụ Windows Service
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "OrderImporter";
});
var host = builder.Build();
host.Run();