using Postgrest.Models;

namespace AndressaLeite.Models
{
    /// <summary>
    /// Conta de superadmin da plataforma MarcAi — cross-tenant, não
    /// pertence a nenhum salão (por isso não usa Models/Profile.cs, que
    /// exige tenant_id). Usada só pelo painel /SuperAdmin.
    /// </summary>
    [Postgrest.Attributes.Table("platform_admins")]
    public class PlatformAdmin : BaseModel
    {
        [Postgrest.Attributes.PrimaryKey("id", true)]
        public string Id { get; set; } = string.Empty;

        [Postgrest.Attributes.Column("email")]
        public string Email { get; set; } = string.Empty;

        [Postgrest.Attributes.Column("password_hash")]
        public string PasswordHash { get; set; } = string.Empty;

        [Postgrest.Attributes.Column("full_name")]
        public string FullName { get; set; } = string.Empty;

        [Postgrest.Attributes.Column("created_at")]
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Segredo TOTP em Base32. Fica preenchido mas "pendente" enquanto
        /// TotpEnabled=false (aguardando confirmação de um código válido
        /// em /SuperAdmin/Security) — só passa a ser exigido no login
        /// depois de confirmado.
        /// </summary>
        [Postgrest.Attributes.Column("totp_secret")]
        public string? TotpSecret { get; set; }

        [Postgrest.Attributes.Column("totp_enabled")]
        public bool TotpEnabled { get; set; }
    }
}
