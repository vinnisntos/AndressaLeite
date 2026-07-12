using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using AndressaLeite.Models;
using AndressaLeite.Services;

namespace AndressaLeite.Pages.Cliente
{
    [Authorize(Policy = "ClientOnly")]
    public class DashClienteModel : PageModel
    {
        private readonly Supabase.Client _supabase;
        public DashClienteModel(Supabase.Client supabase) => _supabase = supabase;

        public string CurrentStep { get; set; } = "home";
        public string ClientName { get; set; } = "Cliente";

        [TempData] public string? SuccessMessage { get; set; }
        [TempData] public string? ErrorMessage { get; set; }

        public List<Profile> Employees { get; set; } = new();
        public List<Service> Services { get; set; } = new();
        public List<AppointmentDTO> ActiveAppointments { get; set; } = new();

        [BindProperty(SupportsGet = true)] public string? SelectedProId { get; set; }
        [BindProperty(SupportsGet = true)] public string? SelectedSrvId { get; set; }
        [BindProperty] public DateTime SelectedDateTime { get; set; } = DateTime.Now.AddDays(1);
        [BindProperty] public string? BookForName { get; set; }
        [BindProperty] public string? BookForPhone { get; set; }

        public async Task<IActionResult> OnGetAsync([FromQuery] string step = "home")
        {
            if (!AuthorizationService.TryGetUserId(User, out var userId))
            {
                return Forbid();
            }

            CurrentStep = step;
            var userIdStr = userId.ToString();

            var profile = await _supabase.From<Profile>().Where(x => x.Id == userIdStr).Single();
            if (profile != null) ClientName = profile.FullName;

            if (CurrentStep == "home")
            {
                // PGRST100 FIX: postgrest-csharp não traduz operador || (OR) diretamente.
                // Solução: usar duas queries separadas (cada uma é um Where em AND interno).
                // Cada query busca agendamentos do cliente com um status específico,
                // depois combinamos os resultados em memória.
                var pendingResponse = await _supabase.From<Appointment>()
                    .Where(x => x.ClientId == userIdStr && x.Status == "pending")
                    .Get();

                var confirmedResponse = await _supabase.From<Appointment>()
                    .Where(x => x.ClientId == userIdStr && x.Status == "confirmed")
                    .Get();

                // Combina resultados e remove duplicatas (improvável, mas seguro)
                var allAppointments = pendingResponse.Models
                    .Concat(confirmedResponse.Models)
                    .GroupBy(a => a.Id)
                    .Select(g => g.First())
                    .ToList();

                ActiveAppointments = allAppointments.Select(a => new AppointmentDTO
                {
                    Id = a.Id,
                    ServiceName = "Design de Sobrancelha Premium",
                    EmployeeName = "Paula Designer",
                    StartTime = a.StartTime,
                    BookedForName = a.BookedForName
                }).ToList();
            }
            else if (CurrentStep == "pro")
            {
                var pros = await _supabase.From<Profile>().Where(x => x.Role == "employee").Get();
                Employees = pros.Models;
            }
            else if (CurrentStep == "service")
            {
                // Só permite avançar se SelectedProId for de fato um employee ativo.
                if (string.IsNullOrWhiteSpace(SelectedProId))
                {
                    ErrorMessage = "Selecione uma profissional antes de escolher o serviço.";
                    return RedirectToPage("/Cliente/DashCliente", new { step = "pro" });
                }
                var proCheck = await _supabase.From<Profile>().Where(x => x.Id == SelectedProId).Single();
                if (proCheck is null || proCheck.Role != "employee")
                {
                    ErrorMessage = "Profissional inválida.";
                    return RedirectToPage("/Cliente/DashCliente", new { step = "pro" });
                }

                var srvs = await _supabase.From<Service>().Where(x => x.IsActive == true).Get();
                Services = srvs.Models;
            }

            return Page();
        }

