using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Text.RegularExpressions;
using AndressaLeite.Models;
using AndressaLeite.Services;

namespace AndressaLeite.Pages.Profissional
{
    // Admin também acessa (é "profissional premium" — Fase 6 do roadmap,
    // readme.txt), mas mantém o próprio DashAdmin como painel de gestão
    // separado.
    [Authorize(Policy = "EmployeeOrAdmin")]
    public class DashProfissionalModel : PageModel
    {
        private readonly Supabase.Client _supabase;
        private readonly ILogger<DashProfissionalModel> _logger;
        private readonly CurrentTenant _currentTenant;
        private readonly AppointmentBookingService _bookingService;

        public DashProfissionalModel(Supabase.Client supabase, ILogger<DashProfissionalModel> logger, CurrentTenant currentTenant, AppointmentBookingService bookingService)
        {
            _supabase = supabase;
            _logger = logger;
            _currentTenant = currentTenant;
            _bookingService = bookingService;
        }

        // dinheiro | pix | cartao_debito | cartao_credito | outro — mesmos
        // valores do check constraint de appointments.payment_method
        // (migration 0003).
        private static readonly HashSet<string> AllowedPaymentMethods = new()
        {
            "dinheiro", "pix", "cartao_debito", "cartao_credito", "outro"
        };

        private const int HistoryPageSize = 10;

        public string EmployeeName { get; set; } = "Profissional";
        public List<AppointmentView> AppointmentsToday { get; set; } = new();

        /// <summary>Últimos atendimentos concluídos/cancelados (item 5.4 do readme.txt) — só populado quando ShowHistory=true.</summary>
        public List<AppointmentView> HistoryAppointments { get; set; } = new();

        /// <summary>Alterna entre "agenda de hoje" e "histórico" na mesma página (?ShowHistory=true).</summary>
        [BindProperty(SupportsGet = true)]
        public bool ShowHistory { get; set; }

        /// <summary>Página do histórico (0-based) — paginação leve, item 5.1 do readme.txt.</summary>
        [BindProperty(SupportsGet = true)]
        public int HistoryPage { get; set; }
        public bool HasMoreHistory { get; set; }

        /// <summary>
        /// Catálogo de serviços do tenant (ativos e inativos) pro dropdown
        /// "Serviço prestado" na conclusão do atendimento — inclui
        /// inativos porque a profissional pode ter atendido algo que já
        /// saiu do catálogo de agendamento do cliente.
        /// </summary>
        public List<Service> TenantServices { get; set; } = new();

        /// <summary>
        /// Override de preço/duração desta profissional por serviço (chave
        /// = ServiceId), pra tela "Meus Serviços". Ver Models/ProfessionalService.cs.
        /// </summary>
        public Dictionary<string, ProfessionalService> MyServiceOverrides { get; set; } = new();

        [TempData] public string? SuccessMessage { get; set; }
        [TempData] public string? ErrorMessage { get; set; }

        public async Task<IActionResult> OnGetAsync()
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

            string userIdStr = userId.ToString();

            var profile = await _supabase.From<Profile>()
                .Where(x => x.Id == userIdStr)
                .Where(x => x.TenantId == tenantId)
                .Single();
            if (profile is not null) EmployeeName = profile.FullName;

