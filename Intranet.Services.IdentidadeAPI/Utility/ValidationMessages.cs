using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;

namespace Intranet.Services.IdentidadeAPI.Utility;

/// <summary>
/// Resposta 400 de validação automática ([ApiController]) com mensagens em pt-BR.
/// Mantém o formato ValidationProblemDetails e acrescenta "message" (lida pelo front).
/// </summary>
public static partial class ValidationMessages
{
    public const string Title = "Há campos inválidos. Verifique os dados informados.";

    [GeneratedRegex(@"^The (.+) field is required\.?$")]
    private static partial Regex RequiredRegex();

    [GeneratedRegex(@"maximum length of (\d+)")]
    private static partial Regex MaxLengthRegex();

    /// <summary>Traduz as mensagens padrão (em inglês) do model binding/DataAnnotations.</summary>
    public static string ToPtBr(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "Valor inválido.";

        if (RequiredRegex().IsMatch(message)) return "Preencha os campos obrigatórios.";
        if (message.Contains("could not be converted", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("JSON", StringComparison.Ordinal) ||
            message.StartsWith("The input was not valid", StringComparison.OrdinalIgnoreCase))
            return "Algum campo foi enviado em formato inválido (verifique datas e números).";
        if (message.Contains("non-empty request body", StringComparison.OrdinalIgnoreCase))
            return "Nenhum dado foi enviado.";

        var maxLength = MaxLengthRegex().Match(message);
        if (maxLength.Success) return $"Texto muito longo (máximo de {maxLength.Groups[1].Value} caracteres).";

        if (message.Contains("is not valid", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("is invalid", StringComparison.OrdinalIgnoreCase))
            return "Valor inválido.";

        return message;
    }

    public static IActionResult InvalidModelStateResponse(ActionContext context)
    {
        var problem = new ValidationProblemDetails(context.ModelState)
        {
            Title = Title,
            Status = StatusCodes.Status400BadRequest,
        };

        foreach (var key in problem.Errors.Keys.ToList())
        {
            problem.Errors[key] = problem.Errors[key].Select(ToPtBr).Distinct().ToArray();
        }

        var messages = problem.Errors.Values.SelectMany(m => m).Distinct().ToList();
        problem.Extensions["message"] = messages.Count > 0 ? string.Join(" ", messages) : Title;

        // application/json: o front (useHttp) só lê o corpo de respostas JSON
        return new BadRequestObjectResult(problem) { ContentTypes = { "application/json" } };
    }
}
