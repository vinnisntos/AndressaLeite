using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace AndressaLeite.Services
{
    /// <summary>
    /// Implementação via Resend (readme.txt 9.1) — HttpClient tipado, sem
    /// pacote NuGet dedicado: a API do Resend é um único POST JSON, coberto
    /// inteiro por System.Net.Http.Json (mesmo espírito de TotpService, que
    /// também evita puxar uma dependência pra algo simples o bastante pra
    /// escrever na mão).
    /// </summary>
    public class ResendEmailService : IEmailService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<ResendEmailService> _logger;
        private readonly string _fromAddress;

        public ResendEmailService(HttpClient httpClient, IConfiguration configuration, ILogger<ResendEmailService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;

            var apiKey = configuration["Resend:ApiKey"];
            _fromAddress = configuration["Resend:FromAddress"] ?? "MarcAi <noreply@marcai.app>";

            _httpClient.BaseAddress = new Uri("https://api.resend.com/");
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }
        }

        public async Task SendEmailAsync(string to, string subject, string htmlBody)
        {
            var payload = new
            {
                from = _fromAddress,
                to = new[] { to },
                subject,
                html = htmlBody
            };

            var response = await _httpClient.PostAsJsonAsync("emails", payload);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                // Nunca inclui a API key nem o corpo cru da resposta numa
                // mensagem pro usuário — só loga o detalhe internamente,
                // mesmo padrão de todo catch de exceção do projeto.
                _logger.LogError("Falha ao enviar e-mail via Resend para {To}: {Status} {Body}", to, response.StatusCode, body);
                throw new InvalidOperationException("Falha ao enviar e-mail via Resend.");
            }
        }
    }
}
