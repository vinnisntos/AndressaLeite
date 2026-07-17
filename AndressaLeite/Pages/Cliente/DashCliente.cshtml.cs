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
        private readonly ILogger<DashClienteModel> _logger;
        private readonly CurrentTenant _currentTenant;
        private readonly AppointmentBookingService _bookingService;

        public DashClienteModel(Supabase.Client supabase, ILogger<DashClienteModel> logger, CurrentTenant currentTenant, AppointmentBookingService bookingService)
        {
            _supabase = supabase;
            _logger = logger;
            _currentTenant = currentTenant;
            _bookingService = bookingService;
        }

        public string CurrentStep { get; set; } = "home";
        public string ClientName { get; set; } = "Cliente";

        [TempData] public string? SuccessMessage { get; set; }
        [TempData] public string? ErrorMessage { get; set; }

        public List<Profile> Employees { get; set; } = new();
        public List<ServiceOption> Services { get; set; } = new();
        public List<AppointmentDTO> ActiveAppointments { get; set; } = new();

        /// <summary>Últimos agendamentos concluídos/cancelados — ver step "historico" (item 5.4 do readme.txt).</summary>
        public List<AppointmentDTO> HistoryAppointments { get; set; } = new();
        private const int HistoryPageSize = 10;

        /// <summary>Página do histórico (0-based) — paginação leve, item 5.1 do readme.txt.</summary>
        [BindProperty(SupportsGet = true)]
        public int HistoryPage { get; set; }
        public bool HasMoreHistory { get; set; }

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
            // Guarda explícita: sem tenant resolvido não há como filtrar as
            // queries com segurança (evita filtrar por TenantId == null).
            if (!_currentTenant.IsResolved)
            {
                return Forbid();
            }
            var tenantId = _currentTenant.Id!;

            CurrentStep = step;
            var userIdStr = userId.ToString();

            var profile = await _supabase.From<Profile>()
                .Where(x => x.Id == userIdStr)
                .Where(x => x.TenantId == tenantId)
                .Single();
            if (profile != null) ClientName = profile.FullName;

            if (CurrentStep == "home")
            {
                // PGRST100 FIX: postgrest-csharp não traduz operador || (OR) diretamente.
                // Solução: usar duas queries separadas (cada uma é um Where em AND interno).
                // Cada query busca agendamentos do cliente com um status específico,
                // depois combinamos os resultados em memória.
                var pendingResponse = await _supabase.From<Appointment>()
                    .Where(x => x.ClientId == userIdStr && x.Status == "pending")
                    .Where(x => x.TenantId == tenantId)
                    .Get();

                var confirmedResponse = await _supabase.From<Appointment>()
                    .Where(x => x.ClientId == userIdStr && x.Status == "confirmed")
                    .Where(x => x.TenantId == tenantId)
                    .Get();

                // Combina resultados e remove duplicatas (improvável, mas seguro)
                var allAppointments = pendingResponse.Models
                    .Concat(confirmedResponse.Models)
                    .GroupBy(a => a.Id)
                    .Select(g => g.First())
                    .ToList();

                // Resolve nome real do serviço e da profissional (antes vinham
                // fixos/hardcoded). Volume é baixo (agendamentos ativos de um
                // único cliente), então busca individual por id é aceitável.
                var (servicesById, employeesById) = await ResolveNamesAsync(allAppointments);

                ActiveAppointments = allAppointments.Select(a => new AppointmentDTO
                {
                    Id = a.Id,
                    ServiceName = servicesById.TryGetValue(a.ServiceId, out var svc) ? svc.Name : "Serviço",
                    EmployeeName = employeesById.TryGetValue(a.EmployeeId, out var emp) ? emp.FullName : "Profissional",
                    StartTime = a.StartTime,
                    BookedForName = a.BookedForName,
                    Status = a.Status
                }).ToList();
            }
            else if (CurrentStep == "historico")
            {
                // Últimos concluídos/cancelados (item 5.4 do readme.txt) —
                // duas queries separadas concatenadas, mesmo padrão de OR
                // já usado no step "home" pra contornar o bug do driver.
                var completedResponse = await _supabase.From<Appointment>()
                    .Where(x => x.ClientId == userIdStr && x.Status == "completed")
                    .Where(x => x.TenantId == tenantId)
                    .Get();

                var cancelledResponse = await _supabase.From<Appointment>()
                    .Where(x => x.ClientId == userIdStr && x.Status == "cancelled")
                    .Where(x => x.TenantId == tenantId)
                    .Get();

                var allPastAppointments = completedResponse.Models
                    .Concat(cancelledResponse.Models)
                    .OrderByDescending(a => a.StartTime)
                    .ToList();

                // Paginação simples em memória (item 5.1 do readme.txt):
                // aceitável no volume de agendamentos de um único cliente.
                // Busca uma linha a mais só pra saber se tem próxima página,
                // sem precisar de outra query.
                var pastAppointments = allPastAppointments
                    .Skip(HistoryPage * HistoryPageSize)
                    .Take(HistoryPageSize)
                    .ToList();
                HasMoreHistory = allPastAppointments.Count > (HistoryPage + 1) * HistoryPageSize;

                var (histServicesById, histEmployeesById) = await ResolveNamesAsync(pastAppointments);

                HistoryAppointments = pastAppointments.Select(a => new AppointmentDTO
                {
                    Id = a.Id,
                    ServiceName = histServicesById.TryGetValue(a.ServiceId, out var svc) ? svc.Name : "Serviço",
                    EmployeeName = histEmployeesById.TryGetValue(a.EmployeeId, out var emp) ? emp.FullName : "Profissional",
                    StartTime = a.StartTime,
                    BookedForName = a.BookedForName,
                    Status = a.Status
                }).ToList();
            }
            else if (CurrentStep == "pro")
            {
                // Admin é "profissional premium" (Fase 6 do roadmap): também
                // atende clientes, além de gerenciar. Duas queries separadas
                // e concatenadas em memória em vez de Role=="employee" ||
                // Role=="admin" num só Where — mesmo padrão já usado no
                // projeto pra contornar o bug de OR do driver postgrest-csharp.
                var employeesResponse = await _supabase.From<Profile>()
                    .Where(x => x.Role == "employee")
                    .Where(x => x.TenantId == tenantId)
                    .Get();

                var adminsResponse = await _supabase.From<Profile>()
                    .Where(x => x.Role == "admin")
                    .Where(x => x.TenantId == tenantId)
                    .Get();

                Employees = employeesResponse.Models
                    .Concat(adminsResponse.Models)
                    .OrderBy(p => p.FullName)
                    .ToList();
            }
            else if (CurrentStep == "service")
            {
                // Só permite avançar se SelectedProId for de fato um employee ativo.
                if (string.IsNullOrWhiteSpace(SelectedProId))
                {
                    ErrorMessage = "Selecione uma profissional antes de escolher o serviço.";
                    return RedirectToPage("/Cliente/DashCliente", new { step = "pro" });
                }
                var proCheck = await _supabase.From<Profile>()
                    .Where(x => x.Id == SelectedProId)
                    .Where(x => x.TenantId == tenantId)
                    .Single();
                if (proCheck is null || !IsBookableProfessional(proCheck.Role))
                {
                    ErrorMessage = "Profissional inválida.";
                    return RedirectToPage("/Cliente/DashCliente", new { step = "pro" });
                }

                var srvs = await _supabase.From<Service>()
                    .Where(x => x.IsActive == true)
                    .Where(x => x.TenantId == tenantId)
                    .Get();

                // Mostra o preço/duração EFETIVOS da profissional escolhida
                // (override em professional_services, se existir — ver
                // Fase 4 do roadmap), não o padrão do catálogo direto.
                var options = new List<ServiceOption>();
                foreach (var svc in srvs.Models)
                {
                    var (price, duration) = await _bookingService.GetEffectiveServiceValuesAsync(SelectedProId!, svc);
                    options.Add(new ServiceOption
                    {
                        Id = svc.Id,
                        Name = svc.Name,
                        Price = price,
                        DurationMinutes = duration
                    });
                }
                Services = options;
            }

            return Page();
        }

        public async Task<IActionResult> OnPostBookAsync()
        {
            if (!AuthorizationService.TryGetUserId(User, out var userId))
            {
                return Forbid();
            }
            if (!_currentTenant.IsResolved)
            {
                return Forbid();
            }
            var tenantId = _currentTenant.Id!;
            var userIdStr = userId.ToString();

            // 1) Rejeitar no passado.
            if (SelectedDateTime <= DateTime.Now)
            {
                ErrorMessage = "Não é possível agendar para uma data no passado.";
                return RedirectToPage("/Cliente/DashCliente", new { step = "home" });
            }

            // 2) Validar que SelectedProId é uma profissional (employee ou
            // admin — Fase 6 do roadmap) real deste tenant.
            if (string.IsNullOrWhiteSpace(SelectedProId))
            {
                ErrorMessage = "Profissional inválida.";
                return RedirectToPage("/Cliente/DashCliente", new { step = "pro" });
            }
            var proCheck = await _supabase.From<Profile>()
                .Where(x => x.Id == SelectedProId)
                .Where(x => x.TenantId == tenantId)
                .Single();
            if (proCheck is null || !IsBookableProfessional(proCheck.Role))
            {
                ErrorMessage = "Profissional inválida.";
                return RedirectToPage("/Cliente/DashCliente", new { step = "pro" });
            }

            // 3) Validar que SelectedSrvId é um serviço ativo deste tenant.
            if (string.IsNullOrWhiteSpace(SelectedSrvId))
            {
                ErrorMessage = "Serviço inválido.";
                return RedirectToPage("/Cliente/DashCliente", new { step = "service", proId = SelectedProId });
            }
            var srvCheck = await _supabase.From<Service>()
                .Where(x => x.Id == SelectedSrvId)
                .Where(x => x.TenantId == tenantId)
                .Single();
            if (srvCheck is null || !srvCheck.IsActive)
            {
                ErrorMessage = "Serviço inválido ou inativo.";
                return RedirectToPage("/Cliente/DashCliente", new { step = "service", proId = SelectedProId });
            }

            // Preço/duração EFETIVOS da profissional (override em
            // professional_services, se existir — Fase 4 do roadmap),
            // usados no cálculo de horário e na receita gravada.
            var (effectivePrice, effectiveDuration) = await _bookingService.GetEffectiveServiceValuesAsync(SelectedProId!, srvCheck);

            // 4) Horário comercial + almoço (configurável por tenant — Fase 2)
            // e conflito de agenda (Fase 5: regra compartilhada com o
            // agendamento manual da profissional — ver AppointmentBookingService).
            var validationError = await _bookingService.ValidateBookingAsync(SelectedProId!, SelectedDateTime, effectiveDuration);
            if (validationError is not null)
            {
                ErrorMessage = validationError;
                return RedirectToPage("/Cliente/DashCliente", new { step = "home" });
            }

            var newStart = SelectedDateTime.ToUniversalTime();
            var newEnd = SelectedDateTime.AddMinutes(effectiveDuration).ToUniversalTime();

            try
            {
                var newAppointment = new Appointment
                {
                    ClientId = userIdStr,
                    EmployeeId = SelectedProId!,
                    ServiceId = SelectedSrvId!,
                    StartTime = newStart,
                    EndTime = newEnd,
                    BookedForName = string.IsNullOrWhiteSpace(BookForName) ? null : BookForName.Trim(),
                    BookedForPhone = string.IsNullOrWhiteSpace(BookForPhone) ? null : BookForPhone.Trim(),
                    Status = "pending",
                    EstimatedRevenue = effectivePrice,
                    TenantId = tenantId
                };

                await _supabase.From<Appointment>().Insert(newAppointment);
                SuccessMessage = "Agendamento realizado com sucesso!";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao criar agendamento para cliente {ClientId}", userIdStr);
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
            if (!_currentTenant.IsResolved)
            {
                return Forbid();
            }
            var tenantId = _currentTenant.Id!;

            if (string.IsNullOrWhiteSpace(id))
            {
                ErrorMessage = "Agendamento inválido.";
                return RedirectToPage("/Cliente/DashCliente", new { step = "home" });
            }

            try
            {
                // BUG RAIZ DE SEGURANÇA: antes, qualquer cliente podia cancelar
                // qualquer agendamento passando o id. Agora: valida posse
                // (cliente e tenant).
                var existing = await _supabase.From<Appointment>()
                    .Where(x => x.Id == id)
                    .Where(x => x.TenantId == tenantId)
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

                // A trigger do banco (trigger_check_cancellation, migration
                // 0006) já trava cancelamentos com menos de 1h de
                // antecedência, levantando uma exceção com o marcador
                // "CANCELLATION_TOO_LATE" — tratado abaixo pra dar uma
                // mensagem específica em vez do erro genérico.
                await _supabase.From<Appointment>()
                    .Where(x => x.Id == id)
                    .Set(x => x.Status, "cancelled")
                    .Update();

                SuccessMessage = "Agendamento cancelado com sucesso.";
            }
            catch (Exception ex) when (ex.Message.Contains("CANCELLATION_TOO_LATE") ||
                                        (ex.InnerException?.Message.Contains("CANCELLATION_TOO_LATE") ?? false))
            {
                ErrorMessage = "Esse agendamento não pode mais ser cancelado — faltam menos de 1h para o horário marcado.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao cancelar agendamento {Id}", id);
                ErrorMessage = "Não foi possível cancelar. Tente novamente.";
            }
            return RedirectToPage("/Cliente/DashCliente", new { step = "home" });
        }

        /// <summary>
        /// Resolve nome real do serviço e da profissional pra uma lista de
        /// agendamentos — extraído pra evitar repetir a mesma busca em
        /// memória (volume baixo: agendamentos de um único cliente) nos
        /// steps "home" e "historico".
        /// </summary>
        private async Task<(Dictionary<string, Service> Services, Dictionary<string, Profile> Employees)> ResolveNamesAsync(IEnumerable<Appointment> appointments)
        {
            var servicesById = new Dictionary<string, Service>();
            foreach (var sid in appointments.Select(a => a.ServiceId).Distinct())
            {
                var svc = await _supabase.From<Service>().Where(x => x.Id == sid).Single();
                if (svc != null) servicesById[sid] = svc;
            }

            var employeesById = new Dictionary<string, Profile>();
            foreach (var eid in appointments.Select(a => a.EmployeeId).Distinct())
            {
                var emp = await _supabase.From<Profile>().Where(x => x.Id == eid).Single();
                if (emp != null) employeesById[eid] = emp;
            }

            return (servicesById, employeesById);
        }

        /// <summary>
        /// Roles que podem ser escolhidas como profissional num
        /// agendamento — admin também atende clientes (Fase 6 do roadmap,
        /// "profissional premium"), além de employee.
        /// </summary>
        private static bool IsBookableProfessional(string? role) => role is "employee" or "admin";

        public class ServiceOption
        {
            public string Id { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public decimal Price { get; set; }
            public int DurationMinutes { get; set; }
        }
    }
}
