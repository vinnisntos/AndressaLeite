using System.Security.Cryptography;
using System.Text;

namespace AndressaLeite.Services
{
    /// <summary>
    /// Geração/validação de tokens de ação por e-mail (reset de senha,
    /// verificação de e-mail, convite de equipe) — classe estática sem
    /// estado, mesmo molde de Services/TotpService.cs. Só lógica pura
    /// (matemática de hash/expiração), sem I/O — é a parte desta feature
    /// que vale testar automaticamente, o resto (tudo que fala com o
    /// Supabase/Resend) fica fora, mesmo escopo de teste já praticado no
    /// projeto (ver AndressaLeite.Tests).
    /// </summary>
    public static class EmailTokenService
    {
        private const int TokenLengthBytes = 32; // 256 bits

        /// <summary>
        /// Gera um token novo. O token CRU vai inteiro na URL do e-mail —
        /// nunca é persistido; só o hash é gravado no banco (ver migration
        /// 0007/0008), porque um token de ação é a única credencial
        /// necessária pra agir e trafega por lugares que um segredo
        /// contínuo (como o totp_secret) nunca trafega — logs de proxy,
        /// histórico de navegador, crawler de preview de e-mail.
        /// </summary>
        public static (string RawToken, string TokenHash) GenerateToken()
        {
            var bytes = RandomNumberGenerator.GetBytes(TokenLengthBytes);
            // Base64url (sem +, /, = ) — vai direto numa query string sem
            // precisar de mais encoding.
            var rawToken = Convert.ToBase64String(bytes)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
            return (rawToken, Hash(rawToken));
        }

        /// <summary>Hash SHA-256 em hex minúsculo — o que fica gravado no banco.</summary>
        public static string Hash(string rawToken)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        /// <summary>
        /// True se o token já expirou (ou nunca teve validade definida).
        /// asOfUtc existe só pra permitir teste determinístico, mesmo
        /// padrão do overload com DateTimeOffset explícito em
        /// TotpService.ValidateCode — produção sempre usa o default (UtcNow).
        /// </summary>
        public static bool IsExpired(DateTime? expiresAtUtc, DateTime? asOfUtc = null)
        {
            if (expiresAtUtc is null) return true;
            var asOf = asOfUtc ?? DateTime.UtcNow;
            return asOf >= expiresAtUtc.Value;
        }
    }
}
