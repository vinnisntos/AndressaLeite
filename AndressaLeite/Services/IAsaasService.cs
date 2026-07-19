namespace AndressaLeite.Services
{
    /// <summary>
    /// Integração com o Asaas (billing, readme.txt 4.9/9.2) — só o
    /// necessário pra criar um checkout hospedado de assinatura recorrente.
    /// Nunca colide com dado de cartão: a Asaas coleta PIX/cartão na
    /// própria página deles, eu só recebo o resultado via webhook (ver
    /// Pages/Webhooks/Asaas.cshtml.cs).
    /// </summary>
    public interface IAsaasService
    {
        Task<AsaasCheckoutResult> CreateCheckoutSessionAsync(AsaasCheckoutRequest request);
    }

    public record AsaasCheckoutRequest(
        string TenantId,
        string TenantName,
        string OwnerName,
        string OwnerEmail,
        string CpfCnpj,
        string Phone,
        DateTime NextDueDate,
        decimal Value,
        string SuccessUrl,
        string CancelUrl,
        string ExpiredUrl
    );

    public record AsaasCheckoutResult(string CheckoutId, string CheckoutUrl);
}
