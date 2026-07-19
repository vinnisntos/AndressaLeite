using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Security.Claims;
using System.Text.RegularExpressions;
using AndressaLeite.Models;
using AndressaLeite.Services;
using QRCoder;
using BCrypt.Net;

namespace AndressaLeite.Pages.SuperAdmin
{
    /// <summary>
    /// Troca de senha + 2FA (TOTP) da conta de superadmin — a mais
    /// poderosa do sistema (liga/desliga qualquer salão), então é a única
    /// que tem essa tela hoje (Profile.cshtml é pra conta de tenant, não
    /// serve aqui). Ver Services/TotpService.cs pro algoritmo.
    /// </summary>
    [Authorize(Policy = "SuperAdminOnly")]
    public class SecurityModel : PageModel
    {
        private readonly Supabase.Client _supabase;
        private readonly ILogger<SecurityModel> _logger;

        public SecurityModel(Supabase.Client supabase, ILogger<SecurityModel> logger)
        {
            _supabase = supabase;
            _logger = logger;
        }

        public bool TotpEnabled { get; set; }
        /// <summary>Segredo gerado mas ainda não confirmado com um código válido.</summary>
        public bool TotpPending { get; set; }
        public string? QrCodeDataUri { get; set; }
        public string? ManualEntryKey { get; set; }

        [TempData] public string? SuccessMessage { get; set; }
        [TempData] public string? ErrorMessage { get; set; }

        public async Task<IActionResult> OnGetAsync()
        {
            var admin = await GetCurrentAdminAsync();
            if (admin is null) return Forbid();

            TotpEnabled = admin.TotpEnabled;
            TotpPending = !admin.TotpEnabled && !string.IsNullOrEmpty(admin.TotpSecret);

            if (TotpPending)
            {
                var otpAuthUri = TotpService.BuildOtpAuthUri(admin.TotpSecret!, admin.Email);
                QrCodeDataUri = BuildQrDataUri(otpAuthUri);
                ManualEntryKey = admin.TotpSecret;
            }

            return Page();
        }

        /// <summary>POST: troca a senha (exige a senha atual).</summary>
        public async Task<IActionResult> OnPostChangePasswordAsync(
            [FromForm] string CurrentPassword,
            [FromForm] string NewPassword,
            [FromForm] string ConfirmPassword)
        {
            var admin = await GetCurrentAdminAsync();
            if (admin is null) return Forbid();

            if (string.IsNullOrEmpty(admin.PasswordHash) || !BCrypt.Net.BCrypt.Verify(CurrentPassword, admin.PasswordHash))
            {
                ErrorMessage = "Senha atual incorreta.";
                return RedirectToPage();
            }

            if (string.IsNullOrWhiteSpace(NewPassword) || NewPassword.Length < 8)
            {
                ErrorMessage = "A nova senha deve ter no mínimo 8 caracteres.";
                return RedirectToPage();
            }

            if (!Regex.IsMatch(NewPassword, @"[A-Za-z]") || !Regex.IsMatch(NewPassword, @"\d"))
            {
                ErrorMessage = "A nova senha deve conter pelo menos uma letra e um número.";
                return RedirectToPage();
            }

            if (NewPassword != ConfirmPassword)
            {
                ErrorMessage = "As senhas informadas não coincidem.";
                return RedirectToPage();
            }

            try
            {
                await _supabase.From<PlatformAdmin>()
                    .Where(x => x.Id == admin.Id)
                    .Set(x => x.PasswordHash, BCrypt.Net.BCrypt.HashPassword(NewPassword))
                    .Update();
                SuccessMessage = "Senha alterada com sucesso.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao trocar senha do superadmin {AdminId}", admin.Id);
                ErrorMessage = "Não foi possível trocar a senha agora.";
            }

            return RedirectToPage();
        }

