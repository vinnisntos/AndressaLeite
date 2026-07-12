using System.ComponentModel.DataAnnotations;
using Postgrest.Models;

namespace AndressaLeite.Models
{
    [Postgrest.Attributes.Table("appointments")]
    public class Appointment : BaseModel
    {
        [Postgrest.Attributes.PrimaryKey("id", true)]
        public string Id { get; set; } = string.Empty;

        [Required]
        [Postgrest.Attributes.Column("client_id")]
        public string ClientId { get; set; } = string.Empty;

        [Required]
        [Postgrest.Attributes.Column("employee_id")]
        public string EmployeeId { get; set; } = string.Empty;

        [Required]
        [Postgrest.Attributes.Column("service_id")]
        public string ServiceId { get; set; } = string.Empty;

        [Required]
        [Postgrest.Attributes.Column("start_time")]
        public DateTime StartTime { get; set; }

        [Postgrest.Attributes.Column("end_time")]
        public DateTime EndTime { get; set; }

        /// <summary>
        /// pending | confirmed | completed | cancelled
        /// </summary>
        [Required]
        [RegularExpression("^(pending|confirmed|completed|cancelled)$",
            ErrorMessage = "Status inválido.")]
        [Postgrest.Attributes.Column("status")]
        public string Status { get; set; } = "pending";

        [StringLength(120, ErrorMessage = "Nome do atendido com no máximo 120 caracteres.")]
        [Postgrest.Attributes.Column("booked_for_name")]
        public string? BookedForName { get; set; }

        [RegularExpression(@"^\+?[1-9]\d{10,14}$",
            ErrorMessage = "Telefone do atendido inválido.")]
        [Postgrest.Attributes.Column("booked_for_phone")]
        public string? BookedForPhone { get; set; }

        [Range(0, 999999.99, ErrorMessage = "Receita estimada inválida.")]
        [Postgrest.Attributes.Column("estimated_revenue")]
        public decimal EstimatedRevenue { get; set; }

        [Range(0, 999999.99, ErrorMessage = "Receita real inválida.")]
        [Postgrest.Attributes.Column("actual_revenue")]
        public decimal ActualRevenue { get; set; }
    }
}
