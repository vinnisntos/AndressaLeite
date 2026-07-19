namespace AndressaLeite.Services
{
    /// <summary>
    /// Texto do lembrete de agendamento, extraído do que antes só existia
    /// dentro do link de WhatsApp (Pages/Profissional/DashProfissional.cshtml.cs,
    /// BuildWhatsAppReminderLink) pra ser reaproveitado também pelo e-mail
    /// automático (Services/AppointmentReminderService.cs, readme.txt 5.3).
    /// </summary>
    public static class AppointmentReminderMessages
    {
        /// <summary>Mesmo texto usado no link de WhatsApp — canal-agnóstico.</summary>
        public static string BuildReminderText(string clientName, string serviceName, string salonName, DateTime localStart)
        {
            return $"Olá {clientName}! Passando para lembrar do seu horário de {serviceName} em {salonName} " +
                $"no dia {localStart:dd/MM} às {localStart:HH:mm}. Qualquer imprevisto, nos avise! 💛";
        }

        public static string BuildReminderEmailSubject(string salonName)
            => $"Lembrete do seu horário em {salonName}";

        public static string BuildReminderEmailHtml(string clientName, string serviceName, string salonName, DateTime localStart)
        {
            var text = BuildReminderText(clientName, serviceName, salonName, localStart);
            return $"<p>{System.Net.WebUtility.HtmlEncode(text)}</p>";
        }
    }
}
