namespace Intranet.Services.GerenciamentoCategoriaAPI.Models.DTOs;

/// <summary>Envelope padrão das respostas (mesmo formato dos outros serviços).</summary>
public class ResponseDto
{
    public object? Result { get; set; }
    public bool IsSuccess { get; set; } = true;
    public string Message { get; set; } = string.Empty;
}
