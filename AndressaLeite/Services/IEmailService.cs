namespace AndressaLeite.Services
{
    /// <summary>
    /// Único ponto de envio de e-mail transacional do app. Interface de um
    /// método de propósito — o MarcAi só tem uma "modalidade" de e-mail
    /// (transacional, um destinatário, corpo HTML), então não há motivo
    /// pra uma abstração mais rica.
    /// </summary>
    public interface IEmailService
    {
        Task SendEmailAsync(string to, string subject, string htmlBody);
    }
}
