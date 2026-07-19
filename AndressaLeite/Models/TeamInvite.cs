using AndressaLeite.Services;
using Postgrest.Models;

namespace AndressaLeite.Models
{
    /// <summary>
    /// Convite de equipe pendente (readme.txt 5.6) — tabela separada de
    /// profiles, mesmo padrão de platform_admins (0004): o profile real da
    /// profissional só nasce quando ela aceita o convite e define a senha
    /// (ver Pages/Auth/AceitarConvite.cshtml.cs). Token armazenado como
    /// hash SHA-256 (Services/EmailTokenService.cs), nunca em texto puro.
    /// </summary>
    [Postgrest.Attributes.Table("team_invites")]
    public class TeamInvite : BaseModel
    {
        [Postgrest.Attributes.PrimaryKey("id", true)]
        public string Id { get; set; } = string.Empty;

        [Postgrest.Attributes.Column("tenant_id")]
        public string TenantId { get; set; } = string.Empty;

        [Postgrest.Attributes.Column("email")]
        public string Email { get; set; } = string.Empty;

        [Postgrest.Attributes.Column("full_name")]
        public string FullName { get; set; } = string.Empty;

        [Postgrest.Attributes.Column("phone")]
        public string Phone { get; set; } = string.Empty;

        [Postgrest.Attributes.Column("token_hash")]
        public string TokenHash { get; set; } = string.Empty;

        // Todos os campos DateTime/DateTime? abaixo passam por
        // PostgrestTime.ToTrueUtc no setter — ver o mesmo comentário em
        // Models/Appointement.cs (readme.txt 12.2.a).

        private DateTime _expiresAt;
        [Postgrest.Attributes.Column("expires_at")]
        public DateTime ExpiresAt
        {
            get => _expiresAt;
            set => _expiresAt = PostgrestTime.ToTrueUtc(value);
        }

        /// <summary>Id do profile do admin que criou o convite.</summary>
        [Postgrest.Attributes.Column("created_by")]
        public string CreatedBy { get; set; } = string.Empty;

        private DateTime? _usedAt;
        [Postgrest.Attributes.Column("used_at")]
        public DateTime? UsedAt
        {
            get => _usedAt;
            set => _usedAt = PostgrestTime.ToTrueUtc(value);
        }

        private DateTime? _cancelledAt;
        [Postgrest.Attributes.Column("cancelled_at")]
        public DateTime? CancelledAt
        {
            get => _cancelledAt;
            set => _cancelledAt = PostgrestTime.ToTrueUtc(value);
        }

        private DateTime _createdAt;
        [Postgrest.Attributes.Column("created_at")]
        public DateTime CreatedAt
        {
            get => _createdAt;
            set => _createdAt = PostgrestTime.ToTrueUtc(value);
        }
    }
}
