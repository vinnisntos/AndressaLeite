using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using AndressaLeite.Models;
using AndressaLeite.Services;
using BCrypt.Net;

namespace AndressaLeite.Pages.Auth
{
    /// <summary>
    /// Segundo passo do reset de senha (readme.txt 4.1) — consome o token
    /// emitido por EsqueciSenha.cshtml.cs. Valida no GET (pra decidir se
    /// mostra o formulário) e de novo no POST (nunca confia só no estado
    /// carregado no GET — o token pode ter expirado/sido usado entre um
    /// request e outro).
    /// </summary>
    public class RedefinirSenhaModel : PageModel
    {
        private readonly Supabase.Client _supabase;
        private readonly ILogger<RedefinirSenhaModel> _logger;
        private readonly CurrentTenant _currentTenant;

        public RedefinirSenhaModel(Supabase.Client supabase, ILogger<RedefinirSenhaModel> logger, CurrentTenant currentTenant)
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

        public async Task<IActionResult> OnGetAsync()
        {
            if (!_currentTenant.IsResolved || !_currentTenant.IsActive)
            {
                return RedirectToPage("/Index");
            }

            var profile = await FindProfileByTokenAsync(Token);
            TokenIsValid = profile is not null;
            if (!TokenIsValid)
            {
                ErrorMessage = "Este link é inválido ou já expirou. Peça um novo em \"Esqueci minha senha\".";
            }
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!_currentTenant.IsResolved || !_currentTenant.IsActive)
            {
                return RedirectToPage("/Index");
            }

            // Revalida tudo de novo — não confia no TokenIsValid do GET.
            var profile = await FindProfileByTokenAsync(Token);
            if (profile is null)
            {
                TokenIsValid = false;
                ErrorMessage = "Este link é inválido ou já expirou. Peça um novo em \"Esqueci minha senha\".";
                return Page();
            }
            TokenIsValid = true;

            if (!ModelState.IsValid) return Page();

            if (!Regex.IsMatch(Password, @"[A-Za-z]") || !Regex.IsMatch(Password, @"\d"))
            {
                ModelState.AddModelError(nameof(Password), "A senha deve conter pelo menos uma letra e um número.");
                return Page();
            }

            try
            {
                // .Set(x => x.Campo, null) quebra no postgrest-csharp 3.5.1
                // (ArgumentException: "Expected Value to be of Type: String,
                // instead received: .") — achado desta rodada. Update() do
                // objeto completo (já carregado por FindProfileByTokenAsync)
                // funciona normalmente pra limpar colunas nullable.
                profile.PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password);
                // Uso único: limpa o token assim que ele é consumido.
                profile.ActionTokenHash = null;
                profile.ActionTokenType = null;
                profile.ActionTokenExpiresAt = null;
                await _supabase.From<Profile>().Update(profile);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao redefinir senha para o perfil {ProfileId}", profile.Id);
                ErrorMessage = "Não foi possível redefinir sua senha agora. Tente novamente em instantes.";
                return Page();
            }

            TempData["InfoMessage"] = "Senha redefinida com sucesso. Faça login com a nova senha.";
            return RedirectToPage("/Auth/Login");
        }

        /// <summary>
        /// Busca o perfil pelo hash do token, validando tipo e expiração —
        /// usado tanto no GET quanto no POST (nunca confia em validação
        /// feita numa requisição anterior).
        /// </summary>
        private async Task<Profile?> FindProfileByTokenAsync(string? rawToken)
        {
            if (string.IsNullOrWhiteSpace(rawToken) || !_currentTenant.IsResolved) return null;

            var hash = EmailTokenService.Hash(rawToken);
            var profile = await _supabase.From<Profile>()
                .Where(x => x.ActionTokenHash == hash)
                .Where(x => x.TenantId == _currentTenant.Id)
                .Single();

            if (profile is null) return null;
            if (profile.ActionTokenType != "password_reset") return null;
            // Profile.ActionTokenExpiresAt já normaliza Kind=Utc no
            // próprio setter do modelo — não precisa corrigir aqui.
            if (EmailTokenService.IsExpired(profile.ActionTokenExpiresAt)) return null;

            return profile;
        }
    }
}
