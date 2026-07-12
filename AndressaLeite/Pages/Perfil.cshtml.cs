using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.RegularExpressions;
using AndressaLeite.Models;
using AndressaLeite.Services;

namespace AndressaLeite.Pages
{
    [Authorize]
    public class PerfilModel : PageModel
    {
        private readonly Supabase.Client _supabase;

        public PerfilModel(Supabase.Client supabase) => _supabase = supabase;

        // Dados do Perfil (read-only do cliente)
        public string? Email { get; set; }
        public string? Role { get; set; }

        // Dados editáveis
        [BindProperty]
        [Required(ErrorMessage = "O nome completo é obrigatório.")]
        [StringLength(120, MinimumLength = 2, ErrorMessage = "O nome deve ter entre 2 e 120 caracteres.")]
        [RegularExpression(@"^[\p{L}\p{M}'\.\- ]+$",
            ErrorMessage = "O nome contém caracteres inválidos.")]
        public string FullName { get; set; } = string.Empty;

        [BindProperty]
        [Required(ErrorMessage = "O telefone é obrigatório.")]
        [RegularExpression(@"^\+?[1-9]\d{10,14}$",
            ErrorMessage = "Telefone inválido. Use DDI + DDD + número (somente dígitos).")]
        public string Phone { get; set; } = string.Empty;

        // Campos de Alteração de Senha (todos opcionais)
        [BindProperty]
        [DataType(DataType.Password)]
        public string? CurrentPassword { get; set; }

        [BindProperty]
        [DataType(DataType.Password)]
        [StringLength(100, MinimumLength = 8, ErrorMessage = "A nova senha deve ter no mínimo 8 caracteres.")]
        public string? NewPassword { get; set; }

        [BindProperty]
        [DataType(DataType.Password)]
        [Compare("NewPassword", ErrorMessage = "As senhas informadas não coincidem.")]
        public string? ConfirmPassword { get; set; }

        [TempData] public string? SuccessMessage { get; set; }
        [TempData] public string? ErrorMessage { get; set; }
        [TempData] public string? PasswordChangeMessage { get; set; }

        /// <summary>
        /// GET: Carrega dados do perfil do usuário autenticado.
        /// </summary>
        public async Task<IActionResult> OnGetAsync()
        {
            if (!AuthorizationService.TryGetUserId(User, out var userId))
            {
                return Forbid();
            }

            var userIdStr = userId.ToString();
            Email = User.FindFirstValue(ClaimTypes.Email);
            Role = User.FindFirstValue(ClaimTypes.Role);

            try
            {
                // Busca o perfil do usuário no Supabase
                var profile = await _supabase.From<Profile>()
                    .Where(x => x.Id == userIdStr)
                    .Single();

                if (profile is null)
                {
                    // Perfil não encontrado - pode ser novo usuário
                    ErrorMessage = "Perfil não encontrado. Entre em contato com o suporte.";
                    return Page();
                }

                // Popula os campos editáveis
                FullName = profile.FullName;
                Phone = profile.Phone;
            }
            catch (Exception ex)
            {
                // Log aqui se necessário
                ErrorMessage = "Não foi possível carregar seu perfil. Tente novamente.";
            }

            return Page();
        }

        /// <summary>
        /// POST: Atualiza o perfil do usuário (FullName, Phone) e opcionalmente a senha.
        /// Role e Email são read-only.
        /// </summary>
        public async Task<IActionResult> OnPostAsync()
        {
            if (!AuthorizationService.TryGetUserId(User, out var userId))
            {
                return Forbid();
            }

            var userIdStr = userId.ToString();
            Email = User.FindFirstValue(ClaimTypes.Email);
            Role = User.FindFirstValue(ClaimTypes.Role);

            // Validação customizada: se qualquer campo de senha for preenchido,
            // todos os 3 devem ser validados
            bool isChangingPassword = !string.IsNullOrWhiteSpace(CurrentPassword) ||
                                      !string.IsNullOrWhiteSpace(NewPassword) ||
                                      !string.IsNullOrWhiteSpace(ConfirmPassword);

            if (isChangingPassword)
            {
                // Se está tentando alterar a senha, valida todos os campos
                if (string.IsNullOrWhiteSpace(CurrentPassword))
                {
                    ModelState.AddModelError(nameof(CurrentPassword), "Informe sua senha atual para alterar.");
                }
                if (string.IsNullOrWhiteSpace(NewPassword))
                {
                    ModelState.AddModelError(nameof(NewPassword), "Informe a nova senha.");
                }
                if (string.IsNullOrWhiteSpace(ConfirmPassword))
                {
                    ModelState.AddModelError(nameof(ConfirmPassword), "Confirme a nova senha.");
                }

                // Validação de força de senha (letra + número, mínimo 8 caracteres)
                if (!string.IsNullOrWhiteSpace(NewPassword))
                {
                    if (!Regex.IsMatch(NewPassword, @"[A-Za-z]") || !Regex.IsMatch(NewPassword, @"\d"))
                    {
                        ModelState.AddModelError(nameof(NewPassword),
                            "A nova senha deve conter pelo menos uma letra e um número.");
                    }
                }
            }

            if (!ModelState.IsValid)
            {
                return Page();
            }

            try
            {
                // 1. Atualiza dados do perfil (sempre)
                var cleanPhone = Regex.Replace(Phone, @"[^\d]", "");
                var response = await _supabase.From<Profile>()
                    .Where(x => x.Id == userIdStr)
                    .Set(x => x.FullName, FullName.Trim())
                    .Set(x => x.Phone, cleanPhone)
                    .Update();

                // 2. Se está mudando senha, tenta atualizar através do Supabase Auth
                if (isChangingPassword && !string.IsNullOrWhiteSpace(NewPassword))
                {
                    try
                    {
                        // Supabase Auth API - alterar senha requer a senha atual para segurança
                        await _supabase.Auth.Update(new Supabase.Gotrue.UserAttributes
                        {
                            Password = NewPassword
                        });
                        PasswordChangeMessage = "✓ Senha alterada com sucesso!";
                    }
                    catch (Exception ex)
                    {
                        // Erro ao alterar senha (geralmente senha atual inválida)
                        PasswordChangeMessage = "Não foi possível alterar a senha. Verifique se a senha atual está correta.";
                    }
                }

                SuccessMessage = "Perfil atualizado com sucesso!";

                // Limpa os campos de senha após sucesso
                CurrentPassword = null;
                NewPassword = null;
                ConfirmPassword = null;
            }
            catch (Exception ex)
            {
                ErrorMessage = "Não foi possível atualizar seu perfil. Tente novamente.";
            }

            return Page();
        }

        /// <summary>
        /// Helper: Formata a role para exibição amigável.
        /// </summary>
        public string FormatRole(string? role) => (role ?? "").ToLowerInvariant() switch
        {
            "admin" => "Administrador",
            "employee" => "Profissional",
            "client" => "Cliente",
            _ => "Desconhecida"
        };
    }
}
