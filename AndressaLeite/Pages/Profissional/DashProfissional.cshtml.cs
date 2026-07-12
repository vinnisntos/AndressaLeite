using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using AndressaLeite.Services;

namespace AndressaLeite.Pages.Profissional
{
    [Authorize(Policy = "EmployeeOnly")]
    public class DashProfissionalModel : PageModel
    {
        private readonly Supabase.Client _supabase;
        public DashProfissionalModel(Supabase.Client supabase) => _supabase = supabase;

        public string EmployeeName { get; set; } = "Profissional";
        public List<AppointmentView> AppointmentsToday { get; set; } = new();

        public async Task<IActionResult> OnGetAsync()
        {
            if (!AuthorizationService.TryGetUserId(User, out var userId))
            {
                return Forbid();
            }

            var profile = await _supabase.From<Models.Profile>().Where(x => x.Id == userId.ToString()).Single();
            if (profile is not null) EmployeeName = profile.FullName;

            // Lista appointments do dia para esta profissional.
            var startOfDay = DateTime.UtcNow.Date;
            var endOfDay = startOfDay.AddDays(1);

            var response = await _supabase.From<Models.Appointment>()
                .Where(x => x.EmployeeId == userId.ToString()
                            && x.StartTime >= startOfDay
                            && x.StartTime < endOfDay
                            && x.Status != "cancelled")
                .Get();

            // Mantém simples: nomes vêm "decorados" — refine depois com JOIN.
            AppointmentsToday = response.Models.Select(a => new AppointmentView
            {
                StartTime = a.StartTime,
                ClientName = a.BookedForName ?? "Cliente",
                ServiceName = "Atendimento",
                BookedForName = a.BookedForName,
                Status = a.Status
            }).ToList();

            return Page();
        }

        public class AppointmentView
        {
            public DateTime StartTime { get; set; }
            public string ClientName { get; set; } = string.Empty;
            public string ServiceName { get; set; } = string.Empty;
            public string? BookedForName { get; set; }
            public string Status { get; set; } = "pending";
        }
    }
}
