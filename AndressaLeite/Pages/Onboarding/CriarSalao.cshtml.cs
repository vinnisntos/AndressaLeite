using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using AndressaLeite.Models;
using AndressaLeite.Services;
using Postgrest.Exceptions;
using BCrypt.Net;

namespace AndressaLeite.Pages.Onboarding
{
    /// <summary>
    /// Onboarding self-service: dono de um novo salão cria seu tenant e a
    /// própria conta admin. Só faz sentido em contexto de plataforma (sem
    /// subdomínio de tenant resolvido) — ver CurrentTenant/TenantResolutionMiddleware.
    /// </summary>
    [EnableRateLimiting("onboarding")]
    public class CriarSalaoModel : PageModel
    {
        // Slugs que não podem virar nome de salão porque colidem com rotas/
        // infraestrutura da própria plataforma (ex.: www.suaapp.com precisa
        // continuar sendo a home da plataforma, não um salão chamado "www").
        private static readonly HashSet<string> ReservedSlugs = new(StringComparer.OrdinalIgnoreCase)
        {
            "www", "api", "admin", "app", "mail", "static", "assets", "onboarding",
            "auth", "cliente", "profissional", "cdn", "ftp", "smtp", "ns1", "ns2", "suporte", "support"
        };

        private static readonly Regex SlugFormat = new(@"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.Compiled);

        private readonly Supabase.Client _supabase;
        private readonly ILogger<CriarSalaoModel> _logger;
        private readonly CurrentTenant _currentTenant;
        private readonly IConfiguration _configuration;
        private readonly IEmailService _emailService;

        public CriarSalaoModel(Supabase.Client supabase, ILogger<CriarSalaoModel> logger, CurrentTenant currentTenant, IConfiguration configuration, IEmailService emailService)
        {
            _supabase = supabase;
            _logger = logger;
            _currentTenant = currentTenant;
            _configuration = configuration;
            _emailService = emailService;
        }

        [BindProperty]
        [Required(ErrorMessage = "Escolha um endereço para o seu salão.")]
        [StringLength(63, MinimumLength = 2, ErrorMessage = "O endereço deve ter entre 2 e 63 caracteres.")]
        public string SalonSlug { get; set; } = string.Empty;

        [BindProperty]
        [Required(ErrorMessage = "O nome do salão é obrigatório.")]
        [StringLength(120, MinimumLength = 2, ErrorMessage = "O nome deve ter entre 2 e 120 caracteres.")]
        public string SalonName { get; set; } = string.Empty;

        [BindProperty]
        [Required(ErrorMessage = "Seu nome é obrigatório.")]
        [StringLength(120, MinimumLength = 2, ErrorMessage = "O nome deve ter entre 2 e 120 caracteres.")]
        public string OwnerName { get; set; } = string.Empty;

        [BindProperty]
        [Required(ErrorMessage = "E-mail é obrigatório.")]
        [EmailAddress(ErrorMessage = "Informe um e-mail válido.")]
        public string OwnerEmail { get; set; } = string.Empty;

        [BindProperty]
        [Required(ErrorMessage = "Celular é obrigatório.")]
        [RegularExpression(@"^\+?[1-9]\d{10,14}$", ErrorMessage = "Telefone inválido. Use DDI + DDD + número (somente dígitos).")]
        public string OwnerPhone { get; set; } = string.Empty;

        [BindProperty]
        [Required(ErrorMessage = "Senha é obrigatória.")]
        [StringLength(100, MinimumLength = 8, ErrorMessage = "A senha deve ter no mínimo 8 caracteres.")]
        [DataType(DataType.Password)]
        public string OwnerPassword { get; set; } = string.Empty;

        [BindProperty]
        [Required(ErrorMessage = "A confirmação da senha é obrigatória.")]
        [Compare("OwnerPassword", ErrorMessage = "As senhas informadas não coincidem.")]
        [DataType(DataType.Password)]
        public string ConfirmPassword { get; set; } = string.Empty;

        public string? ErrorMessage { get; set; }

        public IActionResult OnGet()
        {
            // Só faz sentido no domínio raiz da plataforma — dentro do
            // subdomínio de um salão já existente, não há o que criar aqui.
            if (_currentTenant.IsResolved)
            {
                return RedirectToPage("/Index");
            }
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (_currentTenant.IsResolved)
            {
                return RedirectToPage("/Index");
            }

            if (!ModelState.IsValid) return Page();

            if (!Regex.IsMatch(OwnerPassword, @"[A-Za-z]") || !Regex.IsMatch(OwnerPassword, @"\d"))
            {
                ModelState.AddModelError(nameof(OwnerPassword), "A senha deve conter pelo menos uma letra e um número.");
                return Page();
            }

            var cleanSlug = SalonSlug.Trim().ToLowerInvariant();
            if (!SlugFormat.IsMatch(cleanSlug))
            {
                ModelState.AddModelError(nameof(SalonSlug),
                    "Use só letras minúsculas, números e hífen, sem começar ou terminar com hífen.");
                return Page();
            }
            if (ReservedSlugs.Contains(cleanSlug))
            {
                ModelState.AddModelError(nameof(SalonSlug), "Este endereço é reservado. Escolha outro.");
                return Page();
            }

            var cleanPhone = Regex.Replace(OwnerPhone, @"[^\d]", "");
            var cleanEmail = OwnerEmail.Trim().ToLowerInvariant();

            Tenant? createdTenant = null;
            try
            {
                var tenant = new Tenant
                {
                    Id = Guid.NewGuid().ToString(),
                    Slug = cleanSlug,
                    Name = SalonName.Trim(),
                    IsActive = true
                };
                await _supabase.From<Tenant>().Insert(tenant);
                createdTenant = tenant;

                var profile = new Profile
                {
                    Id = Guid.NewGuid().ToString(),
                    FullName = OwnerName.Trim(),
                    Phone = cleanPhone,
                    Role = "admin",
                    Email = cleanEmail,
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(OwnerPassword),
                    TenantId = tenant.Id
                };
                await _supabase.From<Profile>().Insert(profile);

                // Billing (readme.txt 4.9/9.2): trial de 14 dias a partir
                // de agora — nunca pode impedir a criação do salão de
                // completar, mesmo raciocínio do e-mail de verificação
                // logo abaixo.
                try
                {
                    var subscription = new TenantSubscription
                    {
                        Id = Guid.NewGuid().ToString(),
                        TenantId = tenant.Id,
                        Status = "trial",
                        TrialEndsAt = DateTime.UtcNow.AddDays(14)
                    };
                    await _supabase.From<TenantSubscription>().Insert(subscription);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Falha ao criar assinatura trial pro tenant {TenantId}", tenant.Id);
                }

                // Verificação de e-mail (readme.txt 4.2) — nunca pode
                // impedir a criação do salão de completar; Resend fora do
                // ar só loga e segue, o salão já está criado de qualquer
                // forma.
                await TrySendVerificationEmailAsync(profile.Id, profile.Email, cleanSlug);

                var rootDomain = _configuration["Tenancy:RootDomain"] ?? "localhost";
                var scheme = Request.IsHttps ? "https" : "http";
                var port = Request.Host.Port.HasValue ? $":{Request.Host.Port}" : "";
                var loginUrl = $"{scheme}://{cleanSlug}.{rootDomain}{port}/Auth/Login";

                // Login automático cross-subdomínio não é possível (o cookie
                // é host-scoped por padrão — ver Program.cs); o dono loga de
                // novo já no subdomínio do salão recém-criado.
                return Redirect(loginUrl);
            }
            catch (PostgrestException pgex)
            {
                // Evita deixar um tenant "órfão" (sem admin) ocupando um slug
                // bom caso o insert do profile falhe.
                await TryCleanupOrphanTenantAsync(createdTenant);

                _logger.LogError(pgex, "Falha ao criar tenant/admin no onboarding para slug {Slug}", cleanSlug);
                ErrorMessage = pgex.Message.Contains("23505") || pgex.Message.Contains("duplicate")
                    ? "Este endereço de salão ou e-mail já está em uso."
                    : "Não foi possível criar seu salão agora. Tente novamente em instantes.";
                return Page();
            }
            catch (Exception ex)
            {
                await TryCleanupOrphanTenantAsync(createdTenant);

                _logger.LogError(ex, "Falha inesperada no onboarding para slug {Slug}", cleanSlug);
                ErrorMessage = "Não foi possível criar seu salão agora. Tente novamente em instantes.";
                return Page();
            }
        }

        /// <summary>
        /// Gera o token de verificação e envia o e-mail — sempre em
        /// try/catch só-loga (ver comentário no chamador): um salão criado
        /// com sucesso nunca pode virar erro por causa do Resend.
        /// </summary>
        private async Task TrySendVerificationEmailAsync(string profileId, string email, string slug)
        {
            try
            {
                var (rawToken, tokenHash) = EmailTokenService.GenerateToken();
                await _supabase.From<Profile>()
                    .Where(x => x.Id == profileId)
                    .Set(x => x.ActionTokenHash, tokenHash)
                    .Set(x => x.ActionTokenType, "email_verification")
                    .Set(x => x.ActionTokenExpiresAt, DateTime.UtcNow.AddHours(24))
                    .Update();

                var rootDomain = _configuration["Tenancy:RootDomain"] ?? "localhost";
                var scheme = Request.IsHttps ? "https" : "http";
                var port = Request.Host.Port.HasValue ? $":{Request.Host.Port}" : "";
                var verifyUrl = $"{scheme}://{slug}.{rootDomain}{port}/Auth/VerificarEmail?token={rawToken}";

                var html = $"<p>Confirme seu e-mail no MarcAi clicando no link abaixo (válido por 24 horas):</p>" +
                    $"<p><a href=\"{System.Net.WebUtility.HtmlEncode(verifyUrl)}\">Confirmar meu e-mail</a></p>";
                await _emailService.SendEmailAsync(email, "Confirme seu e-mail — MarcAi", html);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao enviar e-mail de verificação no onboarding para {Email}", email);
            }
        }

        private async Task TryCleanupOrphanTenantAsync(Tenant? createdTenant)
        {
            if (createdTenant is null) return;
            try
            {
                await _supabase.From<Tenant>().Where(x => x.Id == createdTenant.Id).Delete();
            }
            catch (Exception cleanupEx)
            {
                _logger.LogError(cleanupEx, "Falha ao limpar tenant órfão {TenantId}", createdTenant.Id);
            }
        }
    }
}
