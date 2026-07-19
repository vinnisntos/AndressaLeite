using AndressaLeite.Services;
using Postgrest.Models;

namespace AndressaLeite.Models
{
    [Postgrest.Attributes.Table("tenants")]
    public class Tenant : BaseModel
    {
        [Postgrest.Attributes.PrimaryKey("id", true)]
        public string Id { get; set; } = string.Empty;

        [Postgrest.Attributes.Column("slug")]
        public string Slug { get; set; } = string.Empty;

        [Postgrest.Attributes.Column("name")]
        public string Name { get; set; } = string.Empty;

        [Postgrest.Attributes.Column("is_active")]
        public bool IsActive { get; set; } = true;

        // Setter passa por PostgrestTime.ToTrueUtc — ver comentário
        // completo em Models/Appointement.cs (readme.txt 12.2.a).
        private DateTime _createdAt;
        [Postgrest.Attributes.Column("created_at")]
        public DateTime CreatedAt
        {
            get => _createdAt;
            set => _createdAt = PostgrestTime.ToTrueUtc(value);
        }

        // Horário de funcionamento do salão. Guardado como string
        // ("HH:mm:ss", igual ao formato que o Postgres devolve pra coluna
        // `time` via PostgREST) em vez de TimeSpan/TimeOnly — evita
        // depender de como o driver serializa esses tipos, que não foi
        // validado neste projeto. Parsear com TimeSpan.Parse ao usar.

        [Postgrest.Attributes.Column("business_open_time")]
        public string BusinessOpenTime { get; set; } = "09:00:00";

        [Postgrest.Attributes.Column("business_close_time")]
        public string BusinessCloseTime { get; set; } = "19:00:00";

        /// <summary>Null = sem intervalo de almoço configurado.</summary>
        [Postgrest.Attributes.Column("lunch_start_time")]
        public string? LunchStartTime { get; set; }

        [Postgrest.Attributes.Column("lunch_end_time")]
        public string? LunchEndTime { get; set; }
    }
}
