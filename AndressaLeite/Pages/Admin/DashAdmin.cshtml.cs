using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;
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
        private readonly ILogger<DashAdminModel> _logger;
        private readonly CurrentTenant _currentTenant;
        private readonly IMemoryCache _cache;
        private readonly IEmailService _emailService;
        private readonly IConfiguration _configuration;

        public DashAdminModel(Supabase.Client supabase, ILogger<DashAdminModel> logger, CurrentTenant currentTenant, IMemoryCache cache, IEmailService emailService, IConfiguration configuration)
        {
            _supabase = supabase;
            _logger = logger;
            _currentTenant = currentTenant;
            _cache = cache;
            _emailService = emailService;
            _configuration = configuration;
        }

        public decimal EstimatedRevenue { get; set; } = 0.00m;
        public decimal ActualRevenue { get; set; } = 0.00m;
        public List<Profile> ActiveEmployees { get; set; } = new();
        public List<Service> AllServices { get; set; } = new();

        /// <summary>Convites de equipe ainda não aceitos/cancelados/expirados (readme.txt 5.6).</summary>
        public List<TeamInvite> PendingInvites { get; set; } = new();

        /// <summary>Status de billing do salão (readme.txt 4.9/9.2) — null se ainda não tem linha em tenant_subscriptions.</summary>
        public string? SubscriptionStatus { get; set; }

        // Métricas do dia/mês (Fase 7 do roadmap, readme.txt).
        public int CompletedToday { get; set; }
        public decimal EstimatedRevenueToday { get; set; }
        public List<PaymentMethodSummary> PaymentMethodBreakdown { get; set; } = new();
        public List<RecentNoteView> RecentNotes { get; set; } = new();

        // Valores atuais pra pré-preencher o form de "Horário de
        // Funcionamento" — formato "HH:mm" pra bater com <input type="time">.
        public string CurrentBusinessOpenTime => _currentTenant.BusinessOpenTime.ToString(@"hh\:mm");
        public string CurrentBusinessCloseTime => _currentTenant.BusinessCloseTime.ToString(@"hh\:mm");
        public string? CurrentLunchStartTime => _currentTenant.LunchStartTime?.ToString(@"hh\:mm");
        public string? CurrentLunchEndTime => _currentTenant.LunchEndTime?.ToString(@"hh\:mm");

        [TempData] public string? SuccessMessage { get; set; }
        [TempData] public string? ErrorMessage { get; set; }

        public async Task<IActionResult> OnGetAsync()
        {
            if (AuthorizationService.GetRole(User) != "admin")
            {
                return Forbid();
            }
            if (!_currentTenant.IsResolved)
            {
                return Forbid();
            }
            var tenantId = _currentTenant.Id!;

            var employeesResponse = await _supabase.From<Profile>()
                .Where(x => x.Role == "employee")
                .Where(x => x.TenantId == tenantId)
                .Get();
            ActiveEmployees = employeesResponse.Models;

            var subscription = await _supabase.From<TenantSubscription>()
                .Where(x => x.TenantId == tenantId)
                .Single();
            SubscriptionStatus = subscription?.Status;

            // Antes buscava TODOS os agendamentos de TODOS os tenants sem
            // nenhum filtro — era o vazamento cross-tenant mais crítico do
            // app (indicadores financeiros de um salão vazando pra outro).
            var appointmentsResponse = await _supabase.From<Appointment>()
                .Where(x => x.TenantId == tenantId)
                .Get();
            var list = appointmentsResponse.Models;

            EstimatedRevenue = list.Where(x => x.Status != "cancelled").Sum(x => x.EstimatedRevenue);
            ActualRevenue = list.Where(x => x.Status == "completed").Sum(x => x.ActualRevenue);

            var servicesResponse = await _supabase.From<Service>()
                .Where(x => x.TenantId == tenantId)
                .Get();
            AllServices = servicesResponse.Models.OrderBy(s => s.Name).ToList();

            // Convites pendentes (readme.txt 5.6) — filtra usado/cancelado
            // em memória (mesmo padrão de OR via múltiplas condições já
            // usado no resto do projeto) e expiração comparando com agora.
            var invitesResponse = await _supabase.From<TeamInvite>()
                .Where(x => x.TenantId == tenantId)
                .Get();
            var now = DateTime.UtcNow;
            PendingInvites = invitesResponse.Models
                .Where(i => i.UsedAt is null && i.CancelledAt is null && i.ExpiresAt > now)
                .OrderByDescending(i => i.CreatedAt)
                .ToList();

            // Métricas do dia/mês — reaproveita a mesma lista "list" já
            // buscada acima (todos os agendamentos do tenant), sem query
            // extra. StartTime é gravado em UTC, mas "hoje"/"este mês"
            // precisam ser o dia/mês LOCAL do salão — DateTime.Now já é
            // Kind=Local, então ToUniversalTime() converte certo usando o
            // fuso do servidor (mesma premissa do booking; comparar direto
            // com DateTime.UtcNow.Date erra a virada do dia perto da
            // meia-noite local).
            var todayUtc = DateTime.Now.Date.ToUniversalTime();
            var tomorrowUtc = DateTime.Now.Date.AddDays(1).ToUniversalTime();

            CompletedToday = list.Count(x =>
                x.Status == "completed" && x.StartTime >= todayUtc && x.StartTime < tomorrowUtc);

            EstimatedRevenueToday = list
                .Where(x => x.Status != "cancelled" && x.StartTime >= todayUtc && x.StartTime < tomorrowUtc)
                .Sum(x => x.EstimatedRevenue);

            var monthStartLocal = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1, 0, 0, 0, DateTimeKind.Local);
            var monthStartUtc = monthStartLocal.ToUniversalTime();
            PaymentMethodBreakdown = list
                .Where(x => x.Status == "completed" && x.StartTime >= monthStartUtc && !string.IsNullOrEmpty(x.PaymentMethod))
                .GroupBy(x => x.PaymentMethod!)
                .Select(g => new PaymentMethodSummary
                {
                    Method = g.Key,
                    Total = g.Sum(a => a.ActualRevenue),
                    Count = g.Count()
                })
                .OrderByDescending(x => x.Total)
                .ToList();

            // Observações recentes: últimos atendimentos concluídos com
            // Notes preenchido, com o contexto (serviço/cliente) resolvido
            // — igual ao padrão já usado em DashCliente/DashProfissional.
            var recentCompleted = list
                .Where(x => x.Status == "completed" && !string.IsNullOrWhiteSpace(x.Notes))
                .OrderByDescending(x => x.StartTime)
                .Take(10)
                .ToList();

            var noteServicesById = new Dictionary<string, Service>();
            foreach (var sid in recentCompleted.Select(a => a.ServiceId).Distinct())
            {
                var svc = await _supabase.From<Service>().Where(x => x.Id == sid).Single();
                if (svc != null) noteServicesById[sid] = svc;
            }

            var noteClientsById = new Dictionary<string, Profile>();
            foreach (var cid in recentCompleted.Select(a => a.ClientId).Where(id => id != null).Distinct())
            {
                var client = await _supabase.From<Profile>().Where(x => x.Id == cid).Single();
                if (client != null) noteClientsById[cid!] = client;
            }

            RecentNotes = recentCompleted.Select(a => new RecentNoteView
            {
                StartTime = a.StartTime,
                ServiceName = noteServicesById.TryGetValue(a.ServiceId, out var svc) ? svc.Name : "Atendimento",
                ClientName = a.BookedForName
                    ?? (a.ClientId != null && noteClientsById.TryGetValue(a.ClientId, out var cl) ? cl.FullName : "Cliente"),
                Notes = a.Notes!,
                PaymentMethod = a.PaymentMethod
            }).ToList();

            return Page();
        }

        /// <summary>
        /// Rótulo amigável pra forma de pagamento (mesmos valores do check
        /// constraint de appointments.payment_method — migration 0003).
        /// </summary>
        public static string FormatPaymentMethod(string? method) => method switch
        {
            "dinheiro" => "Dinheiro",
            "pix" => "Pix",
            "cartao_debito" => "Cartão de Débito",
            "cartao_credito" => "Cartão de Crédito",
            "outro" => "Outro",
            _ => "—"
        };

        public class PaymentMethodSummary
        {
            public string Method { get; set; } = string.Empty;
            public decimal Total { get; set; }
            public int Count { get; set; }
        }

        public class RecentNoteView
        {
            public DateTime StartTime { get; set; }
            public string ServiceName { get; set; } = string.Empty;
            public string ClientName { get; set; } = string.Empty;
            public string Notes { get; set; } = string.Empty;
            public string? PaymentMethod { get; set; }
        }

        /// <summary>
        /// POST: Atualiza o horário de funcionamento do salão (abertura,
        /// fechamento e, opcionalmente, o intervalo de almoço). Usado pelo
        /// fluxo de agendamento do cliente e pelo agendamento manual da
        /// profissional pra validar horário — ver DashCliente.OnPostBookAsync.
        /// </summary>
        public async Task<IActionResult> OnPostUpdateBusinessHoursAsync(
            [FromForm] string BusinessOpenTime,
            [FromForm] string BusinessCloseTime,
            [FromForm] string? LunchStartTime,
            [FromForm] string? LunchEndTime)
        {
            if (AuthorizationService.GetRole(User) != "admin")
            {
                return Forbid();
            }
            if (!_currentTenant.IsResolved)
            {
                return Forbid();
            }

            if (!TimeSpan.TryParse(BusinessOpenTime, out var openTs) ||
                !TimeSpan.TryParse(BusinessCloseTime, out var closeTs))
            {
                ErrorMessage = "Horário de abertura/fechamento inválido.";
                return RedirectToPage();
            }
            if (openTs >= closeTs)
            {
                ErrorMessage = "O horário de abertura precisa ser antes do fechamento.";
                return RedirectToPage();
            }

            bool hasLunchStart = !string.IsNullOrWhiteSpace(LunchStartTime);
            bool hasLunchEnd = !string.IsNullOrWhiteSpace(LunchEndTime);
            if (hasLunchStart != hasLunchEnd)
            {
                ErrorMessage = "Informe início e fim do almoço juntos, ou deixe os dois em branco.";
                return RedirectToPage();
            }

            TimeSpan? lunchStartTs = null;
            TimeSpan? lunchEndTs = null;
            if (hasLunchStart && hasLunchEnd)
            {
                if (!TimeSpan.TryParse(LunchStartTime, out var ls) || !TimeSpan.TryParse(LunchEndTime, out var le))
                {
                    ErrorMessage = "Horário de almoço inválido.";
                    return RedirectToPage();
                }
                if (ls >= le)
                {
                    ErrorMessage = "O início do almoço precisa ser antes do fim.";
                    return RedirectToPage();
                }
                if (ls < openTs || le > closeTs)
                {
                    ErrorMessage = "O horário de almoço precisa estar dentro do expediente.";
                    return RedirectToPage();
                }
                lunchStartTs = ls;
                lunchEndTs = le;
            }

            try
            {
                // .Set(x => x.Campo, null) quebra no postgrest-csharp 3.5.1
                // quando LunchStartTime/LunchEndTime ficam null (salão sem
                // intervalo de almoço — achado da rodada de e-mail
                // transacional, readme.txt 12.2.b). Busca o tenant
                // primeiro e faz Update() do objeto completo, em vez de
                // criar um Tenant novo em memória só com esses 4 campos —
                // um Update() de objeto parcial mandaria Slug/Name vazios
                // e apagaria esses dados de verdade.
                var tenant = await _supabase.From<Tenant>()
                    .Where(x => x.Id == _currentTenant.Id)
                    .Single();
                if (tenant is null)
                {
                    ErrorMessage = "Salão não encontrado.";
                    return RedirectToPage();
                }
                tenant.BusinessOpenTime = openTs.ToString(@"hh\:mm\:ss");
                tenant.BusinessCloseTime = closeTs.ToString(@"hh\:mm\:ss");
                tenant.LunchStartTime = lunchStartTs?.ToString(@"hh\:mm\:ss");
                tenant.LunchEndTime = lunchEndTs?.ToString(@"hh\:mm\:ss");
                await _supabase.From<Tenant>().Update(tenant);

                // Sem isso, a mudança só valeria depois do TTL do cache do
                // TenantResolutionMiddleware expirar sozinho (até 60s).
                _cache.Remove(TenantResolutionMiddleware.CacheKey(_currentTenant.Slug!));

                SuccessMessage = "Horário de funcionamento atualizado.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao atualizar horário de funcionamento do tenant {TenantId}", _currentTenant.Id);
                ErrorMessage = "Não foi possível atualizar o horário de funcionamento.";
            }

            return RedirectToPage();
        }

        /// <summary>
        /// POST: Cadastra um novo serviço oferecido pelo salão. Sem isso, a
        /// etapa "Escolha o Serviço" do agendamento do cliente fica sem
        /// nenhuma opção, a menos que alguém insira linhas manualmente
        /// direto no banco.
        /// </summary>
        public async Task<IActionResult> OnPostAddServiceAsync(
            [FromForm] string SrvName,
            [FromForm] decimal SrvPrice,
            [FromForm] int SrvDurationMinutes)
        {
            if (AuthorizationService.GetRole(User) != "admin")
            {
                return Forbid();
            }
            if (!_currentTenant.IsResolved)
            {
                return Forbid();
            }

            if (string.IsNullOrWhiteSpace(SrvName) || SrvName.Trim().Length < 2)
            {
                ErrorMessage = "Nome do serviço é obrigatório e deve ter pelo menos 2 caracteres.";
                return RedirectToPage();
            }

            if (SrvPrice <= 0)
            {
                ErrorMessage = "Preço do serviço deve ser maior que zero.";
                return RedirectToPage();
            }

            if (SrvDurationMinutes <= 0)
            {
                ErrorMessage = "Duração do serviço deve ser maior que zero minutos.";
                return RedirectToPage();
            }

            try
            {
                var serviceData = new Service
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = SrvName.Trim(),
                    Price = SrvPrice,
                    DurationMinutes = SrvDurationMinutes,
                    IsActive = true,
                    TenantId = _currentTenant.Id!
                };

                await _supabase.From<Service>().Insert(serviceData);

                SuccessMessage = $"✅ Serviço '{SrvName}' cadastrado com sucesso!";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao criar serviço {Name}", SrvName);
                ErrorMessage = "Não foi possível salvar o serviço. Verifique os dados e tente novamente.";
            }

            return RedirectToPage();
        }

        /// <summary>
        /// POST: Ativa/desativa um serviço. Serviços inativos somem da
        /// etapa de agendamento do cliente, mas continuam associados a
        /// agendamentos já existentes.
        /// </summary>
        public async Task<IActionResult> OnPostToggleServiceAsync(string id)
        {
            if (AuthorizationService.GetRole(User) != "admin")
            {
                return Forbid();
            }
            if (!_currentTenant.IsResolved)
            {
                return Forbid();
            }

            if (string.IsNullOrWhiteSpace(id))
            {
                ErrorMessage = "Serviço inválido.";
                return RedirectToPage();
            }

            try
            {
                // Filtra por tenant: um admin não pode mexer num serviço de
                // outro salão só sabendo o GUID.
                var service = await _supabase.From<Service>()
                    .Where(x => x.Id == id)
                    .Where(x => x.TenantId == _currentTenant.Id)
                    .Single();
                if (service is null)
                {
                    ErrorMessage = "Serviço não encontrado.";
                    return RedirectToPage();
                }

                await _supabase.From<Service>()
                    .Where(x => x.Id == id)
                    .Set(x => x.IsActive, !service.IsActive)
                    .Update();

                SuccessMessage = service.IsActive
                    ? "Serviço desativado."
                    : "Serviço reativado.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao alternar status do serviço {Id}", id);
                ErrorMessage = "Não foi possível atualizar o serviço.";
            }

            return RedirectToPage();
        }

        /// <summary>
        /// POST: Remove um serviço definitivamente da tabela.
        /// </summary>
        public async Task<IActionResult> OnPostRemoveServiceAsync(string id)
        {
            if (AuthorizationService.GetRole(User) != "admin")
            {
                return Forbid();
            }
            if (!_currentTenant.IsResolved)
            {
                return Forbid();
            }

            if (string.IsNullOrWhiteSpace(id))
            {
                ErrorMessage = "Serviço inválido.";
                return RedirectToPage();
            }

            try
            {
                await _supabase.From<Service>()
                    .Where(x => x.Id == id)
                    .Where(x => x.TenantId == _currentTenant.Id)
                    .Delete();
                SuccessMessage = "Serviço removido com sucesso.";
            }
            catch (PostgrestException pgex)
            {
                _logger.LogError(pgex, "Falha ao remover serviço {Id}", id);

                if (pgex.Message.Contains("23503"))
                {
                    ErrorMessage = "Não é possível remover um serviço com agendamentos associados. Desative-o em vez de remover.";
                }
                else
                {
                    ErrorMessage = "Não foi possível remover o serviço.";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha inesperada ao remover serviço {Id}", id);
                ErrorMessage = "Ocorreu um erro inesperado ao remover o serviço.";
            }

            return RedirectToPage();
        }

        /// <summary>
        /// POST: convida um novo profissional por e-mail (readme.txt 5.6)
        /// — substitui o antigo fluxo de criar a conta com senha direto.
        /// Admin informa nome/e-mail/telefone, a profissional aceita o
        /// convite e define a própria senha em Pages/Auth/AceitarConvite.cshtml.cs.
        /// </summary>
        public async Task<IActionResult> OnPostInviteEmployeeAsync(
            [FromForm] string EmpName,
            [FromForm] string EmpEmail,
            [FromForm] string EmpPhone)
        {
            if (AuthorizationService.GetRole(User) != "admin")
            {
                return Forbid();
            }
            if (!_currentTenant.IsResolved)
            {
                return Forbid();
            }

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

            var cleanPhone = System.Text.RegularExpressions.Regex.Replace(EmpPhone ?? "", @"[^\d]", "");
            if (!string.IsNullOrWhiteSpace(cleanPhone) &&
                !System.Text.RegularExpressions.Regex.IsMatch(cleanPhone, @"^\+?[1-9]\d{10,14}$"))
            {
                ErrorMessage = "Telefone inválido. Use DDI + DDD + número (ex: +5515988888888).";
                return RedirectToPage();
            }
            if (string.IsNullOrWhiteSpace(cleanPhone))
            {
                cleanPhone = "11999999999"; // Telefone padrão para testes
            }

            var cleanEmail = EmpEmail.Trim().ToLowerInvariant();
            var userId = AuthorizationService.TryGetUserId(User, out var adminId) ? adminId.ToString() : string.Empty;

            try
            {
                var (rawToken, tokenHash) = EmailTokenService.GenerateToken();
                var invite = new TeamInvite
                {
                    Id = Guid.NewGuid().ToString(),
                    TenantId = _currentTenant.Id!,
                    Email = cleanEmail,
                    FullName = EmpName.Trim(),
                    Phone = cleanPhone,
                    TokenHash = tokenHash,
                    ExpiresAt = DateTime.UtcNow.AddDays(7),
                    CreatedBy = userId
                };
                await _supabase.From<TeamInvite>().Insert(invite);

                var rootDomain = _configuration["Tenancy:RootDomain"] ?? "localhost";
                var scheme = Request.IsHttps ? "https" : "http";
                var port = Request.Host.Port.HasValue ? $":{Request.Host.Port}" : "";
                var acceptUrl = $"{scheme}://{_currentTenant.Slug}.{rootDomain}{port}/Auth/AceitarConvite?token={rawToken}";

                var html = $"<p>{System.Net.WebUtility.HtmlEncode(_currentTenant.Name)} te convidou pra fazer parte da equipe no MarcAi.</p>" +
                    $"<p><a href=\"{System.Net.WebUtility.HtmlEncode(acceptUrl)}\">Clique aqui pra aceitar o convite e definir sua senha</a> " +
                    $"— o link vale por 7 dias.</p>";
                await _emailService.SendEmailAsync(cleanEmail, $"Convite para {_currentTenant.Name} — MarcAi", html);

                SuccessMessage = $"✅ Convite enviado para {cleanEmail}.";
                return RedirectToPage();
            }
            catch (PostgrestException pgex)
            {
                _logger.LogError(pgex, "Falha ao criar convite para {Email}", cleanEmail);
                ErrorMessage = (pgex.Message.Contains("23505") || pgex.Message.Contains("duplicate"))
                    ? "Já existe uma conta ou convite pendente para este e-mail."
                    : "Não foi possível enviar o convite. Verifique os dados e tente novamente.";
                return RedirectToPage();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha inesperada ao criar convite para {Email}", cleanEmail);
                ErrorMessage = "Ocorreu um erro inesperado ao enviar o convite.";
                return RedirectToPage();
            }
        }

        /// <summary>
        /// POST: cancela um convite ainda não aceito — dá pro admin
        /// corrigir um convite mandado errado sem esperar os 7 dias de
        /// expiração natural.
        /// </summary>
        public async Task<IActionResult> OnPostCancelInviteAsync(string id)
        {
            if (AuthorizationService.GetRole(User) != "admin")
            {
                return Forbid();
            }
            if (!_currentTenant.IsResolved)
            {
                return Forbid();
            }

            try
            {
                await _supabase.From<TeamInvite>()
                    .Where(x => x.Id == id)
                    .Where(x => x.TenantId == _currentTenant.Id)
                    .Set(x => x.CancelledAt, DateTime.UtcNow)
                    .Update();
                SuccessMessage = "Convite cancelado.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao cancelar convite {Id}", id);
                ErrorMessage = "Não foi possível cancelar o convite.";
            }

            return RedirectToPage();
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
            if (!_currentTenant.IsResolved)
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
                // Delete o profissional da tabela (remove completamente),
                // restrito ao tenant atual (um admin não pode remover
                // profissional de outro salão só sabendo o GUID).
                await _supabase.From<Profile>()
                    .Where(x => x.Id == id)
                    .Where(x => x.TenantId == _currentTenant.Id)
                    .Delete();

                SuccessMessage = "Profissional removido da equipe com sucesso.";
            }
            catch (PostgrestException pgex)
            {
                _logger.LogError(pgex, "Falha ao remover employee {Id}", id);

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
                    ErrorMessage = "Não foi possível remover o profissional.";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha inesperada ao remover employee {Id}", id);
                ErrorMessage = "Ocorreu um erro inesperado ao remover o profissional.";
            }

            return RedirectToPage();
        }
    }
}
