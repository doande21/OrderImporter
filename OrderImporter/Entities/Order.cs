using System;
using System.ComponentModel.DataAnnotations;

namespace OrderImporter.Entities
{
    public class Order
    {
        public int Id { get; set; }

        [Required]
        [MaxLength(10)]
        public string OrderCode { get; set; } = string.Empty;

        [Required]
        [MaxLength(100)]
        public string CustomerName { get; set; } = string.Empty;

        [Required]
        [MaxLength(100)]
        public string ProductName { get; set; } = string.Empty;

        public int Quantity { get; set; }

        public decimal UnitPrice { get; set; }

        public DateTime OrderDate { get; set; }

        [Required]
        [MaxLength(255)]
        public string SourceFile { get; set; } = string.Empty;
        public DateTime ImportedAt { get; set; } = DateTime.Now;
    }
}