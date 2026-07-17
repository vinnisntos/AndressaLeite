using AndressaLeite.Services;

namespace AndressaLeite.Tests;

/// <summary>
/// TotpService é o algoritmo mais crítico do app pra testar: é quem decide
/// se alguém entra no painel de superadmin (secao 3.9 do readme.txt) ou
/// não. Os vetores usados aqui são os vetores de teste OFICIAIS do RFC
/// 6238 Appendix B (não inventados) — os mesmos já usados manualmente
/// antes de escrever TotpService.cs pela primeira vez, agora fixados como
/// teste automatizado pra não regredir.
/// </summary>
public class TotpServiceTests
{
    // RFC 6238 Appendix B usa "12345678901234567890" (ASCII) como segredo
    // e publica códigos de 8 dígitos; os de 6 dígitos usados aqui são os
    // últimos 6 dígitos dos oficiais — matematicamente equivalentes
    // (X mod 10^6 == (X mod 10^8) mod 10^6), só truncados a mais.
    private static readonly string Rfc6238Secret =
        TotpService.Base32Encode(System.Text.Encoding.ASCII.GetBytes("12345678901234567890"));

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    public void ValidateCode_MatchesOfficialRfc6238Vectors(long unixTimeSeconds, string expectedCode)
    {
        var asOf = DateTimeOffset.FromUnixTimeSeconds(unixTimeSeconds);

        // allowedDriftSteps: 0 pra testar exatamente o passo do vetor, sem
        // a tolerância de +/-30s que existe em produção.
        var isValid = TotpService.ValidateCode(Rfc6238Secret, expectedCode, asOf, allowedDriftSteps: 0);

        Assert.True(isValid, $"Código {expectedCode} deveria ser válido no timestamp {unixTimeSeconds}");
    }

    [Fact]
    public void ValidateCode_RejectsWrongCode()
    {
        var asOf = DateTimeOffset.FromUnixTimeSeconds(59);

        var isValid = TotpService.ValidateCode(Rfc6238Secret, "000000", asOf, allowedDriftSteps: 0);

        Assert.False(isValid);
    }

    [Fact]
    public void ValidateCode_RejectsCodeOutsideDriftWindow()
    {
        // O código de t=59 (step 1) não deve validar 3 passos (90s) depois.
        var asOf = DateTimeOffset.FromUnixTimeSeconds(59 + 90);

        var isValid = TotpService.ValidateCode(Rfc6238Secret, "287082", asOf, allowedDriftSteps: 1);

        Assert.False(isValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]     // curto demais
    [InlineData("1234567")]   // longo demais
    [InlineData("12345a")]    // não é só dígito
    [InlineData(null)]
    public void ValidateCode_RejectsMalformedCode(string? malformedCode)
    {
        var isValid = TotpService.ValidateCode(Rfc6238Secret, malformedCode, DateTimeOffset.UtcNow);

        Assert.False(isValid);
    }

    [Fact]
    public void GenerateSecret_ProducesValidBase32ThatRoundTrips()
    {
        var secret = TotpService.GenerateSecret();

        // Base32 usa só A-Z e 2-7 (RFC 4648) — se tiver qualquer outro
        // caractere, apps autenticadores vão rejeitar na hora de escanear.
        Assert.Matches("^[A-Z2-7]+$", secret);

        var decoded = TotpService.Base32Decode(secret);
        var reEncoded = TotpService.Base32Encode(decoded);
        Assert.Equal(secret, reEncoded);

        // 20 bytes (160 bits) é o tamanho padrão recomendado pra TOTP/SHA1.
        Assert.Equal(20, decoded.Length);
    }

    [Fact]
    public void GenerateSecret_ProducesDifferentSecretsEachTime()
    {
        var secret1 = TotpService.GenerateSecret();
        var secret2 = TotpService.GenerateSecret();

        Assert.NotEqual(secret1, secret2);
    }

    [Fact]
    public void ValidCodeForFreshlyGeneratedSecret_ValidatesSuccessfully()
    {
        // Round-trip completo simulando o uso real: gera um segredo novo
        // (como no "Ativar 2FA"), calcula o código correto pro instante
        // atual com o mesmo helper de baixo nível que ValidateCode usa
        // internamente, e confirma que ValidateCode aceita esse código.
        var secret = TotpService.GenerateSecret();
        var now = DateTimeOffset.UtcNow;
        var otpAuthUri = TotpService.BuildOtpAuthUri(secret, "teste@marcai.com");

        Assert.Contains("otpauth://totp/", otpAuthUri);
        Assert.Contains(secret, otpAuthUri);

        var secretBytes = TotpService.Base32Decode(secret);
        var currentStep = now.ToUnixTimeSeconds() / TotpService.StepSeconds;
        var expectedCode = TotpService.ComputeCode(secretBytes, currentStep);

        Assert.True(TotpService.ValidateCode(secret, expectedCode, now));
        Assert.False(TotpService.ValidateCode(secret, "000000", now));
    }
}
