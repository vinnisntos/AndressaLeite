using System.Security.Claims;

namespace AndressaLeite.Services
{
    /// <summary>
    /// Helpers estáticos para autorização. Mantém em um único lugar o mapeamento
    /// role → URL padrão e as validações de URL e identidade que o resto do app
    /// precisa para evitar redirecionamentos inseguros (open-redirect) e checagens
    /// de posse inconsistentes.
    /// </summary>
    public static class AuthorizationService
    {
        /// <summary>
        /// Nome da claim customizada que grava o tenant (salão) do usuário
        /// no cookie de autenticação. Gravada no login/cadastro, lida pelo
        /// middleware de segurança que compara com o CurrentTenant
        /// resolvido pelo subdomínio da requisição (ver Program.cs).
        /// </summary>
        public const string TenantClaimType = "tenant_id";

        /// <summary>
        /// Mapeamento centralizado: dado uma role conhecida, retorna a página
        /// padrão pós-login. Roles desconhecidas caem em "/" (Index).
        /// </summary>
        public static string GetDefaultLandingForRole(string? role) => (role ?? "").ToLowerInvariant() switch
        {
            "admin" => "/Admin/DashAdmin",
            "employee" => "/Profissional/DashProfissional",
            "client" => "/Cliente/DashCliente",
            _ => "/"
        };

        /// <summary>
        /// Valida se uma URL fornecida via query string (ReturnUrl) é segura
        /// para redirecionar. Rejeita URLs absolutas, scheme-relative (//evil.com)
        /// e backslash tricks (/\\evil.com) — todos vetores clássicos de
        /// open-redirect que <c>Url.IsLocalUrl</c> por si só não cobre 100%.
        /// </summary>
        public static bool IsLocalSafeUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            // Rejeita scheme-relative //host/path e backslash trick
            if (url.StartsWith("//", StringComparison.Ordinal)) return false;
            if (url.StartsWith("/\\", StringComparison.Ordinal)) return false;
            if (url.StartsWith("\\", StringComparison.Ordinal)) return false;
            // A partir daqui, delega ao ASP.NET (cobre paths absolutos ao host)
            return Uri.TryCreate(url, UriKind.Relative, out _);
        }

        /// <summary>
        /// Extrai o identificador do usuário (claim NameIdentifier) de forma
        /// segura. Retorna false se não houver claim ou se ela não for um Guid válido.
        /// </summary>
        public static bool TryGetUserId(ClaimsPrincipal? user, out Guid id)
        {
            id = Guid.Empty;
            if (user is null) return false;
            var raw = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(raw)) return false;
            return Guid.TryParse(raw, out id);
        }

        /// <summary>
        /// Lê a role do principal. Nunca retorna null — usa string.Empty se ausente.
        /// </summary>
        public static string GetRole(ClaimsPrincipal? user)
            => user?.FindFirstValue(ClaimTypes.Role) ?? string.Empty;

        /// <summary>
        /// Extrai o id do tenant (claim TenantClaimType) de forma segura.
        /// Retorna false se não houver a claim.
        /// </summary>
        public static bool TryGetTenantId(ClaimsPrincipal? user, out string tenantId)
        {
            tenantId = string.Empty;
            if (user is null) return false;
            var raw = user.FindFirstValue(TenantClaimType);
            if (string.IsNullOrWhiteSpace(raw)) return false;
            tenantId = raw;
            return true;
        }

        /// <summary>
        /// Roles conhecidas do sistema. Útil para validações de input em
        /// endpoints de mutação (ex.: "atualizar perfil só pode setar uma
        /// dessas roles se for admin").
        /// </summary>
        public static readonly HashSet<string> KnownRoles =
            new(StringComparer.OrdinalIgnoreCase) { "admin", "employee", "client", "inactive" };
    }
}