        public async Task<IActionResult> OnPostBookAsync()
        {
            if (!AuthorizationService.TryGetUserId(User, out var userId))
            {
                return Forbid();
            }
            var userIdStr = userId.ToString();

            // 1) Rejeitar no passado.
            if (SelectedDateTime <= DateTime.Now)
            {
                ErrorMessage = "Não é possível agendar para uma data no passado.";
                return RedirectToPage("/Cliente/DashCliente", new { step = "home" });
            }

            // 2) Validar que SelectedProId é um employee real.
            if (string.IsNullOrWhiteSpace(SelectedProId))
            {
                ErrorMessage = "Profissional inválida.";
                return RedirectToPage("/Cliente/DashCliente", new { step = "pro" });
            }
            var proCheck = await _supabase.From<Profile>().Where(x => x.Id == SelectedProId).Single();
            if (proCheck is null || proCheck.Role != "employee")
            {
                ErrorMessage = "Profissional inválida.";
                return RedirectToPage("/Cliente/DashCliente", new { step = "pro" });
            }

            // 3) Validar que SelectedSrvId é um serviço ativo.
            if (string.IsNullOrWhiteSpace(SelectedSrvId))
            {
                ErrorMessage = "Serviço inválido.";
                return RedirectToPage("/Cliente/DashCliente", new { step = "service", proId = SelectedProId });
            }
            var srvCheck = await _supabase.From<Service>().Where(x => x.Id == SelectedSrvId).Single();
            if (srvCheck is null || !srvCheck.IsActive)
            {
                ErrorMessage = "Serviço inválido ou inativo.";
                return RedirectToPage("/Cliente/DashCliente", new { step = "service", proId = SelectedProId });
            }

            try
            {
                var newAppointment = new Appointment
                {
                    ClientId = userIdStr,
                    EmployeeId = SelectedProId!,
                    ServiceId = SelectedSrvId!,
                    StartTime = SelectedDateTime.ToUniversalTime(),
                    BookedForName = string.IsNullOrWhiteSpace(BookForName) ? null : BookForName.Trim(),
                    BookedForPhone = string.IsNullOrWhiteSpace(BookForPhone) ? null : BookForPhone.Trim(),
                    Status = "pending"
                };

                await _supabase.From<Appointment>().Insert(newAppointment);
                SuccessMessage = "Agendamento realizado com sucesso!";
            }
            catch
            {
                ErrorMessage = "Não foi possível criar o agendamento. Tente novamente.";
            }
            return RedirectToPage("/Cliente/DashCliente", new { step = "home" });
        }

        public async Task<IActionResult> OnPostCancelAsync(string id)
        {
            if (!AuthorizationService.TryGetUserId(User, out var userId))
            {
                return Forbid();
            }
            if (string.IsNullOrWhiteSpace(id))
            {
                ErrorMessage = "Agendamento inválido.";
                return RedirectToPage("/Cliente/DashCliente", new { step = "home" });
            }

            try
            {
                // BUG RAIZ DE SEGURANÇA: antes, qualquer cliente podia cancelar
                // qualquer agendamento passando o id. Agora: valida posse.
                var existing = await _supabase.From<Appointment>()
                    .Where(x => x.Id == id)
                    .Single();

                if (existing is null)
                {
                    ErrorMessage = "Agendamento não encontrado.";
                    return RedirectToPage("/Cliente/DashCliente", new { step = "home" });
                }

                if (existing.ClientId != userId.ToString())
                {
                    // 403: o cliente tentou cancelar agendamento de outro.
                    return Forbid();
                }

                // A trigger do banco (trigger_check_cancellation) já trava
                // cancelamentos com menos de 1h de antecedência.
                await _supabase.From<Appointment>()
                    .Where(x => x.Id == id)
                    .Set(x => x.Status, "cancelled")
                    .Update();

                SuccessMessage = "Agendamento cancelado com sucesso.";
            }
            catch
            {
                ErrorMessage = "Não foi possível cancelar. Tente novamente.";
            }
            return RedirectToPage("/Cliente/DashCliente", new { step = "home" });
        }
    }
}
