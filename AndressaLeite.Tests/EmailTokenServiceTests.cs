using AndressaLeite.Services;

namespace AndressaLeite.Tests;

/// <summary>
/// EmailTokenService é a única parte pura (sem I/O) do fluxo de e-mail
/// transacional (reset de senha, verificação de e-mail, convite de
/// equipe) — mesmo escopo de teste já praticado no projeto: cobre a
/// matemática de hash/expiração, não o que fala com Supabase/Resend.
/// </summary>
public class EmailTokenServiceTests
{
    [Fact]
    public void GenerateToken_HashMatchesIndependentHashOfSameRawToken()
    {
        var (rawToken, tokenHash) = EmailTokenService.GenerateToken();

        Assert.Equal(tokenHash, EmailTokenService.Hash(rawToken));
    }

    [Fact]
    public void GenerateToken_ProducesDifferentTokensEachCall()
    {
        var (rawToken1, hash1) = EmailTokenService.GenerateToken();
        var (rawToken2, hash2) = EmailTokenService.GenerateToken();

        Assert.NotEqual(rawToken1, rawToken2);
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void Hash_IsDeterministic()
    {
        var (rawToken, _) = EmailTokenService.GenerateToken();

        Assert.Equal(EmailTokenService.Hash(rawToken), EmailTokenService.Hash(rawToken));
    }

    [Fact]
    public void Hash_DifferentTokensProduceDifferentHashes()
    {
        var (rawToken1, _) = EmailTokenService.GenerateToken();
        var (rawToken2, _) = EmailTokenService.GenerateToken();

        Assert.NotEqual(EmailTokenService.Hash(rawToken1), EmailTokenService.Hash(rawToken2));
    }

    [Fact]
    public void IsExpired_NullExpiryIsAlwaysExpired()
    {
        Assert.True(EmailTokenService.IsExpired(null));
    }

    [Fact]
    public void IsExpired_FutureExpiryIsNotExpired()
    {
        var asOf = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var expiresAt = asOf.AddMinutes(30);

        Assert.False(EmailTokenService.IsExpired(expiresAt, asOf));
    }

    [Fact]
    public void IsExpired_PastExpiryIsExpired()
    {
        var asOf = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var expiresAt = asOf.AddMinutes(-1);

        Assert.True(EmailTokenService.IsExpired(expiresAt, asOf));
    }

    [Fact]
    public void IsExpired_ExactBoundaryIsExpired()
    {
        // asOf == expiresAt: já não é mais válido (limite inclusivo pro
        // lado "expirado") — mesmo raciocínio de janela fechada usado no
        // resto do projeto (ex.: janela de cancelamento em Supabase).
        var asOf = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(EmailTokenService.IsExpired(asOf, asOf));
    }
}