            if (ShowHistory)
            {
                // Últimos concluídos/cancelados (item 5.4 do readme.txt) —
                // duas queries separadas concatenadas, mesmo padrão de OR
                // já usado no projeto pra contornar o bug do driver.
                var completedResponse = await _supabase.From<Appointment>()
                    .Where(x => x.EmployeeId == userIdStr)
                    .Where(x => x.Status == "completed")
                    .Where(x => x.TenantId == tenantId)
                    .Get();

                var cancelledResponse = await _supabase.From<Appointment>()
                    .Where(x => x.EmployeeId == userIdStr)
                    .Where(x => x.Status == "cancelled")
                    .Where(x => x.TenantId == tenantId)
                    .Get();

                var allPastAppointments = completedResponse.Models
                    .Concat(cancelledResponse.Models)
                    .OrderByDescending(a => a.StartTime)
                    .ToList();

                // Paginação simples em memória (item 5.1 do readme.txt) —
                // aceitável no volume de atendimentos de uma única
                // profissional.
                var pastAppointments = allPastAppointments
                    .Skip(HistoryPage * HistoryPageSize)
                    .Take(HistoryPageSize)
                    .ToList();
                HasMoreHistory = allPastAppointments.Count > (HistoryPage + 1) * HistoryPageSize;

                HistoryAppointments = await BuildAppointmentViewsAsync(pastAppointments);
            }
            else
            {
                // Lista appointments do dia para esta profissional. StartTime é
                // gravado em UTC (ver DashCliente/AppointmentBookingService),
                // mas "hoje" precisa ser o dia local do salão, não o dia UTC —
                // DateTime.Now já é Kind=Local, então ToUniversalTime() converte
                // certo usando o fuso do servidor (mesma premissa já usada no
                // booking: sem isso, atendimentos perto da meia-noite local
                // podiam cair no dia UTC errado).
                var startOfDay = DateTime.Now.Date.ToUniversalTime();
                var endOfDay = DateTime.Now.Date.AddDays(1).ToUniversalTime();

                // 🔴 CORRIGIDO: Separado em vários .Where() para evitar o bug de sintaxe (PGRST100) do driver do Supabase
                var response = await _supabase.From<Appointment>()
                    .Where(x => x.EmployeeId == userIdStr)
                    .Where(x => x.StartTime >= startOfDay)
                    .Where(x => x.StartTime < endOfDay)
                    .Where(x => x.Status != "cancelled")
                    .Where(x => x.TenantId == tenantId)
                    .Get();

                AppointmentsToday = (await BuildAppointmentViewsAsync(response.Models))
                    .OrderBy(a => a.StartTime)
                    .ToList();
            }

            var tenantServicesResponse = await _supabase.From<Service>()
                .Where(x => x.TenantId == tenantId)
                .Get();
            TenantServices = tenantServicesResponse.Models.OrderBy(s => s.Name).ToList();

            var overridesResponse = await _supabase.From<ProfessionalService>()
                .Where(x => x.EmployeeId == userIdStr)
                .Where(x => x.TenantId == tenantId)
                .Get();
            MyServiceOverrides = overridesResponse.Models.ToDictionary(x => x.ServiceId);

            return Page();
        }

        /// <summary>
        /// Resolve nome/telefone real do serviço e da cliente pra uma lista
        /// de agendamentos e monta o link de WhatsApp — extraído pra
        /// reaproveitar entre a agenda de hoje e o histórico (item 5.4).
        /// </summary>
        private async Task<List<AppointmentView>> BuildAppointmentViewsAsync(List<Appointment> appointments)
        {
            var servicesById = new Dictionary<string, Service>();
            foreach (var sid in appointments.Select(a => a.ServiceId).Distinct())
            {
                var svc = await _supabase.From<Service>().Where(x => x.Id == sid).Single();
                if (svc != null) servicesById[sid] = svc;
            }

            // ClientId pode ser nulo (agendamento manual sem conta de
            // cliente vinculada — ver Fase 5 do roadmap) — filtra antes de
            // buscar os perfis.
            var clientsById = new Dictionary<string, Profile>();
            foreach (var cid in appointments.Select(a => a.ClientId).Where(id => id != null).Distinct())
            {
                var client = await _supabase.From<Profile>().Where(x => x.Id == cid).Single();
                if (client != null) clientsById[cid!] = client;
            }

            return appointments.Select(a =>
            {
                var clientName = a.BookedForName
                    ?? (a.ClientId != null && clientsById.TryGetValue(a.ClientId, out var client) ? client.FullName : "Cliente");
                var phone = a.BookedForPhone
                    ?? (a.ClientId != null && clientsById.TryGetValue(a.ClientId, out var clientForPhone) ? clientForPhone.Phone : null);
                var serviceName = servicesById.TryGetValue(a.ServiceId, out var svc) ? svc.Name : "Atendimento";

                return new AppointmentView
                {
                    Id = a.Id,
                    StartTime = a.StartTime,
                    ClientName = clientName,
                    ServiceId = a.ServiceId,
                    ServiceName = serviceName,
                    BookedForName = a.BookedForName,
                    Status = a.Status,
                    SuggestedRevenue = a.EstimatedRevenue,
                    WhatsAppLink = BuildWhatsAppReminderLink(phone, clientName, serviceName, a.StartTime)
                };
            }).ToList();
        }

