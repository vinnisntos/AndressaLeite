using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using AndressaLeite.Models;
using AndressaLeite.Services;

namespace AndressaLeite.Pages.Auth
{
    /// <summary>
    /// Consome o token de verificação de e-mail (readme.txt 4.2) — emitido
    /// no cadastro/onboarding (Cadastro.cshtml.cs/CriarSalao.cshtml.cs) ou
    /// reenviado por Perfil.cshtml.cs. Ação em GET (não POST) de propósito:
    /// é um link clicado direto do e-mail, sem formulário — mesmo padrão
    /// de link de confirmação/unsubscribe comum no mercado; não há nada
    /// sensível o suficiente aqui pra exigir uma etapa de confirmação
    /// extra (só marca um booleano, não altera senha/dados).
    /// </summary>
    public class VerificarEmailModel : PageModel
    {
        private readonly Supabase.Client _supabase;
        private readonly ILogger<VerificarEmailModel> _logger;
        private readonly CurrentTenant _currentTenant;

        public VerificarEmailModel(Supabase.Client supabase, ILogger<VerificarEmailModel> logger, CurrentTenant currentTenant)
        {
            _supabase = supabase;
            _logger = logger;
            _currentTenant = currentTenant;
        }

        [BindProperty(SupportsGet = true)]
        public string? Token { get; set; }

        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }

        public async Task<IActionResult> OnGetAsync()
        {
            if (!_currentTenant.IsResolved)
            {
                return RedirectToPage("/Index");
            }

            if (string.IsNullOrWhiteSpace(Token))
            {
                ErrorMessage = "Link de verificação inválido.";
                return Page();
            }

            try
            {
                var hash = EmailTokenService.Hash(Token);
                var profile = await _supabase.From<Profile>()
                    .Where(x => x.ActionTokenHash == hash)
                    .Where(x => x.TenantId == _currentTenant.Id)
                    .Single();

                // Profile.ActionTokenExpiresAt já normaliza Kind=Utc no
                // próprio setter do modelo — não precisa corrigir aqui.
                if (profile is null || profile.ActionTokenType != "email_verification" ||
                    EmailTokenService.IsExpired(profile.ActionTokenExpiresAt))
                {
                    ErrorMessage = "Este link é inválido ou já expirou.";
                    return Page();
                }

                // .Set(x => x.Campo, null) quebra no postgrest-csharp 3.5.1
                // (ver RedefinirSenha.cshtml.cs) — Update() do objeto
                // completo funciona normalmente pra limpar colunas nullable.
                profile.EmailVerified = true;
                // Uso único: limpa o token assim que ele é consumido.
                profile.ActionTokenHash = null;
                profile.ActionTokenType = null;
                profile.ActionTokenExpiresAt = null;
                await _supabase.From<Profile>().Update(profile);

                Success = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao verificar e-mail com token");
                ErrorMessage = "Não foi possível confirmar seu e-mail agora. Tente novamente.";
            }

            return Page();
        }
    }
}
