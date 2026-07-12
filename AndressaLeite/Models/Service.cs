using Postgrest.Models;

namespace AndressaLeite.Models
{
    [Postgrest.Attributes.Table("services")]
    public class Service : BaseModel
    {
        [Postgrest.Attributes.PrimaryKey("id", true)]
        public string Id { get; set; } = string.Empty;

        [Postgrest.Attributes.Column("name")]
        public string Name { get; set; } = string.Empty;

        [Postgrest.Attributes.Column("price")]
        public decimal Price { get; set; }

        [Postgrest.Attributes.Column("duration_minutes")]
        public int DurationMinutes { get; set; }

        [Postgrest.Attributes.Column("is_active")]
        public bool IsActive { get; set; } = true;
    }
}