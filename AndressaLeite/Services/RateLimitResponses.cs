using System.Net;

namespace AndressaLeite.Services
{
    /// <summary>
    /// Página de erro 429 (rate limit) com a identidade visual do MarcAi.
    /// Antes era um Response.WriteAsync de texto puro (fundo preto do
    /// navegador, sem marca nenhuma — readme.txt secao 10.3). Sem CSS/JS
    /// externo de propósito: é a resposta de erro, não pode depender de um
    /// CDN estar no ar.
    /// </summary>
    public static class RateLimitResponses
    {
        public static string BuildHtml(TimeSpan retryAfter)
        {
            var friendly = WebUtility.HtmlEncode(FormatRetryAfter(retryAfter));
            return $$"""
                <!DOCTYPE html>
                <html lang="pt-BR">
                <head>
                    <meta charset="utf-8" />
                    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                    <title>MarcAi</title>
                    <style>
                        body {
                            margin: 0;
                            min-height: 100vh;
                            display: flex;
                            align-items: center;
                            justify-content: center;
                            background-color: #f8f9fa;
                            color: #212529;
                            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
                            -webkit-font-smoothing: antialiased;
                        }
                        .card {
                            max-width: 420px;
                            margin: 24px;
                            padding: 40px 32px;
                            background: #ffffff;
                            border-radius: 16px;
                            box-shadow: 0 4px 18px rgba(0, 0, 0, 0.06);
                            text-align: center;
                        }
                        .brand {
                            font-weight: 700;
                            letter-spacing: 0.04em;
                            color: #d4a373;
                            font-size: 13px;
                            text-transform: uppercase;
                            margin-bottom: 18px;
                        }
                        h1 {
                            font-size: 20px;
                            font-weight: 600;
                            margin: 0 0 10px;
                        }
                        p {
                            color: #5f6570;
                            font-size: 15px;
                            line-height: 1.5;
                            margin: 0;
                        }
                    </style>
                </head>
                <body>
                    <div class="card">
                        <div class="brand">MarcAi</div>
                        <h1>Muitas tentativas</h1>
                        <p>Por segurança, pausamos essa ação por aqui. Tente novamente {{friendly}}.</p>
                    </div>
                </body>
                </html>
                """;
        }

        private static string FormatRetryAfter(TimeSpan retryAfter)
        {
            if (retryAfter.TotalSeconds <= 5) return "em instantes";
            if (retryAfter.TotalMinutes < 1) return $"em {(int)Math.Ceiling(retryAfter.TotalSeconds)} segundos";
            if (retryAfter.TotalMinutes < 2) return "em cerca de 1 minuto";
            if (retryAfter.TotalHours < 1) return $"em cerca de {(int)Math.Ceiling(retryAfter.TotalMinutes)} minutos";
            if (retryAfter.TotalHours < 1.5) return "em cerca de 1 hora";
            if (retryAfter.TotalDays < 1) return $"em cerca de {(int)Math.Ceiling(retryAfter.TotalHours)} horas";
            if (retryAfter.TotalDays < 1.5) return "amanhã";
            return $"em cerca de {(int)Math.Ceiling(retryAfter.TotalDays)} dias";
        }
    }
}
