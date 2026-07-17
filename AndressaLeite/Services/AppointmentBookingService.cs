using AndressaLeite.Models;

namespace AndressaLeite.Services
{
    /// <summary>
    /// Regras de negócio de agendamento compartilhadas entre o fluxo do
    /// cliente (Pages/Cliente/DashCliente.cshtml.cs) e o agendamento
    /// manual da profissional (Pages/Profissional/DashProfissional.cshtml.cs)
    /// — antes vivia só no primeiro; extraído na Fase 5 do roadmap
    /// (readme.txt) pra não duplicar (e um dia dessincronizar) a mesma
    /// regra em dois lugares.
    /// </summary>
    public class AppointmentBookingService
    {
        private readonly Supabase.Client _supabase;
        private readonly CurrentTenant _currentTenant;

        public AppointmentBookingService(Supabase.Client supabase, CurrentTenant currentTenant)
        {
            _supabase = supabase;
            _currentTenant = currentTenant;
        }

        /// <summary>
        /// Preço/duração efetivos de um serviço para uma profissional
        /// específica: o override dela em professional_services (campo a
        /// campo — pode ter só preço ou só duração personalizados), senão
        /// o padrão do catálogo (Fase 4 do roadmap).
        /// </summary>
        public async Task<(decimal Price, int DurationMinutes)> GetEffectiveServiceValuesAsync(string employeeId, Service baseService)
        {
            var overrideRow = await _supabase.From<ProfessionalService>()
                .Where(x => x.EmployeeId == employeeId)
                .Where(x => x.ServiceId == baseService.Id)
                .Where(x => x.TenantId == _currentTenant.Id)
                .Single();

            var price = overrideRow?.Price ?? baseService.Price;
            var duration = overrideRow?.DurationMinutes ?? baseService.DurationMinutes;
            return (price, duration);
        }

        /// <summary>
        /// Valida horário comercial (+ almoço, ambos configuráveis por
        /// tenant — Fase 2) e conflito de agenda (a profissional não pode
        /// ter dois atendimentos que se sobrepõem) para um novo
        /// agendamento. <paramref name="localStart"/> é hora local (não
        /// UTC) — mesmo formato do datetime-local do form.
        /// Retorna null se válido, ou uma mensagem de erro pronta pra
        /// mostrar ao usuário.
        /// </summary>
        public async Task<string?> ValidateBookingAsync(string employeeId, DateTime localStart, int durationMinutes)
        {
            var localEnd = localStart.AddMinutes(durationMinutes);

            if (localStart.DayOfWeek == DayOfWeek.Sunday)
            {
                return "O salão não funciona aos domingos. Escolha outro dia.";
            }

            var openTime = _currentTenant.BusinessOpenTime;
            var closeTime = _currentTenant.BusinessCloseTime;

            if (localEnd.Date != localStart.Date ||
                localStart.TimeOfDay < openTime ||
                localEnd.TimeOfDay > closeTime)
            {
                return $"Horário fora do expediente. O salão atende das {openTime:hh\\:mm} às {closeTime:hh\\:mm}, e o atendimento (com duração de {durationMinutes} min) precisa terminar dentro desse período.";
            }

            if (_currentTenant.LunchStartTime.HasValue && _currentTenant.LunchEndTime.HasValue)
            {
                var lunchStart = _currentTenant.LunchStartTime.Value;
                var lunchEnd = _currentTenant.LunchEndTime.Value;

                if (localStart.TimeOfDay < lunchEnd && localEnd.TimeOfDay > lunchStart)
                {
                    return $"Esse horário cai no intervalo de almoço do salão ({lunchStart:hh\\:mm} às {lunchEnd:hh\\:mm}). Escolha outro horário.";
                }
            }

            // Conflito de horário: busca tudo que não foi cancelado e
            // filtra sobreposição em memória (mesmo padrão já usado no
            // projeto para contornar o bug PGRST100 do driver do Supabase).
            var utcStart = localStart.ToUniversalTime();
            var utcEnd = localEnd.ToUniversalTime();

            var employeeAppointments = await _supabase.From<Appointment>()
                .Where(x => x.EmployeeId == employeeId)
                .Where(x => x.TenantId == _currentTenant.Id)
                .Get();

            bool hasConflict = employeeAppointments.Models
                .Where(a => a.Status != "cancelled")
                .Any(a => a.StartTime < utcEnd && a.EndTime > utcStart);

            if (hasConflict)
            {
                return "Esta profissional já tem um atendimento marcado nesse horário. Escolha outro horário.";
            }

            return null;
        }
    }
}
