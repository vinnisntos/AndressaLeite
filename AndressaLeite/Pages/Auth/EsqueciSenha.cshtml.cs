using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using System.ComponentModel.DataAnnotations;
using System.Net;
using AndressaLeite.Models;
using AndressaLeite.Services;

namespace AndressaLeite.Pages.Auth
{
    /// <summary>
    /// Pedido de redefinição de senha (readme.txt 4.1). Sempre mostra a
    /// mesma mensagem genérica de sucesso, exista ou não o e-mail — defesa
    /// contra enumeração de contas. Ver Pages/Auth/RedefinirSenha.cshtml.cs
    /// pro passo seguinte (consumir o token).
    /// </summary>
    [EnableRateLimiting("password-reset")]
    public class EsqueciSenhaModel : PageModel
    {
        private readonly Supabase.Client _supabase;
        private readonly IEmailService _emailService;
        private readonly ILogger<EsqueciSenhaModel> _logger;
        private readonly CurrentTenant _currentTenant;
        private readonly IConfiguration _configuration;

        public EsqueciSenhaModel(Supabase.Client supabase, IEmailService emailService, ILogger<EsqueciSenhaModel> logger, CurrentTenant currentTenant, IConfiguration configuration)
        {
            _supabase = supabase;
            _emailService = emailService;
            _logger = logger;
            _currentTenant = currentTenant;
            _configuration = configuration;
        }

        [BindProperty]
        [Required(ErrorMessage = "E-mail é obrigatório.")]
        [EmailAddress(ErrorMessage = "Informe um e-mail válido.")]
        public string Email { get; set; } = string.Empty;

        public string? SuccessMessage { get; set; }

        public IActionResult OnGet()
        {
            if (!_currentTenant.IsResolved || !_currentTenant.IsActive)
            {
                return RedirectToPage("/Index");
            }
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!_currentTenant.IsResolved || !_currentTenant.IsActive)
            {
                return RedirectToPage("/Index");
            }
            if (!ModelState.IsValid) return Page();

            var started = DateTime.UtcNow;
            var targetEmail = Email.Trim().ToLowerInvariant();

            Profile? profile = null;
            try
            {
                profile = await _supabase.From<Profile>()
                    .Where(x => x.Email == targetEmail)
                    .Where(x => x.TenantId == _currentTenant.Id)
                    .Single();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao buscar perfil no pedido de reset de senha para {Email}", targetEmail);
            }

            // Gera um token mesmo quando o perfil não existe — CPU
            // equivalente nos dois caminhos, parte da defesa contra
            // enumeração por tempo de resposta (o token do caminho "não
            // encontrado" nunca é persistido).
            var (rawToken, tokenHash) = EmailTokenService.GenerateToken();

            if (profile is not null)
            {
                try
                {
                    await _supabase.From<Profile>()
                        .Where(x => x.Id == profile.Id)
                        .Set(x => x.ActionTokenHash, tokenHash)
                        .Set(x => x.ActionTokenType, "password_reset")
                        .Set(x => x.ActionTokenExpiresAt, DateTime.UtcNow.AddMinutes(30))
                        .Update();

                    var resetUrl = BuildAbsoluteUrl("/Auth/RedefinirSenha", rawToken);
                    var html = $"<p>Recebemos um pedido para redefinir sua senha no MarcAi.</p>" +
                        $"<p><a href=\"{WebUtility.HtmlEncode(resetUrl)}\">Clique aqui para escolher uma senha nova</a> " +
                        $"— o link vale por 30 minutos.</p>" +
                        $"<p>Se você não pediu isso, pode ignorar este e-mail com segurança.</p>";

                    await _emailService.SendEmailAsync(profile.Email, "Redefinir sua senha — MarcAi", html);
                }
                catch (Exception ex)
                {
                    // Resend fora do ar (ou qualquer outra falha aqui) nunca
                    // pode mudar a mensagem mostrada — só loga e segue.
                    _logger.LogError(ex, "Falha ao processar pedido de reset de senha para {Email}", targetEmail);
                }
            }

            // Piso mínimo de tempo de resposta pra não deixar a diferença
            // entre "e-mail existe" (mais trabalho: update + envio) e "não
            // existe" (só gera um token e descarta) perceptível por
            // latência — reforço além da mensagem já ser sempre idêntica.
            var elapsed = DateTime.UtcNow - started;
            var floor = TimeSpan.FromMilliseconds(250);
            if (elapsed < floor)
            {
                await Task.Delay(floor - elapsed);
            }

            SuccessMessage = "Se esse e-mail estiver cadastrado, enviamos um link de redefinição de senha. Confira sua caixa de entrada (e o spam).";
            return Page();
        }

        private string BuildAbsoluteUrl(string path, string token)
        {
            var rootDomain = _configuration["Tenancy:RootDomain"] ?? "localhost";
            var scheme = Request.IsHttps ? "https" : "http";
            var port = Request.Host.Port.HasValue ? $":{Request.Host.Port}" : "";
            return $"{scheme}://{_currentTenant.Slug}.{rootDomain}{port}{path}?token={token}";
        }
    }
}
