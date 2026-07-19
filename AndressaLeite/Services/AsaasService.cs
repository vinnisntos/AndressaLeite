using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace AndressaLeite.Services
{
    /// <summary>
    /// Implementação via Asaas (readme.txt 4.9/9.2) — Checkout hospedado
    /// (`POST /v3/checkouts`, chargeTypes: RECURRENT), não a API de
    /// assinatura "crua": eu só mando os dados do pedido, a própria Asaas
    /// coleta PIX/cartão na página deles — nunca vejo dado de cartão.
    /// Config: Asaas:ApiKey, Asaas:BaseUrl (default sandbox),
    /// Asaas:CheckoutBaseUrl (default sandbox).
    /// </summary>
    public class AsaasService : IAsaasService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<AsaasService> _logger;
        private readonly string _checkoutBaseUrl;

        public AsaasService(HttpClient httpClient, IConfiguration configuration, ILogger<AsaasService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;

            var apiKey = configuration["Asaas:ApiKey"];
            var baseUrl = configuration["Asaas:BaseUrl"] ?? "https://api-sandbox.asaas.com/v3/";
            _checkoutBaseUrl = configuration["Asaas:CheckoutBaseUrl"] ?? "https://sandbox.asaas.com/checkoutSession/show";

            _httpClient.BaseAddress = new Uri(baseUrl);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                _httpClient.DefaultRequestHeaders.Add("access_token", apiKey);
            }
            // Obrigatório pra contas Asaas criadas depois de jun/2024 —
            // requisição sem User-Agent é rejeitada.
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MarcAi", "1.0"));
        }

        public async Task<AsaasCheckoutResult> CreateCheckoutSessionAsync(AsaasCheckoutRequest request)
        {
            var payload = new
            {
                billingTypes = new[] { "PIX", "CREDIT_CARD" },
                chargeTypes = new[] { "RECURRENT" },
                minutesToExpire = 60,
                items = new[]
                {
                    new
                    {
                        description = $"Assinatura MarcAi — {request.TenantName}",
                        quantity = 1,
                        value = request.Value
                    }
                },
                customerData = new
                {
                    name = request.OwnerName,
                    email = request.OwnerEmail,
                    cpfCnpj = request.CpfCnpj,
                    phone = request.Phone
                },
                subscription = new
                {
                    cycle = "MONTHLY",
                    nextDueDate = request.NextDueDate.ToString("yyyy-MM-dd")
                },
                externalReference = request.TenantId,
                callback = new
                {
                    successUrl = request.SuccessUrl,
                    cancelUrl = request.CancelUrl,
                    expiredUrl = request.ExpiredUrl
                }
            };

            var response = await _httpClient.PostAsJsonAsync("checkouts", payload);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                // Nunca inclui a API key numa mensagem pro usuário — só
                // loga o detalhe internamente, mesmo padrão do
                // ResendEmailService.
                _logger.LogError("Falha ao criar checkout no Asaas: {Status} {Body}", response.StatusCode, body);
                throw new InvalidOperationException("Falha ao criar checkout no Asaas.");
            }

            var result = System.Text.Json.JsonSerializer.Deserialize<AsaasCheckoutApiResponse>(body,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (result is null || string.IsNullOrWhiteSpace(result.Id))
            {
                _logger.LogError("Resposta do Asaas sem id de checkout: {Body}", body);
                throw new InvalidOperationException("Resposta inesperada do Asaas ao criar checkout.");
            }

            var checkoutUrl = $"{_checkoutBaseUrl}?id={result.Id}";
            return new AsaasCheckoutResult(result.Id, checkoutUrl);
        }

        private class AsaasCheckoutApiResponse
        {
            [JsonPropertyName("id")]
            public string Id { get; set; } = string.Empty;
        }
    }
}
