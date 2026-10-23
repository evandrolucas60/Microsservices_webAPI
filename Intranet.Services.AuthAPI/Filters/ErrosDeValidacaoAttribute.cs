using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Intranet.Services.AuthAPI.Filters;

/// <summary>
/// Devolve os erros de validação do modelo no formato que o front espera:
/// 400 { isSuccess: false, message, errors: { campo: [mensagens] } }, com o campo em camelCase.
/// Roda antes do filtro automático do [ApiController] (que devolveria o ProblemDetails padrão,
/// com nomes de campo em PascalCase e mensagens de JSON inválido em inglês).
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class ErrosDeValidacaoAttribute : Attribute, IActionFilter, IOrderedFilter
{
    public const string MensagemPadrao = "Verifique os campos destacados.";

    // ModelStateInvalidFilter usa -2000
    public int Order => -3000;

    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (context.ModelState.IsValid) return;

        var errors = new Dictionary<string, List<string>>();
        var corpoInvalido = false;

        foreach (var (key, entry) in context.ModelState)
        {
            if (entry.Errors.Count == 0) continue;

            var campo = NomeDoCampo(key);
            if (campo is null)
            {
                corpoInvalido = true;
                continue;
            }

            if (!errors.TryGetValue(campo, out var lista)) errors[campo] = lista = [];
            foreach (var error in entry.Errors)
            {
                // Erros de conversão do JSON (tipo errado) vêm do serializer, em inglês e sem ErrorMessage
                var msg = error.Exception is not null || key.StartsWith('$') || string.IsNullOrWhiteSpace(error.ErrorMessage)
                    ? "Valor inválido."
                    : error.ErrorMessage;
                if (!lista.Contains(msg)) lista.Add(msg);
            }
        }

        context.Result = new BadRequestObjectResult(new
        {
            isSuccess = false,
            message = corpoInvalido && errors.Count == 0 ? "Envie os dados do formulário." : MensagemPadrao,
            errors,
        });
    }

    public void OnActionExecuted(ActionExecutedContext context) { }

    /// <summary>"Nome" → "nome"; "$.ramal" → "ramal"; corpo inteiro ("", "$", "model") → null.</summary>
    private static string? NomeDoCampo(string key)
    {
        var k = key.StartsWith("$.") ? key[2..] : key;
        if (string.IsNullOrEmpty(k) || k == "$" || k.Equals("model", StringComparison.OrdinalIgnoreCase)) return null;
        return char.ToLowerInvariant(k[0]) + k[1..];
    }
}
