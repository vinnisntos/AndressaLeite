using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using AndressaLeite.Models;
using AndressaLeite.Services;
using BCrypt.Net;

namespace AndressaLeite.Pages.SuperAdmin
{
    /// <summary>
    /// Login do painel cross-tenant da plataforma MarcAi. Só existe no
    /// domínio raiz (contexto plataforma) — não é recurso de nenhum
    /// tenant, e busca em platform_admins, não em profiles.
    ///
    /// Login em duas etapas quando 2FA está ativo: 1) e-mail+senha, 2)
    /// código TOTP. Entre as duas, o id do admin viaja num token protegido
    /// por IDataProtector (não uma claim/cookie de autenticação real — o
    /// login só se completa depois do código confirmado), com validade
    /// curta pra não virar um jeito de ficar "meio logado" indefinidamente.
    /// </summary>
    [EnableRateLimiting("login")]
    public class LoginModel : PageModel
    {
        private static readonly TimeSpan PendingTokenTtl = TimeSpan.FromMinutes(5);

        private readonly Supabase.Client _supabase;
        private readonly ILogger<LoginModel> _logger;
        private readonly CurrentTenant _currentTenant;
        private readonly IDataProtector _protector;

        public LoginModel(Supabase.Client supabase, ILogger<LoginModel> logger, CurrentTenant currentTenant, IDataProtectionProvider dataProtectionProvider)
        {
            _supabase = supabase;
            _logger = logger;
            _currentTenant = currentTenant;
            _protector = dataProtectionProvider.CreateProtector("SuperAdmin.LoginTotpPending");
        }

        [BindProperty]
        [Required(ErrorMessage = "O e-mail é obrigatório.")]
        [EmailAddress(ErrorMessage = "Informe um e-mail válido.")]
        public string Email { get; set; } = string.Empty;

        [BindProperty]
        [Required(ErrorMessage = "A senha é obrigatória.")]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [BindProperty]
        public string? PendingToken { get; set; }

        [BindProperty]
        public string? TotpCode { get; set; }

        /// <summary>True quando a senha já foi validada e falta só o código do app autenticador.</summary>
        public bool ShowTotpStep { get; set; }

        public string? ErrorMessage { get; set; }

        public IActionResult OnGet()
        {
            // Não é recurso de tenant nenhum — só existe no domínio raiz.
            if (_currentTenant.IsResolved)
            {
                return RedirectToPage("/Index");
            }

            if (User.Identity?.IsAuthenticated == true && AuthorizationService.GetRole(User) == "superadmin")
            {
                return RedirectToPage("/SuperAdmin/Dashboard");
            }
            return Page();
        }

        /// <summary>POST etapa 1: valida e-mail/senha.</summary>
        public async Task<IActionResult> OnPostAsync()
        {
            if (_currentTenant.IsResolved)
            {
                return RedirectToPage("/Index");
            }

            if (!ModelState.IsValid) return Page();

            PlatformAdmin? admin = null;
            try
            {
                var targetEmail = Email.Trim().ToLowerInvariant();
                admin = await _supabase.From<PlatformAdmin>()
                    .Where(x => x.Email == targetEmail)
                    .Single();
            }
            catch (Exception ex)
            {
                string msg = ex.Message.ToLower();
                if (!msg.Contains("sequence contains no elements") && !msg.Contains("404") && !msg.Contains("null"))
                {
                    _logger.LogError(ex, "Falha de infraestrutura ao buscar superadmin para {Email}", Email);
                    ErrorMessage = "Não foi possível concluir o login no momento. Tente novamente em instantes.";
                    return Page();
                }
            }

            if (admin is null || string.IsNullOrEmpty(admin.PasswordHash) ||
                !BCrypt.Net.BCrypt.Verify(Password, admin.PasswordHash))
            {
                ModelState.AddModelError(string.Empty, "E-mail ou senha inválidos.");
                return Page();
            }

            if (admin.TotpEnabled && !string.IsNullOrEmpty(admin.TotpSecret))
            {
                PendingToken = _protector.Protect($"{admin.Id}|{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
                ShowTotpStep = true;
                Password = string.Empty;
                return Page();
            }

            await SignInAdminAsync(admin);
            return RedirectToPage("/SuperAdmin/Dashboard");
        }

        /// <summary>POST etapa 2: valida o código de 6 dígitos do app autenticador.</summary>
        public async Task<IActionResult> OnPostVerifyTotpAsync()
        {
            if (_currentTenant.IsResolved)
            {
                return RedirectToPage("/Index");
            }

            if (string.IsNullOrWhiteSpace(PendingToken))
            {
                ErrorMessage = "Sessão de login expirada. Comece de novo.";
                return Page();
            }

            string adminId;
            try
            {
                var unprotected = _protector.Unprotect(PendingToken);
                var parts = unprotected.Split('|');
                var issuedAtUnix = long.Parse(parts[1]);
                if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - issuedAtUnix > PendingTokenTtl.TotalSeconds)
                {
                    ErrorMessage = "Sessão de login expirada. Comece de novo.";
                    return Page();
                }
                adminId = parts[0];
            }
            catch
            {
                // Token adulterado ou malformado — não revela detalhes.
                ErrorMessage = "Sessão de login expirada. Comece de novo.";
                return Page();
            }

            PlatformAdmin? admin;
            try
            {
                admin = await _supabase.From<PlatformAdmin>().Where(x => x.Id == adminId).Single();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao buscar superadmin {AdminId} na verificação de 2FA", adminId);
                ErrorMessage = "Não foi possível concluir o login no momento. Tente novamente em instantes.";
                return Page();
            }

            if (admin is null || !admin.TotpEnabled || string.IsNullOrEmpty(admin.TotpSecret) ||
                !TotpService.ValidateCode(admin.TotpSecret, TotpCode))
            {
                ErrorMessage = "Código inválido.";
                ShowTotpStep = true;
                return Page();
            }

            await SignInAdminAsync(admin);
            return RedirectToPage("/SuperAdmin/Dashboard");
        }

        private async Task SignInAdminAsync(PlatformAdmin admin)
        {
            // Sem claim de tenant_id de propósito — superadmin não
            // pertence a nenhum salão (ver Program.cs, middleware de
            // checagem claim-vs-tenant só age quando a claim existe).
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, admin.Id),
                new Claim(ClaimTypes.Email, admin.Email),
                new Claim(ClaimTypes.Role, "superadmin")
            };

            var claimsIdentity = new ClaimsIdentity(
                claims,
                CookieAuthenticationDefaults.AuthenticationScheme,
                ClaimTypes.Name,
                ClaimTypes.Role
            );

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(claimsIdentity),
                new AuthenticationProperties
                {
                    IsPersistent = true,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7)
                }
            );
        }
    }
}
