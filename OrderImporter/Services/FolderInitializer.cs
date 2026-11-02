using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderImporter.Options;

namespace OrderImporter.Services
{
    public class FolderInitializer : IHostedService
    {
        private readonly ImporterOptions _options;
        private readonly ILogger<FolderInitializer> _logger;

        public FolderInitializer(IOptions<ImporterOptions> options, ILogger<FolderInitializer> logger)
        {
            _options = options.Value;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("==================================================");
            _logger.LogInformation("Khởi động dịch vụ OrderImporter...");
            _logger.LogInformation("Cấu hình thư mục Inbox: {Inbox}", _options.InboxFolder);
            _logger.LogInformation("Cấu hình thư mục Processed: {Processed}", _options.ProcessedFolder);
            _logger.LogInformation("Cấu hình thư mục Error: {Error}", _options.ErrorFolder);
            _logger.LogInformation("Chu kỳ quét: {Interval} giây", _options.ScanIntervalSeconds);
            _logger.LogInformation("==================================================");
            EnsureDirectoryExists(_options.InboxFolder);
            EnsureDirectoryExists(_options.ProcessedFolder);
            EnsureDirectoryExists(_options.ErrorFolder);

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("FolderInitializer đã dừng.");
            return Task.CompletedTask;
        }

        private void EnsureDirectoryExists(string path)
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
                _logger.LogInformation("Đã tự động tạo thư mục: {Path}", path);
            }
        }
    }
}