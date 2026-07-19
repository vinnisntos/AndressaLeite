using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using AndressaLeite.Models;
using AndressaLeite.Services;
using Postgrest.Exceptions;
using BCrypt.Net;

namespace AndressaLeite.Pages.Auth
{
    /// <summary>
    /// Aceite de convite de equipe (readme.txt 5.6) — substitui o antigo
    /// fluxo onde o admin criava a conta da profissional com senha direto
    /// (DashAdmin.cshtml.cs, agora OnPostInviteEmployeeAsync). O profile só
    /// nasce aqui, quando a profissional de fato aceita e define a senha —
    /// ver migration 0008 e Models/TeamInvite.cs pro porquê da tabela
    /// separada em vez de um profile "pendente".
    /// </summary>
    public class AceitarConviteModel : PageModel
    {
        private readonly Supabase.Client _supabase;
        private readonly ILogger<AceitarConviteModel> _logger;
        private readonly CurrentTenant _currentTenant;

        public AceitarConviteModel(Supabase.Client supabase, ILogger<AceitarConviteModel> logger, CurrentTenant currentTenant)
        {
            _supabase = supabase;
            _logger = logger;
            _currentTenant = currentTenant;
        }

        [BindProperty(SupportsGet = true)]
        public string? Token { get; set; }

        [BindProperty]
        [Required(ErrorMessage = "Senha é obrigatória.")]
        [StringLength(100, MinimumLength = 8, ErrorMessage = "A senha deve ter no mínimo 8 caracteres.")]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [BindProperty]
        [Required(ErrorMessage = "A confirmação da senha é obrigatória.")]
        [Compare("Password", ErrorMessage = "As senhas informadas não coincidem.")]
        [DataType(DataType.Password)]
        public string ConfirmPassword { get; set; } = string.Empty;

        public bool TokenIsValid { get; set; }
        public string? ErrorMessage { get; set; }
        public string InviteName { get; set; } = string.Empty;
        public string InviteEmail { get; set; } = string.Empty;

        public async Task<IActionResult> OnGetAsync()
        {
            if (!_currentTenant.IsResolved || !_currentTenant.IsActive)
            {
                return RedirectToPage("/Index");
            }

            var invite = await FindPendingInviteAsync(Token);
            TokenIsValid = invite is not null;
            if (invite is null)
            {
                ErrorMessage = "Este convite é inválido, já foi usado ou expirou. Peça pra sua administradora enviar um novo.";
            }
            else
            {
                InviteName = invite.FullName;
                InviteEmail = invite.Email;
            }
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!_currentTenant.IsResolved || !_currentTenant.IsActive)
            {
                return RedirectToPage("/Index");
            }

            // Revalida tudo de novo — não confia no TokenIsValid do GET (o
            // convite pode ter sido cancelado/usado/expirado entre as duas
            // requisições).
            var invite = await FindPendingInviteAsync(Token);
            if (invite is null)
            {
                TokenIsValid = false;
                ErrorMessage = "Este convite é inválido, já foi usado ou expirou. Peça pra sua administradora enviar um novo.";
                return Page();
            }
            TokenIsValid = true;
            InviteName = invite.FullName;
            InviteEmail = invite.Email;

            if (!ModelState.IsValid) return Page();

            if (!Regex.IsMatch(Password, @"[A-Za-z]") || !Regex.IsMatch(Password, @"\d"))
            {
                ModelState.AddModelError(nameof(Password), "A senha deve conter pelo menos uma letra e um número.");
                return Page();
            }

            var newUserId = Guid.NewGuid().ToString();
            var profile = new Profile
            {
                Id = newUserId,
                FullName = invite.FullName,
                Phone = invite.Phone,
                Role = "employee",
                Email = invite.Email,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
                TenantId = invite.TenantId,
                // Verificado por construção: só chegou até aqui quem clicou
                // no link mandado pro e-mail do convite.
                EmailVerified = true
            };

            try
            {
                await _supabase.From<Profile>().Insert(profile);

                // Marca o convite como usado (uso único) — mesmo raciocínio
                // do token de reset/verificação, aqui via coluna dedicada
                // em vez de limpar o hash, pra manter o histórico do convite.
                await _supabase.From<TeamInvite>()
                    .Where(x => x.Id == invite.Id)
                    .Set(x => x.UsedAt, DateTime.UtcNow)
                    .Update();
            }
            catch (PostgrestException pgex)
            {
                _logger.LogError(pgex, "Falha ao aceitar convite {InviteId}", invite.Id);
                ErrorMessage = pgex.Message.Contains("23505") || pgex.Message.Contains("duplicate")
                    ? "Já existe uma conta com este e-mail."
                    : "Não foi possível concluir seu cadastro agora. Tente novamente em instantes.";
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha inesperada ao aceitar convite {InviteId}", invite.Id);
                ErrorMessage = "Não foi possível concluir seu cadastro agora. Tente novamente em instantes.";
                return Page();
            }

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, newUserId),
                new Claim(ClaimTypes.Email, invite.Email),
                new Claim(ClaimTypes.Role, "employee"),
                new Claim(AuthorizationService.TenantClaimType, invite.TenantId),
                new Claim(AuthorizationService.EmailVerifiedClaimType, "True")
            };
            var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(claimsIdentity),
                new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7) }
            );

            return LocalRedirect(AuthorizationService.GetDefaultLandingForRole("employee"));
        }

        /// <summary>
        /// Busca um convite pendente (não usado, não cancelado, não
        /// expirado) pelo hash do token — usado tanto no GET quanto no
        /// POST, nunca confia em validação de uma requisição anterior.
        /// </summary>
        private async Task<TeamInvite?> FindPendingInviteAsync(string? rawToken)
        {
            if (string.IsNullOrWhiteSpace(rawToken) || !_currentTenant.IsResolved) return null;

            var hash = EmailTokenService.Hash(rawToken);
            var invite = await _supabase.From<TeamInvite>()
                .Where(x => x.TokenHash == hash)
                .Where(x => x.TenantId == _currentTenant.Id)
                .Single();

            if (invite is null) return null;
            if (invite.UsedAt is not null || invite.CancelledAt is not null) return null;
            // TeamInvite.ExpiresAt já normaliza Kind=Utc no próprio setter
            // do modelo — não precisa corrigir aqui.
            if (EmailTokenService.IsExpired(invite.ExpiresAt)) return null;

            return invite;
        }
    }
}
