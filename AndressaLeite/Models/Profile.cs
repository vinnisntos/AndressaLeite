using System.ComponentModel.DataAnnotations;
using Postgrest.Models;

namespace AndressaLeite.Models
{
    [Postgrest.Attributes.Table("profiles")]
    public class Profile : BaseModel
    {
        // 🔴 CORRIGIDO: Alterado para 'true' para forçar o C# a enviar o GUID gerado no cadastro
        [Postgrest.Attributes.PrimaryKey("id", true)]
        public string Id { get; set; } = string.Empty;

        [Required(ErrorMessage = "O nome completo é obrigatório.")]
        [StringLength(120, MinimumLength = 2, ErrorMessage = "O nome deve ter entre 2 e 120 caracteres.")]
        [RegularExpression(@"^[\p{L}\p{M}'\.\- ]+$",
            ErrorMessage = "O nome contém caracteres inválidos.")]
        [Postgrest.Attributes.Column("full_name")]
        public string FullName { get; set; } = string.Empty;

        /// <summary>
        /// Role no sistema. Valores aceitos: admin, employee, client, inactive.
        /// Não confiar em input do cliente para popular isto — sempre ler do JWT
        /// ou do banco após checagem server-side.
        /// </summary>
        [Required]
        [RegularExpression("^(admin|employee|client|inactive)$",
            ErrorMessage = "Role inválida.")]
        [Postgrest.Attributes.Column("role")]
        public string Role { get; set; } = "client";

        /// <summary>
        /// Telefone armazenado apenas com dígitos. Validado por regex no
        /// PageModel de Cadastro antes de persistir.
        /// </summary>
        [RegularExpression(@"^\+?[1-9]\d{10,14}$",
            ErrorMessage = "Telefone inválido. Use DDI + DDD + número (somente dígitos).")]
        [Postgrest.Attributes.Column("phone")]
        public string Phone { get; set; } = string.Empty;

        // =================================================================
        // NOVOS CAMPOS: Para controle de autenticação manual via C#
        // =================================================================

        [Required(ErrorMessage = "O e-mail é obrigatório.")]
        [EmailAddress(ErrorMessage = "Informe um e-mail válido.")]
        [Postgrest.Attributes.Column("email")]
        public string Email { get; set; } = string.Empty;

        [Postgrest.Attributes.Column("password_hash")]
        public string PasswordHash { get; set; } = string.Empty;

        /// <summary>
        /// Salão (tenant) ao qual este perfil pertence. Toda query precisa
        /// filtrar por isto — ver Services/CurrentTenant.cs.
        /// </summary>
        [Required]
        [Postgrest.Attributes.Column("tenant_id")]
        public string TenantId { get; set; } = string.Empty;
    }
}