        /// <summary>
        /// POST: agendamento manual feito pela própria profissional (encaixe,
        /// ligação, walk-in) — sem conta de cliente vinculada (ClientId
        /// nulo), usando BookedForName/BookedForPhone. Reaproveita a mesma
        /// validação de horário comercial/almoço/conflito de agenda do
        /// fluxo do cliente via AppointmentBookingService (Fase 5 do roadmap).
        /// </summary>
        public async Task<IActionResult> OnPostBookManualAsync(
            [FromForm] string ServiceId,
            [FromForm] DateTime SelectedDateTime,
            [FromForm] string BookForName,
            [FromForm] string BookForPhone)
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
            var employeeId = userId.ToString();

            if (SelectedDateTime <= DateTime.Now)
            {
                ErrorMessage = "Não é possível agendar para uma data no passado.";
                return RedirectToPage();
            }

            if (string.IsNullOrWhiteSpace(BookForName) || BookForName.Trim().Length < 2)
            {
                ErrorMessage = "Informe o nome da cliente.";
                return RedirectToPage();
            }

            var cleanPhone = Regex.Replace(BookForPhone ?? "", @"[^\d]", "");
            if (!Regex.IsMatch(cleanPhone, @"^\+?[1-9]\d{10,14}$"))
            {
                ErrorMessage = "Telefone inválido. Use DDI + DDD + número (somente dígitos).";
                return RedirectToPage();
            }

            var service = await _supabase.From<Service>()
                .Where(x => x.Id == ServiceId)
                .Where(x => x.TenantId == tenantId)
                .Single();
            if (service is null || !service.IsActive)
            {
                ErrorMessage = "Serviço inválido ou inativo.";
                return RedirectToPage();
            }

            var (effectivePrice, effectiveDuration) = await _bookingService.GetEffectiveServiceValuesAsync(employeeId, service);

            var validationError = await _bookingService.ValidateBookingAsync(employeeId, SelectedDateTime, effectiveDuration);
            if (validationError is not null)
            {
                ErrorMessage = validationError;
                return RedirectToPage();
            }

