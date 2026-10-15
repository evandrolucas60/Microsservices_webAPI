using Microsoft.AspNetCore.Http;

namespace BuildingBlocks.Shared.Exceptions;

/// <summary>
/// Erro de aplicação com status HTTP e mensagem amigável (pt-BR). O ExceptionHandlingMiddleware
/// converte em resposta JSON { isSuccess: false, message }.
/// </summary>
public class AppException : Exception
{
    public int StatusCode { get; }

    public AppException(string message, int statusCode = StatusCodes.Status400BadRequest, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }
}

/// <summary>Regra de negócio/validação violada (400).</summary>
public class BusinessRuleException(string message) : AppException(message, StatusCodes.Status400BadRequest);

/// <summary>Recurso inexistente (404).</summary>
public class NotFoundException(string message) : AppException(message, StatusCodes.Status404NotFound);

/// <summary>Sem permissão (403).</summary>
public class ForbiddenException(string message) : AppException(message, StatusCodes.Status403Forbidden);

/// <summary>Outro serviço (ou o RabbitMQ) não respondeu a tempo (503).</summary>
public class ServicoIndisponivelException(string message, Exception? inner = null)
    : AppException(message, StatusCodes.Status503ServiceUnavailable, inner);
