using System.Net;
using System.Net.Http;

namespace DeadheimLauncher.Services;

/// <summary>
/// Reenvio com espera para chamadas HTTP que o servidor recusou por excesso de
/// requisições (429) ou erro temporário (5xx).
///
/// Um perfil do Deadheim tem ~40 mods. Sem isso, instalar o perfil inteiro
/// dispara dezenas de chamadas seguidas e o Thunderstore começa a responder 429
/// no meio — foi exatamente o que o self-test pegou. Quando a resposta traz
/// Retry-After, esperamos o que o servidor pediu; senão, backoff exponencial.
/// </summary>
public static class HttpRetry
{
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient http,
        Func<HttpRequestMessage> requestFactory,
        int maxAttempts = 4,
        CancellationToken ct = default)
    {
        HttpResponseMessage? response = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            response?.Dispose();
            response = await http.SendAsync(requestFactory(), ct);

            if (response.StatusCode != HttpStatusCode.TooManyRequests && (int)response.StatusCode < 500)
                return response;

            if (attempt == maxAttempts)
                return response;

            var wait = response.Headers.RetryAfter?.Delta
                       ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));

            // Um Retry-After absurdo trava a instalação; acima de 30s desistimos
            // e deixamos o erro subir com a mensagem real do servidor.
            if (wait > TimeSpan.FromSeconds(30))
                return response;

            await Task.Delay(wait, ct);
        }

        return response!;
    }
}
