using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Supabase.Gotrue;
using Postgrest.Exceptions;
using AndressaLeite.Models;
using AndressaLeite.Services;
using System.Security.Claims;
using System.ComponentModel.DataAnnotations;

namespace AndressaLeite.Pages.Admin
{
    [Authorize(Policy = "AdminOnly")]
    public class DashAdminModel : PageModel
    {
        private readonly Supabase.Client _supabase;
        public DashAdminModel(Supabase.Client supabase) => _supabase = supabase;

        public decimal EstimatedRevenue { get; set; } = 0.00m;
        public decimal ActualRevenue { get; set; } = 0.00m;
        public List<Profile> ActiveEmployees { get; set; } = new();

        [TempData] public string? SuccessMessage { get; set; }
        [TempData] public string? ErrorMessage { get; set; }

        public async Task<IActionResult> OnGetAsync()
        {
            if (AuthorizationService.GetRole(User) != "admin")
            {
                return Forbid();
            }

            var employeesResponse = await _supabase.From<Profile>()
                .Where(x => x.Role == "employee")
                .Get();
            ActiveEmployees = employeesResponse.Models;

            var appointmentsResponse = await _supabase.From<Appointment>().Get();
            var list = appointmentsResponse.Models;

            EstimatedRevenue = list.Where(x => x.Status != "cancelled").Sum(x => x.EstimatedRevenue);
            ActualRevenue = list.Where(x => x.Status == "completed").Sum(x => x.ActualRevenue);

            return Page();
        }

        /// <summary>
        /// POST: Adiciona um novo profissional (employee) ao sistema.
        /// Como o app é ilustrativo (sem autenticação), cria apenas um registro na tabela Profile.
        /// </summary>
        public async Task<IActionResult> OnPostAddEmployeeAsync(
            [FromForm] string EmpName,
            [FromForm] string EmpEmail,
            [FromForm] string EmpPhone,
            [FromForm] string EmpPassword)
        {
            if (AuthorizationService.GetRole(User) != "admin")
            {
                return Forbid();
            }

            // Validação server-side
            if (string.IsNullOrWhiteSpace(EmpName) || EmpName.Length < 2)
            {
                ErrorMessage = "Nome do profissional é obrigatório e deve ter pelo menos 2 caracteres.";
                return RedirectToPage();
            }

            if (string.IsNullOrWhiteSpace(EmpEmail) || !new EmailAddressAttribute().IsValid(EmpEmail))
            {
                ErrorMessage = "E-mail inválido.";
                return RedirectToPage();
            }

            if (string.IsNullOrWhiteSpace(EmpPassword) || EmpPassword.Length < 8)
            {
                ErrorMessage = "Senha deve ter no mínimo 8 caracteres.";
                return RedirectToPage();
            }

            var cleanPhone = System.Text.RegularExpressions.Regex.Replace(EmpPhone ?? "", @"[^\d]", "");

            // Log para debug
            System.Diagnostics.Debug.WriteLine($"DEBUG: EmpPhone original: '{EmpPhone}'");
            System.Diagnostics.Debug.WriteLine($"DEBUG: cleanPhone: '{cleanPhone}'");

            // Validação de telefone (permitir vazio também, já que é ilustrativo)
            if (!string.IsNullOrWhiteSpace(cleanPhone) && 
                !System.Text.RegularExpressions.Regex.IsMatch(cleanPhone, @"^\+?[1-9]\d{10,14}$"))
            {
                ErrorMessage = "Telefone inválido. Use DDI + DDD + número (ex: +5515988888888).";
                return RedirectToPage();
            }

            // Se telefone estiver vazio, usar um padrão
            if (string.IsNullOrWhiteSpace(cleanPhone))
            {
                cleanPhone = "11999999999"; // Telefone padrão para testes
            }

            // Validação extra de força de senha
            if (!System.Text.RegularExpressions.Regex.IsMatch(EmpPassword, @"[A-Za-z]") || 
                !System.Text.RegularExpressions.Regex.IsMatch(EmpPassword, @"\d"))
            {
                ErrorMessage = "Senha deve conter pelo menos uma letra e um número.";
                return RedirectToPage();
            }

            try
            {
                // Gerar um ID único para o profissional (usando Guid)
                string profileId = Guid.NewGuid().ToString();

                // Criar perfil no banco com role "employee"
                var profileData = new Profile
                {
                    Id = profileId,
                    FullName = EmpName.Trim(),
                    Phone = cleanPhone,
                    Role = "employee"
                };

                System.Diagnostics.Debug.WriteLine($"DEBUG: Inserting profile - Id: {profileId}, Name: {EmpName}, Phone: {cleanPhone}, Role: employee");

                // Inserir o profissional na tabela profiles (sem criar usuário no Auth)
                await _supabase.From<Profile>().Insert(profileData);

                // Se precisar logar depois, o profissional usará email + senha
                // (você pode implementar um "login simulado" sem Auth do Supabase)

                SuccessMessage = $"✅ Profissional '{EmpName}' adicionado à equipe com sucesso! " +
                    $"Email de acesso: {EmpEmail}";
                return RedirectToPage();
            }
            catch (PostgrestException pgex)
            {
                // Erro de constraint ou dados duplicados
                System.Diagnostics.Debug.WriteLine($"DEBUG: PostgrestException - {pgex.Message}");

                if (pgex.Message.Contains("23505") || pgex.Message.Contains("duplicate"))
                {
                    ErrorMessage = "E-mail já cadastrado no sistema.";
                }
                else if (pgex.Message.Contains("23502"))
                {
                    ErrorMessage = $"Erro: coluna obrigatória vazia ao criar profissional. Verifique os dados.";
                }
                else
                {
                    ErrorMessage = $"Erro ao salvar perfil: {pgex.Message}";
                }
                return RedirectToPage();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DEBUG: Exception - {ex.GetType().Name}: {ex.Message}");
                ErrorMessage = $"Erro inesperado: {ex.Message}";
                return RedirectToPage();
            }
        }

        /// <summary>
        /// POST: Remove um profissional (DELETE da tabela).
        /// </summary>
        public async Task<IActionResult> OnPostRemoveEmployeeAsync(string id)
        {
            if (AuthorizationService.GetRole(User) != "admin")
            {
                return Forbid();
            }

            if (string.IsNullOrWhiteSpace(id))
            {
                ErrorMessage = "ID do profissional inválido.";
                return RedirectToPage();
            }

            try
            {
                // Delete o profissional da tabela (remove completamente)
                await _supabase.From<Profile>()
                    .Where(x => x.Id == id)
                    .Delete();

                SuccessMessage = "Profissional removido da equipe com sucesso.";
            }
            catch (PostgrestException pgex)
            {
                if (pgex.Message.Contains("23514"))
                {
                    ErrorMessage = "Erro de constraint no banco: não foi possível remover. Contate o administrador.";
                }
                else if (pgex.Message.Contains("23503"))
                {
                    ErrorMessage = "Não é possível remover profissional com agendamentos associados.";
                }
                else
                {
                    ErrorMessage = $"Erro ao remover: {pgex.Message}";
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Erro inesperado: {ex.Message}";
            }

            return RedirectToPage();
        }
    }
}
