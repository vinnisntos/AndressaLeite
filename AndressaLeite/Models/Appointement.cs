using System.ComponentModel.DataAnnotations;
using AndressaLeite.Services;
using Postgrest.Models;

namespace AndressaLeite.Models
{
    [Postgrest.Attributes.Table("appointments")]
    public class Appointment : BaseModel
    {
        [Postgrest.Attributes.PrimaryKey("id", true)]
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Nulo quando o agendamento foi criado manualmente pela
        /// profissional/admin pra alguém sem conta de cliente (usa
        /// BookedForName/BookedForPhone nesse caso — ver Fase 5 do
        /// roadmap no readme.txt).
        /// </summary>
        [Postgrest.Attributes.Column("client_id")]
        public string? ClientId { get; set; }

        [Required]
        [Postgrest.Attributes.Column("employee_id")]
        public string EmployeeId { get; set; } = string.Empty;

        [Required]
        [Postgrest.Attributes.Column("service_id")]
        public string ServiceId { get; set; } = string.Empty;

        private DateTime _startTime;
        /// <summary>
        /// O setter passa por PostgrestTime.ToTrueUtc porque o
        /// postgrest-csharp devolve timestamptz já convertido pro fuso
        /// LOCAL da máquina, mas com Kind=Unspecified em vez de Kind=Utc
        /// — sem essa correção, qualquer .ToLocalTime()/.ToUniversalTime()
        /// aplicado depois (nas views, no WhatsApp reminder, etc.) fica
        /// errado pelo offset local (achado da rodada de e-mail
        /// transacional, readme.txt 12.2.a). Não afeta a escrita: um
        /// DateTime recém-criado com Kind=Utc passa pela função sem
        /// nenhuma mudança.
        /// </summary>
        [Required]
        [Postgrest.Attributes.Column("start_time")]
        public DateTime StartTime
        {
            get => _startTime;
            set => _startTime = PostgrestTime.ToTrueUtc(value);
        }

        private DateTime _endTime;
        [Postgrest.Attributes.Column("end_time")]
        public DateTime EndTime
        {
            get => _endTime;
            set => _endTime = PostgrestTime.ToTrueUtc(value);
        }

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

        /// <summary>
        /// Salão (tenant) dono deste agendamento. Toda query precisa
        /// filtrar por isto — ver Services/CurrentTenant.cs.
        /// </summary>
        [Postgrest.Attributes.Column("tenant_id")]
        public string TenantId { get; set; } = string.Empty;

        /// <summary>
        /// dinheiro | pix | cartao_debito | cartao_credito | outro.
        /// Preenchida na conclusão do atendimento (ver check constraint
        /// na migration 0003).
        /// </summary>
        [RegularExpression("^(dinheiro|pix|cartao_debito|cartao_credito|outro)$",
            ErrorMessage = "Forma de pagamento inválida.")]
        [Postgrest.Attributes.Column("payment_method")]
        public string? PaymentMethod { get; set; }

        /// <summary>
        /// Observações registradas ao concluir o atendimento (ex.:
        /// preferências da cliente, motivo de desconto).
        /// </summary>
        [StringLength(500, ErrorMessage = "Observações com no máximo 500 caracteres.")]
        [Postgrest.Attributes.Column("notes")]
        public string? Notes { get; set; }

        /// <summary>
        /// Marca quando o lembrete automático por e-mail foi enviado (ver
        /// Services/AppointmentReminderService.cs) — null = ainda não
        /// enviado. Garante que o job de polling não reenvie o mesmo
        /// lembrete a cada tick (readme.txt 5.3).
        /// </summary>
        private DateTime? _reminderSentAt;
        [Postgrest.Attributes.Column("reminder_sent_at")]
        public DateTime? ReminderSentAt
        {
            get => _reminderSentAt;
            set => _reminderSentAt = PostgrestTime.ToTrueUtc(value);
        }
    }
}
