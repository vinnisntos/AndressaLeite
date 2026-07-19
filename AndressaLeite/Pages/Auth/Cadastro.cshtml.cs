using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using Supabase.Gotrue;
using BCrypt.Net;
using AndressaLeite.Services;

namespace AndressaLeite.Pages.Auth
{
    [EnableRateLimiting("signup")]
    public class CadastroModel : PageModel
    {
        private readonly ILogger<CadastroModel> _logger;
        private readonly CurrentTenant _currentTenant;
        private readonly IEmailService _emailService;
        private readonly IConfiguration _configuration;

        public CadastroModel(ILogger<CadastroModel> logger, CurrentTenant currentTenant, IEmailService emailService, IConfiguration configuration)
        {
            _logger = logger;
            _currentTenant = currentTenant;
            _emailService = emailService;
            _configuration = configuration;
        }

        [BindProperty]
        [Required(ErrorMessage = "Nome é obrigatório.")]
        [StringLength(120, MinimumLength = 2, ErrorMessage = "O nome deve ter entre 2 e 120 caracteres.")]
        [RegularExpression(@"^[\p{L}\p{M}'\.\- ]+$", ErrorMessage = "O nome contém caracteres inválidos.")]
        public string FullName { get; set; } = string.Empty;

        [BindProperty]
        [Required(ErrorMessage = "E-mail é obrigatório.")]
        [EmailAddress(ErrorMessage = "Informe um e-mail válido.")]
        public string Email { get; set; } = string.Empty;

        [BindProperty]
        [Required(ErrorMessage = "Celular é obrigatório.")]
        [RegularExpression(@"^\+?[1-9]\d{10,14}$", ErrorMessage = "Telefone inválido. Use DDI + DDD + número (somente dígitos).")]
        public string Phone { get; set; } = string.Empty;

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

        [TempData] public string? InfoMessage { get; set; }
        public string? ErrorMessage { get; set; }

        public IActionResult OnGet()
        {
            // Cadastro de cliente só faz sentido dentro do subdomínio de um
            // salão. Sem tenant resolvido ou com o salão suspenso, a Index
            // é quem decide a mensagem certa a mostrar.
            if (!_currentTenant.IsResolved || !_currentTenant.IsActive)
            {
                return RedirectToPage("/Index");
            }

            if (User.Identity?.IsAuthenticated == true)
            {
                return RedirectToPage("/Index");
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

            // Validação de complexidade (Alinhado com NIST SP 800-63B)
            if (!Regex.IsMatch(Password, @"[A-Za-z]") || !Regex.IsMatch(Password, @"\d"))
            {
                ModelState.AddModelError(nameof(Password), "A senha deve conter pelo menos uma letra e um número.");
                return Page();
            }

            var cleanPhone = Regex.Replace(Phone, @"[^\d]", "");

            // 1. Criptografia no C# via BCrypt
            string hashedPassword = BCrypt.Net.BCrypt.HashPassword(Password);

            // 2. Cria um novo ID único para o usuário
            string newUserId = Guid.NewGuid().ToString();

            var profile = new AndressaLeite.Models.Profile
            {
                Id = newUserId,
                FullName = FullName.Trim(),
                Phone = cleanPhone,
                Role = "client",
                Email = Email.Trim().ToLowerInvariant(),
                PasswordHash = hashedPassword,
                TenantId = _currentTenant.Id!
            };

            try
            {
                // 3. Executa o insert na tabela pública
                await supabase.From<AndressaLeite.Models.Profile>().Insert(profile);

                // Verificação de e-mail (readme.txt 4.2) — nunca pode
                // impedir o cadastro de completar; Resend fora do ar só
                // loga e segue, o cadastro já está feito de qualquer forma.
                await TrySendVerificationEmailAsync(supabase, newUserId, profile.Email);

                // 4. Cria a identidade de Cookies local do .NET
                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.NameIdentifier, newUserId),
                    new Claim(ClaimTypes.Email, Email.Trim().ToLowerInvariant()),
                    new Claim(ClaimTypes.Role, "client"),
                    new Claim(AuthorizationService.TenantClaimType, _currentTenant.Id!),
                    new Claim(AuthorizationService.EmailVerifiedClaimType, "False")
                };

                var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

                await HttpContext.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    new ClaimsPrincipal(claimsIdentity),
                    new AuthenticationProperties
                    {
                        IsPersistent = true,
                        ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7)
                    }
                );

                return RedirectToPage("/Cliente/DashCliente");
            }
            catch (Exception ex)
            {
                // Loga o detalhe internamente; a UI recebe só uma mensagem genérica
                // para não expor schema/infra do banco a um usuário não autenticado.
                var isDuplicate = (ex.InnerException?.Message ?? ex.Message).Contains("23505");
                _logger.LogError(ex, "Falha ao inserir profile no cadastro para {Email}", Email);
                ErrorMessage = isDuplicate
                    ? "Este e-mail já está cadastrado."
                    : "Não foi possível concluir o cadastro no momento. Tente novamente em instantes.";
                return Page();
            }
        }

        /// <summary>
        /// Gera o token de verificação e envia o e-mail — sempre em
        /// try/catch só-loga (ver comentário no chamador): um cadastro
        /// bem-sucedido nunca pode virar erro por causa do Resend.
        /// </summary>
        private async Task TrySendVerificationEmailAsync(Supabase.Client supabase, string profileId, string email)
        {
            try
            {
                var (rawToken, tokenHash) = EmailTokenService.GenerateToken();
                await supabase.From<AndressaLeite.Models.Profile>()
                    .Where(x => x.Id == profileId)
                    .Set(x => x.ActionTokenHash, tokenHash)
                    .Set(x => x.ActionTokenType, "email_verification")
                    .Set(x => x.ActionTokenExpiresAt, DateTime.UtcNow.AddHours(24))
                    .Update();

                var rootDomain = _configuration["Tenancy:RootDomain"] ?? "localhost";
                var scheme = Request.IsHttps ? "https" : "http";
                var port = Request.Host.Port.HasValue ? $":{Request.Host.Port}" : "";
                var verifyUrl = $"{scheme}://{_currentTenant.Slug}.{rootDomain}{port}/Auth/VerificarEmail?token={rawToken}";

                var html = $"<p>Confirme seu e-mail no MarcAi clicando no link abaixo (válido por 24 horas):</p>" +
                    $"<p><a href=\"{System.Net.WebUtility.HtmlEncode(verifyUrl)}\">Confirmar meu e-mail</a></p>";
                await _emailService.SendEmailAsync(email, "Confirme seu e-mail — MarcAi", html);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao enviar e-mail de verificação no cadastro para {Email}", email);
            }
        }
    }
}