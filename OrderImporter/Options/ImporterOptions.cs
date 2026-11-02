namespace OrderImporter.Options
{
    public class ImporterOptions
    {
        public string InboxFolder { get; set; } = @"C:\OrderImporter\Inbox";
        public string ProcessedFolder { get; set; } = @"C:\OrderImporter\Processed";
        public string ErrorFolder { get; set; } = @"C:\OrderImporter\Error";
        public int ScanIntervalSeconds { get; set; } = 10;
    }
}