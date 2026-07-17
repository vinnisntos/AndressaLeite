using System.Security.Cryptography;
using System.Text;

namespace AndressaLeite.Services
{
    /// <summary>
    /// TOTP (RFC 6238, sobre HOTP da RFC 4226) pro 2FA do superadmin —
    /// implementação própria (sem pacote externo) porque é só HMAC-SHA1 +
    /// truncamento dinâmico, ambos já disponíveis no BCL. Validada antes
    /// de entrar no código contra os 5 vetores de teste oficiais do RFC
    /// 6238 Appendix B (todos bateram) e um round-trip de Base32 — ver
    /// readme.txt seção 3.8 pra detalhes.
    /// </summary>
    public static class TotpService
    {
        private const int SecretLengthBytes = 20; // 160 bits — padrão pra TOTP/SHA1
        private const int CodeDigits = 6;
        internal const int StepSeconds = 30; // internal só pra teste calcular o step esperado

        /// <summary>Gera um novo segredo aleatório, em Base32 (formato que apps autenticadores esperam).</summary>
        public static string GenerateSecret()
        {
            var bytes = RandomNumberGenerator.GetBytes(SecretLengthBytes);
            return Base32Encode(bytes);
        }

        /// <summary>
        /// URI otpauth:// pro QR code — apps como Google Authenticator/Authy
        /// escaneiam isso pra configurar a conta automaticamente.
        /// </summary>
        public static string BuildOtpAuthUri(string secret, string accountEmail, string issuer = "MarcAi")
        {
            var label = Uri.EscapeDataString($"{issuer}:{accountEmail}");
            var issuerEncoded = Uri.EscapeDataString(issuer);
            return $"otpauth://totp/{label}?secret={secret}&issuer={issuerEncoded}&algorithm=SHA1&digits={CodeDigits}&period={StepSeconds}";
        }

        /// <summary>
        /// Valida um código de 6 dígitos contra o segredo, com tolerância
        /// de +/-1 passo (30s pra cada lado) pra absorver pequena diferença
        /// de relógio entre o celular e o servidor.
        /// </summary>
        public static bool ValidateCode(string secret, string? code, int allowedDriftSteps = 1)
            => ValidateCode(secret, code, DateTimeOffset.UtcNow, allowedDriftSteps);

        /// <summary>
        /// Overload com horário explícito — existe pra permitir teste
        /// determinístico contra os vetores oficiais do RFC 6238 Appendix B
        /// (que usam timestamps fixos), sem depender do relógio real.
        /// A validação em produção sempre usa a outra sobrecarga (UtcNow).
        /// </summary>
        public static bool ValidateCode(string secret, string? code, DateTimeOffset asOf, int allowedDriftSteps = 1)
        {
            if (string.IsNullOrWhiteSpace(code) || code.Length != CodeDigits || !code.All(char.IsDigit))
            {
                return false;
            }

            byte[] secretBytes;
            try
            {
                secretBytes = Base32Decode(secret);
            }
            catch
            {
                return false;
            }

            var currentStep = asOf.ToUnixTimeSeconds() / StepSeconds;

            for (int drift = -allowedDriftSteps; drift <= allowedDriftSteps; drift++)
            {
                if (ComputeCode(secretBytes, currentStep + drift) == code)
                {
                    return true;
                }
            }

            return false;
        }

        // internal (não private) pelo mesmo motivo do Base32Encode/Decode
        // acima — permite ao teste calcular o código esperado sem duplicar
        // a lógica de HMAC, num teste de round-trip completo e realista.
        internal static string ComputeCode(byte[] secretBytes, long step)
        {
            var stepBytes = BitConverter.GetBytes(step);
            if (BitConverter.IsLittleEndian) Array.Reverse(stepBytes);

            using var hmac = new HMACSHA1(secretBytes);
            var hash = hmac.ComputeHash(stepBytes);

            int offset = hash[^1] & 0x0F;
            int binaryCode = ((hash[offset] & 0x7F) << 24)
                | ((hash[offset + 1] & 0xFF) << 16)
                | ((hash[offset + 2] & 0xFF) << 8)
                | (hash[offset + 3] & 0xFF);

            int code = binaryCode % 1_000_000;
            return code.ToString("D6");
        }

        // internal (não private) só pra permitir teste direto de round-trip
        // do Base32 em AndressaLeite.Tests — ver InternalsVisibleTo no
        // AndressaLeite.csproj. Continua não fazendo parte da API pública
        // do serviço (GenerateSecret/ValidateCode/BuildOtpAuthUri).
        internal static string Base32Encode(byte[] data)
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            var sb = new StringBuilder();
            int bits = 0, value = 0;
            foreach (var b in data)
            {
                value = (value << 8) | b;
                bits += 8;
                while (bits >= 5)
                {
                    sb.Append(alphabet[(value >> (bits - 5)) & 0x1F]);
                    bits -= 5;
                }
            }
            if (bits > 0)
            {
                sb.Append(alphabet[(value << (5 - bits)) & 0x1F]);
            }
            return sb.ToString();
        }

        internal static byte[] Base32Decode(string base32)
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            base32 = base32.TrimEnd('=').ToUpperInvariant();
            var bytes = new List<byte>();
            int bits = 0, value = 0;
            foreach (var c in base32)
            {
                int idx = alphabet.IndexOf(c);
                if (idx < 0) continue;
                value = (value << 5) | idx;
                bits += 5;
                if (bits >= 8)
                {
                    bytes.Add((byte)((value >> (bits - 8)) & 0xFF));
                    bits -= 8;
                }
            }
            return bytes.ToArray();
        }
    }
}