        /// <summary>
        /// POST: gera um novo segredo TOTP (fica "pendente" até um código
        /// válido confirmar em OnPostConfirmTotpAsync).
        /// </summary>
        public async Task<IActionResult> OnPostGenerateTotpAsync()
        {
            var admin = await GetCurrentAdminAsync();
            if (admin is null) return Forbid();

            try
            {
                var newSecret = TotpService.GenerateSecret();
                await _supabase.From<PlatformAdmin>()
                    .Where(x => x.Id == admin.Id)
                    .Set(x => x.TotpSecret, newSecret)
                    .Set(x => x.TotpEnabled, false)
                    .Update();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao gerar segredo TOTP pro superadmin {AdminId}", admin.Id);
                ErrorMessage = "Não foi possível iniciar a configuração do 2FA agora.";
            }

            return RedirectToPage();
        }

        /// <summary>POST: confirma o segredo pendente com um código válido — só aí o 2FA passa a ser exigido no login.</summary>
        public async Task<IActionResult> OnPostConfirmTotpAsync([FromForm] string TotpCode)
        {
            var admin = await GetCurrentAdminAsync();
            if (admin is null) return Forbid();

            if (string.IsNullOrEmpty(admin.TotpSecret))
            {
                ErrorMessage = "Gere um código QR antes de confirmar.";
                return RedirectToPage();
            }

            if (!TotpService.ValidateCode(admin.TotpSecret, TotpCode))
            {
                ErrorMessage = "Código inválido. Confira o horário do seu celular e tente de novo.";
                return RedirectToPage();
            }

            try
            {
                await _supabase.From<PlatformAdmin>()
                    .Where(x => x.Id == admin.Id)
                    .Set(x => x.TotpEnabled, true)
                    .Update();
                SuccessMessage = "2FA ativado com sucesso.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao confirmar TOTP pro superadmin {AdminId}", admin.Id);
                ErrorMessage = "Não foi possível ativar o 2FA agora.";
            }

            return RedirectToPage();
        }

        /// <summary>POST: desativa o 2FA (exige a senha atual — ação que reduz a segurança da conta).</summary>
        public async Task<IActionResult> OnPostDisableTotpAsync([FromForm] string CurrentPasswordForDisable)
        {
            var admin = await GetCurrentAdminAsync();
            if (admin is null) return Forbid();

            if (string.IsNullOrEmpty(admin.PasswordHash) || !BCrypt.Net.BCrypt.Verify(CurrentPasswordForDisable, admin.PasswordHash))
            {
                ErrorMessage = "Senha incorreta.";
                return RedirectToPage();
            }

            try
            {
                // .Set(x => x.Campo, null) quebra no postgrest-csharp 3.5.1
                // (achado da rodada de e-mail transacional, readme.txt
                // 12.2.b) — Update() do objeto completo funciona
                // normalmente pra limpar uma coluna nullable.
                admin.TotpEnabled = false;
                admin.TotpSecret = null;
                await _supabase.From<PlatformAdmin>().Update(admin);
                SuccessMessage = "2FA desativado.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao desativar TOTP pro superadmin {AdminId}", admin.Id);
                ErrorMessage = "Não foi possível desativar o 2FA agora.";
            }

            return RedirectToPage();
        }

        private async Task<PlatformAdmin?> GetCurrentAdminAsync()
        {
            var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(adminId)) return null;

            return await _supabase.From<PlatformAdmin>().Where(x => x.Id == adminId).Single();
        }

        private static string BuildQrDataUri(string otpAuthUri)
        {
            using var qrGenerator = new QRCodeGenerator();
            using var qrCodeData = qrGenerator.CreateQrCode(otpAuthUri, QRCodeGenerator.ECCLevel.Q);
            var pngQr = new PngByteQRCode(qrCodeData);
            var pngBytes = pngQr.GetGraphic(10);
            return $"data:image/png;base64,{Convert.ToBase64String(pngBytes)}";
        }
    }
}
