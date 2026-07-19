using AndressaLeite.Models;

namespace AndressaLeite.Services
{
    /// <summary>
    /// Job de lembrete automático por e-mail (readme.txt 5.3) — primeira
    /// infraestrutura de job agendado do projeto (não existia nada do tipo
    /// antes). BackgroundService com PeriodicTimer, usando
    /// IServiceScopeFactory pra resolver o Supabase.Client/IEmailService
    /// (ambos scoped, não dá pra injetar direto num serviço singleton).
    ///
    /// LIMITAÇÃO ASSUMIDA POR ESCRITO: só funciona corretamente com uma
    /// única instância do container rodando — sem lock distribuído entre
    /// instâncias. Bate com o deploy atual (EC2 single-instance via
    /// docker-compose, readme.txt seção 9.3); se o projeto algum dia
    /// escalar pra múltiplas instâncias, isso precisa de um lock
    /// distribuído (ou mover pra um cron externo) antes de religar.
    /// </summary>
    public class AppointmentReminderService : BackgroundService
    {
        private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(15);

        // Janela fixa de ~24h antes do agendamento (23-25h), não uma faixa
        // variável — uma janela variável faria o horário de envio ficar
        // imprevisível por agendamento (mais cedo ou mais tarde dependendo
        // de quando ele "entrou" na janela).
        private static readonly TimeSpan WindowStart = TimeSpan.FromHours(23);
        private static readonly TimeSpan WindowEnd = TimeSpan.FromHours(25);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<AppointmentReminderService> _logger;

        public AppointmentReminderService(IServiceScopeFactory scopeFactory, ILogger<AppointmentReminderService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TickInterval);
            do
            {
                try
                {
                    await SendPendingRemindersAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    // Um tick ruim não pode derrubar o BackgroundService —
                    // ele precisa continuar tentando no próximo tick.
                    _logger.LogError(ex, "Falha inesperada num tick do AppointmentReminderService");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        private async Task SendPendingRemindersAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var supabase = scope.ServiceProvider.GetRequiredService<Supabase.Client>();
            var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

            var now = DateTime.UtcNow;
            var windowStart = now.Add(WindowStart);
            var windowEnd = now.Add(WindowEnd);

            // Duas queries separadas concatenadas em vez de "status in
            // (pending, confirmed)" num só Where — mesmo padrão já usado no
            // projeto pra contornar o bug de OR do driver postgrest-csharp
            // (ver DashProfissional.cshtml.cs, histórico completed+cancelled).
            var pendingResp = await supabase.From<Appointment>()
                .Where(x => x.Status == "pending")
                .Where(x => x.StartTime >= windowStart)
                .Where(x => x.StartTime < windowEnd)
                .Get();

            var confirmedResp = await supabase.From<Appointment>()
                .Where(x => x.Status == "confirmed")
                .Where(x => x.StartTime >= windowStart)
                .Where(x => x.StartTime < windowEnd)
                .Get();

            // Este job roda CROSS-TENANT de propósito — diferente de todo o
            // resto do código, não há (nem pode haver) um CurrentTenant
            // populado aqui (isso só existe por requisição HTTP, resolvido
            // pelo subdomínio). Client_id nulo = agendamento feito pra
            // alguém sem conta (BookedForName/Phone) — sem e-mail pra
            // mandar, continua só no lembrete manual de WhatsApp.
            var candidates = pendingResp.Models.Concat(confirmedResp.Models)
                .Where(a => !string.IsNullOrEmpty(a.ClientId) && a.ReminderSentAt is null)
                .ToList();

            if (candidates.Count == 0) return;

            var tenantsById = new Dictionary<string, Tenant>();
            foreach (var tid in candidates.Select(a => a.TenantId).Distinct())
            {
                var tenant = await supabase.From<Tenant>().Where(x => x.Id == tid).Single();
                if (tenant != null) tenantsById[tid] = tenant;
            }

            var clientsById = new Dictionary<string, Profile>();
            foreach (var cid in candidates.Select(a => a.ClientId!).Distinct())
            {
                var client = await supabase.From<Profile>().Where(x => x.Id == cid).Single();
                if (client != null) clientsById[cid] = client;
            }

            var servicesById = new Dictionary<string, Service>();
            foreach (var sid in candidates.Select(a => a.ServiceId).Distinct())
            {
                var svc = await supabase.From<Service>().Where(x => x.Id == sid).Single();
                if (svc != null) servicesById[sid] = svc;
            }

            foreach (var appt in candidates)
            {
                if (ct.IsCancellationRequested) break;

                if (!tenantsById.TryGetValue(appt.TenantId, out var tenant)) continue;
                if (!clientsById.TryGetValue(appt.ClientId!, out var client)) continue;
                if (string.IsNullOrWhiteSpace(client.Email)) continue;

                var serviceName = servicesById.TryGetValue(appt.ServiceId, out var svc) ? svc.Name : "Atendimento";

                try
                {
                    // Appointment.StartTime já normaliza Kind=Utc no
                    // próprio setter do modelo — não precisa corrigir aqui.
                    var localStart = appt.StartTime.ToLocalTime();
                    var subject = AppointmentReminderMessages.BuildReminderEmailSubject(tenant.Name);
                    var html = AppointmentReminderMessages.BuildReminderEmailHtml(client.FullName, serviceName, tenant.Name, localStart);

                    await emailService.SendEmailAsync(client.Email, subject, html);

                    // Grava logo após CADA envio bem-sucedido (não em lote
                    // no final do tick) — um erro no meio do lote não pode
                    // causar reenvio dos lembretes que já saíram.
                    await supabase.From<Appointment>()
                        .Where(x => x.Id == appt.Id)
                        .Set(x => x.ReminderSentAt, DateTime.UtcNow)
                        .Update();
                }
                catch (Exception ex)
                {
                    // Uma falha de envio não pode travar os demais lembretes
                    // do mesmo tick.
                    _logger.LogError(ex, "Falha ao enviar lembrete de agendamento {AppointmentId}", appt.Id);
                }
            }
        }
    }
}
