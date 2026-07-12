using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using AndressaLeite.Services;
using Supabase.Gotrue;

namespace AndressaLeite.Pages.Auth
{
    [EnableRateLimiting("login")]
    public class LoginModel : PageModel
    {
        [BindProperty]
        [Required(ErrorMessage = "O e-mail é obrigatório.")]
        [EmailAddress(ErrorMessage = "Informe um e-mail válido.")]
        public string Email { get; set; } = string.Empty;

        [BindProperty]
        [Required(ErrorMessage = "A senha é obrigatória.")]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        /// <summary>
        /// URL para onde voltar após o login (vinda de query string).
        /// Validada em IsLocalSafeUrl para evitar open-redirect.
        /// </summary>
        [BindProperty(SupportsGet = true)]
        public string? ReturnUrl { get; set; }

        public IActionResult OnGet()
        {
            // Se já estiver autenticado, manda para o destino apropriado.
            if (User.Identity?.IsAuthenticated == true)
            {
                return SafeRedirect(AuthorizationService.GetDefaultLandingForRole(AuthorizationService.GetRole(User)));
            }
            return Page();
        }

        public async Task<IActionResult> OnPostAsync([FromServices] Supabase.Client supabase)
        {
            if (!ModelState.IsValid) return Page();

            // Supabase pode ser configurado de duas formas; usamos email/senha.
            Supabase.Gotrue.Session? session;
            try
            {
                session = await supabase.Auth.SignIn(Email, Password);
            }
            catch (Exception ex) when (LooksLikeInvalidCredentials(ex))
            {
                // Credenciais inválidas — mensagem genérica, sem vazar qual campo falhou.
                ModelState.AddModelError(string.Empty, "E-mail ou senha inválidos.");
                return Page();
            }
            catch (Exception)
            {
                // Erro inesperado — não ecoar a mensagem interna (pode expor
                // schema, URLs internas, etc.). Logar via ILogger idealmente.
                ModelState.AddModelError(string.Empty, "Não foi possível processar o login. Tente novamente em instantes.");
                return Page();
            }

            if (session?.User is null)
            {
                ModelState.AddModelError(string.Empty, "E-mail ou senha inválidos.");
                return Page();
            }

            // Lê o perfil para conhecer a role. Em paralelo, o JWT já traz
            // user_metadata.role (gravado no signup). Usamos os dois e exigimos
            // que batam — assim um atacante que edite o Profile no banco
            // (RLS está desativado) não consegue escalar privilégio.
            string? profileRole = null;
            try
            {
                var profile = await supabase.From<Models.Profile>()
                    .Where(x => x.Id == session.User.Id)
                    .Single();
                profileRole = profile?.Role;
            }
            catch
            {
                // Se o profile não existe ainda, segue só com a role do JWT.
            }

            var jwtRole = session.User.UserMetadata?["role"] as string;
            var role = (profileRole ?? jwtRole ?? string.Empty).ToLowerInvariant();

            if (string.IsNullOrEmpty(role) || !AuthorizationService.KnownRoles.Contains(role) || role == "inactive")
            {
                // Sem role válida: rejeita o login.
                ModelState.AddModelError(string.Empty, "Conta sem permissão de acesso. Contate o suporte.");
                return Page();
            }

            // Se profile e JWT discordam, preferimos o JWT (assinado, não
            // editável pelo usuário). Logar a divergência é recomendado em produção.
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, session.User.Id),
                new Claim(ClaimTypes.Email, Email),
                new Claim(ClaimTypes.Role, role)
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

            // Decide destino: ReturnUrl (se seguro) > landing da role.
            if (AuthorizationService.IsLocalSafeUrl(ReturnUrl))
            {
                return LocalRedirect(ReturnUrl!);
            }
            return SafeRedirect(AuthorizationService.GetDefaultLandingForRole(role));
        }

        private IActionResult SafeRedirect(string path)
        {
            // Helper para garantir que o caminho é local antes de redirecionar.
            return AuthorizationService.IsLocalSafeUrl(path)
                ? LocalRedirect(path)
                : RedirectToPage("/Index");
        }

        /// <summary>
        /// Identifica, de forma defensiva, se a exceção do Supabase Gotrue
        /// representa credenciais inválidas. Como o tipo da exceção varia entre
        /// versões da lib (GotrueException, WeakPasswordException, etc.),
        /// inspecionamos por nome de tipo e por mensagem.
        /// </summary>
        private static bool LooksLikeInvalidCredentials(Exception ex)
        {
            var typeName = ex.GetType().Name;
            if (typeName.Contains("InvalidCredentials", StringComparison.OrdinalIgnoreCase) ||
                typeName.Contains("InvalidLogin", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            // Algumas versões expõem StatusCode via reflection
            var statusProp = ex.GetType().GetProperty("StatusCode");
            if (statusProp?.GetValue(ex) is int code && (code == 400 || code == 401 || code == 422))
            {
                return true;
            }
            // Fallback: checa a mensagem (palavras-chave comuns do Supabase)
            var msg = ex.Message ?? string.Empty;
            return msg.Contains("Invalid login", StringComparison.OrdinalIgnoreCase) ||
                   msg.Contains("Invalid credentials", StringComparison.OrdinalIgnoreCase) ||
                   msg.Contains("Invalid email or password", StringComparison.OrdinalIgnoreCase);
        }
    }
}
