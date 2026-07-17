using Postgrest.Models;

namespace AndressaLeite.Models
{
    /// <summary>
    /// Override opcional de preço/duração que uma profissional define pra
    /// si mesma sobre um serviço do catálogo do tenant (Models/Service.cs).
    /// Quando não existe linha (ou Price/DurationMinutes ficam nulos), vale
    /// o valor padrão definido pelo Admin em "services" — ver Fase 4 do
    /// roadmap no readme.txt.
    /// </summary>
    [Postgrest.Attributes.Table("professional_services")]
    public class ProfessionalService : BaseModel
    {
        [Postgrest.Attributes.PrimaryKey("id", true)]
        public string Id { get; set; } = string.Empty;

        [Postgrest.Attributes.Column("tenant_id")]
        public string TenantId { get; set; } = string.Empty;

        [Postgrest.Attributes.Column("employee_id")]
        public string EmployeeId { get; set; } = string.Empty;

        [Postgrest.Attributes.Column("service_id")]
        public string ServiceId { get; set; } = string.Empty;

        /// <summary>Preço personalizado da profissional. Null = usa o padrão do catálogo.</summary>
        [Postgrest.Attributes.Column("price")]
        public decimal? Price { get; set; }

        /// <summary>Duração personalizada da profissional. Null = usa o padrão do catálogo.</summary>
        [Postgrest.Attributes.Column("duration_minutes")]
        public int? DurationMinutes { get; set; }

        [Postgrest.Attributes.Column("is_active")]
        public bool IsActive { get; set; } = true;
    }
}