            try
            {
                var newAppointment = new Appointment
                {
                    // Sem isso, o Id fica string.Empty (default do modelo) e o
                    // Postgrest.Insert manda "id": "" no payload — o Postgres
                    // rejeita ("invalid input syntax for type uuid") em vez de
                    // usar o default uuid_generate_v4() da coluna (achado de QA).
                    Id = Guid.NewGuid().ToString(),
                    ClientId = null,
                    EmployeeId = employeeId,
                    ServiceId = service.Id,
                    StartTime = SelectedDateTime.ToUniversalTime(),
                    EndTime = SelectedDateTime.AddMinutes(effectiveDuration).ToUniversalTime(),
                    BookedForName = BookForName.Trim(),
                    BookedForPhone = cleanPhone,
                    // Diferente do cliente (que começa "pending" até a
                    // profissional confirmar), um agendamento que a própria
                    // profissional cria já nasce confirmado — ela mesma é
                    // quem está garantindo o horário.
                    Status = "confirmed",
                    EstimatedRevenue = effectivePrice,
                    TenantId = tenantId
                };

                await _supabase.From<Appointment>().Insert(newAppointment);
                SuccessMessage = "Agendamento criado com sucesso.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao criar agendamento manual para profissional {EmployeeId}", employeeId);
                ErrorMessage = "Não foi possível criar o agendamento. Tente novamente.";
            }

            return RedirectToPage();
        }

        /// <summary>
        /// POST: Cria ou atualiza o preço/duração personalizados desta
        /// profissional pra um serviço do catálogo (upsert manual — busca
        /// se já existe, atualiza; senão insere). Campos vazios = sem
        /// override, usa o padrão do catálogo (ver Models/ProfessionalService.cs).
        /// </summary>
        public async Task<IActionResult> OnPostUpdateMyServiceAsync(
            [FromForm] string ServiceId,
            [FromForm] decimal? Price,
            [FromForm] int? DurationMinutes)
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

            if (Price is < 0)
            {
                ErrorMessage = "Preço inválido.";
                return RedirectToPage();
            }
            if (DurationMinutes is <= 0)
            {
                ErrorMessage = "Duração inválida.";
                return RedirectToPage();
            }

            // Revalida que o serviço pertence a este tenant.
            var service = await _supabase.From<Service>()
                .Where(x => x.Id == ServiceId)
                .Where(x => x.TenantId == tenantId)
                .Single();
            if (service is null)
            {
                ErrorMessage = "Serviço inválido.";
                return RedirectToPage();
            }

            try
            {
                var existing = await _supabase.From<ProfessionalService>()
                    .Where(x => x.EmployeeId == userIdStr)
                    .Where(x => x.ServiceId == ServiceId)
                    .Where(x => x.TenantId == tenantId)
                    .Single();

                if (existing is null)
                {
                    var newOverride = new ProfessionalService
                    {
                        Id = Guid.NewGuid().ToString(),
                        TenantId = tenantId,
                        EmployeeId = userIdStr,
                        ServiceId = ServiceId,
                        Price = Price,
                        DurationMinutes = DurationMinutes,
                        IsActive = true
                    };
                    await _supabase.From<ProfessionalService>().Insert(newOverride);
                }
                else
                {
                    // .Set(x => x.Campo, null) quebra no postgrest-csharp
                    // 3.5.1 quando Price/DurationMinutes voltam pro padrão
                    // do catálogo (campo em branco = null) — achado da
                    // rodada de e-mail transacional, readme.txt 12.2.b.
                    // "existing" já veio completo do fetch acima, então
                    // Update() do objeto inteiro não perde nenhum outro
                    // campo.
                    existing.Price = Price;
                    existing.DurationMinutes = DurationMinutes;
                    await _supabase.From<ProfessionalService>().Update(existing);
                }

                SuccessMessage = $"Preferências salvas para \"{service.Name}\".";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao salvar override de serviço {ServiceId} para {EmployeeId}", ServiceId, userIdStr);
                ErrorMessage = "Não foi possível salvar suas preferências para este serviço.";
            }

            return RedirectToPage();
        }

        /// <summary>
        /// POST: Confirma um atendimento (pending -> confirmed). Só a
        /// profissional dona do atendimento pode confirmar.
        /// </summary>
        public async Task<IActionResult> OnPostConfirmAsync(string id)
        {
            if (!AuthorizationService.TryGetUserId(User, out var userId))
            {
                return Forbid();
            }
            if (!_currentTenant.IsResolved)
            {
                return Forbid();
            }

            var appointment = await TryGetOwnedAppointmentAsync(id, userId.ToString());
            if (appointment is null) return Forbid();

            try
            {
                await _supabase.From<Appointment>()
                    .Where(x => x.Id == id)
                    .Set(x => x.Status, "confirmed")
                    .Update();
                SuccessMessage = "Atendimento confirmado.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao confirmar atendimento {Id}", id);
                ErrorMessage = "Não foi possível confirmar o atendimento.";
            }

            return RedirectToPage();
        }

        /// <summary>
        /// POST: Conclui um atendimento (-> completed), registrando o que
        /// de fato foi feito: serviço prestado (pode diferir do agendado),
        /// valor cobrado (pode diferir do preço de tabela — desconto,
        /// etc.), forma de pagamento e observações. Sem isso, o indicador
        /// "Faturamento Real" do Admin nunca saía de R$0, já que nenhum
        /// agendamento chegava a "completed".
        /// </summary>
        public async Task<IActionResult> OnPostCompleteAsync(
            string id,
            [FromForm] string ServiceId,
            [FromForm] decimal ActualRevenue,
            [FromForm] string PaymentMethod,
            [FromForm] string? Notes)
        {
            if (!AuthorizationService.TryGetUserId(User, out var userId))
            {
                return Forbid();
            }
            if (!_currentTenant.IsResolved)
            {
                return Forbid();
            }

            var appointment = await TryGetOwnedAppointmentAsync(id, userId.ToString());
            if (appointment is null) return Forbid();

            if (ActualRevenue < 0)
            {
                ErrorMessage = "Valor cobrado inválido.";
                return RedirectToPage();
            }

            if (!AllowedPaymentMethods.Contains(PaymentMethod))
            {
                ErrorMessage = "Forma de pagamento inválida.";
                return RedirectToPage();
            }

            // Revalida o serviço no servidor em vez de confiar no que veio
            // do form — precisa pertencer ao mesmo tenant.
            var service = await _supabase.From<Service>()
                .Where(x => x.Id == ServiceId)
                .Where(x => x.TenantId == _currentTenant.Id)
                .Single();
            if (service is null)
            {
                ErrorMessage = "Serviço inválido.";
                return RedirectToPage();
            }

            var trimmedNotes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim();
            if (trimmedNotes is { Length: > 500 })
            {
                trimmedNotes = trimmedNotes[..500];
            }

            try
            {
                await _supabase.From<Appointment>()
                    .Where(x => x.Id == id)
                    .Set(x => x.Status, "completed")
                    .Set(x => x.ServiceId, service.Id)
                    .Set(x => x.ActualRevenue, ActualRevenue)
                    .Set(x => x.PaymentMethod, PaymentMethod)
                    .Set(x => x.Notes, trimmedNotes)
                    .Update();
                SuccessMessage = "Atendimento concluído.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao concluir atendimento {Id}", id);
                ErrorMessage = "Não foi possível concluir o atendimento.";
            }

            return RedirectToPage();
        }

        /// <summary>
        /// Busca o agendamento garantindo que pertence à profissional logada
        /// (evita que uma profissional altere status de agendamento alheio).
        /// </summary>
        private async Task<Appointment?> TryGetOwnedAppointmentAsync(string id, string employeeId)
        {
            if (string.IsNullOrWhiteSpace(id) || !_currentTenant.IsResolved) return null;

            var appointment = await _supabase.From<Appointment>()
                .Where(x => x.Id == id)
                .Where(x => x.TenantId == _currentTenant.Id)
                .Single();
            if (appointment is null || appointment.EmployeeId != employeeId) return null;

            return appointment;
        }

        /// <summary>
        /// Monta o link "https://wa.me/{telefone}?text=..." de lembrete
        /// (Fase 8 do roadmap, readme.txt) — deliberadamente simples, sem
        /// integração com WhatsApp Business API: é só um link que abre o
        /// WhatsApp Web/app com a mensagem pré-preenchida, a profissional
        /// confirma o envio manualmente. Retorna null se não há telefone
        /// utilizável (sem número pra mandar, sem lembrete).
        /// </summary>
        private string? BuildWhatsAppReminderLink(string? phone, string clientName, string serviceName, DateTime startTimeUtc)
        {
            if (string.IsNullOrWhiteSpace(phone)) return null;

            // wa.me espera só dígitos (DDI + DDD + número, sem símbolos).
            var digitsOnly = Regex.Replace(phone, @"[^\d]", "");
            if (digitsOnly.Length < 10) return null;

            var localStart = startTimeUtc.ToLocalTime();
            var salonName = _currentTenant.Name ?? "o salão";
            // Texto compartilhado com o lembrete automático por e-mail —
            // ver Services/AppointmentReminderMessages.cs.
            var message = AppointmentReminderMessages.BuildReminderText(clientName, serviceName, salonName, localStart);

            return $"https://wa.me/{digitsOnly}?text={Uri.EscapeDataString(message)}";
        }

        public class AppointmentView
        {
            public string Id { get; set; } = string.Empty;
            public DateTime StartTime { get; set; }
            public string ClientName { get; set; } = string.Empty;
            public string ServiceId { get; set; } = string.Empty;
            public string ServiceName { get; set; } = string.Empty;
            public string? BookedForName { get; set; }
            public string Status { get; set; } = "pending";
            public decimal SuggestedRevenue { get; set; }
            public string? WhatsAppLink { get; set; }
        }
    }
}
