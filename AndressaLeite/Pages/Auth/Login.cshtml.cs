using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using AndressaLeite.Services;
using BCrypt.Net;

namespace AndressaLeite.Pages.Auth
{
    [EnableRateLimiting("login")]
    public class LoginModel : PageModel
    {
        private readonly ILogger<LoginModel> _logger;
        private readonly CurrentTenant _currentTenant;

        public LoginModel(ILogger<LoginModel> logger, CurrentTenant currentTenant)
        {
            _logger = logger;
            _currentTenant = currentTenant;
        }

        [BindProperty]
        [Required(ErrorMessage = "O e-mail é obrigatório.")]
        [EmailAddress(ErrorMessage = "Informe um e-mail válido.")]
        public string Email { get; set; } = string.Empty;

        [BindProperty]
        [Required(ErrorMessage = "A senha é obrigatória.")]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [BindProperty(SupportsGet = true)]
        public string? ReturnUrl { get; set; }

        public string? ErrorMessage { get; set; }

        public IActionResult OnGet()
        {
            // Login só faz sentido dentro do subdomínio de um salão. Sem
            // tenant resolvido (domínio raiz) ou com o salão suspenso, a
            // Index é quem decide a mensagem certa a mostrar.
            if (!_currentTenant.IsResolved || !_currentTenant.IsActive)
            {
                return RedirectToPage("/Index");
            }

            if (User.Identity?.IsAuthenticated == true)
            {
                return SafeRedirect(AuthorizationService.GetDefaultLandingForRole(AuthorizationService.GetRole(User)));
            }
            return Page();
        }

        public async Task<IActionResult> OnPostAsync([FromServices] Supabase.Client supabase)
        {
            if (!_currentTenant.IsResolved || !_currentTenant.IsActive)
            {
                return RedirectToPage("/Index");
            }

            if (!ModelState.IsValid) return Page();

            AndressaLeite.Models.Profile? profile = null;

            try
            {
                // 🔴 RESOLVIDO: Trata a string fora da expressão LINQ do Supabase
                string targetEmail = Email.Trim().ToLowerInvariant();

                // 1. Busca o usuário diretamente na sua tabela pública pelo e-mail
                // tratado, restrito ao salão do subdomínio atual (mesmo e-mail
                // pode existir em salões diferentes como contas separadas).
                // Filtro de tenant SEMPRE como .Where() encadeado separado —
                // não fundir com o filtro de e-mail num só lambda (driver
                // postgrest-csharp tem bug documentado com certas combinações,
                // ver comentários em DashProfissional.cshtml.cs).
                profile = await supabase.From<Models.Profile>()
                    .Where(x => x.Email == targetEmail)
                    .Where(x => x.TenantId == _currentTenant.Id)
                    .Single();
            }
            catch (Exception ex)
            {
                string msg = ex.Message.ToLower();
                if (!msg.Contains("sequence contains no elements") && !msg.Contains("404") && !msg.Contains("null"))
                {
                    // Loga o detalhe internamente; a UI recebe só uma mensagem genérica
                    // para não expor schema/infra do banco a um usuário não autenticado.
                    _logger.LogError(ex, "Falha de infraestrutura ao buscar perfil no login para {Email}", Email);
                    ErrorMessage = "Não foi possível concluir o login no momento. Tente novamente em instantes.";
                    return Page();
                }
            }

            // 2. Valida se o usuário existe e se o hash da senha bate com o BCrypt
            if (profile is null || string.IsNullOrEmpty(profile.PasswordHash) ||
                !BCrypt.Net.BCrypt.Verify(Password, profile.PasswordHash))
            {
                ModelState.AddModelError(string.Empty, "E-mail ou senha inválidos.");
                return Page();
            }

            // 3. Valida se a Role vinda da tabela é permitida
            var role = (profile.Role ?? string.Empty).ToLowerInvariant();

            if (string.IsNullOrEmpty(role) || !AuthorizationService.KnownRoles.Contains(role) || role == "inactive")
            {
                ModelState.AddModelError(string.Empty, "Conta sem permissão de acesso. Contate o suporte.");
                return Page();
            }

            // 4. Cria os Claims do Cookie baseados nos dados da tabela pública
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, profile.Id),
                new Claim(ClaimTypes.Email, profile.Email ?? Email),
                new Claim(ClaimTypes.Role, role),
                new Claim(AuthorizationService.TenantClaimType, profile.TenantId),
                new Claim(AuthorizationService.EmailVerifiedClaimType, profile.EmailVerified.ToString())
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

            if (AuthorizationService.IsLocalSafeUrl(ReturnUrl))
            {
                return LocalRedirect(ReturnUrl!);
            }

            return SafeRedirect(AuthorizationService.GetDefaultLandingForRole(role));
        }

        private IActionResult SafeRedirect(string path)
        {
            return AuthorizationService.IsLocalSafeUrl(path)
                ? LocalRedirect(path)
                : RedirectToPage("/Index");
        }
    }
}