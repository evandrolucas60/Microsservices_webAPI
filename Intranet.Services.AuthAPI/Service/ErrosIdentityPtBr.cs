using Microsoft.AspNetCore.Identity;

namespace Intranet.Services.AuthAPI.Service;

/// <summary>
/// Traduz os erros do ASP.NET Identity (IdentityError.Code) para pt-BR, agrupados pelo campo do
/// formulário de cadastro (password / email). Códigos desconhecidos viram uma mensagem genérica.
/// </summary>
public static class ErrosIdentityPtBr
{
    public const string CampoSenha = "password";
    public const string CampoEmail = "email";
    /// <summary>Erros que não pertencem a um campo (o front mostra no topo do formulário).</summary>
    public const string CampoGeral = "geral";

    public static Dictionary<string, List<string>> Traduzir(IEnumerable<IdentityError> erros, PasswordOptions senha)
    {
        var resultado = new Dictionary<string, List<string>>();
        foreach (var erro in erros)
        {
            var (campo, mensagem) = Traduzir(erro, senha);
            if (!resultado.TryGetValue(campo, out var lista)) resultado[campo] = lista = [];
            if (!lista.Contains(mensagem)) lista.Add(mensagem);
        }
        return resultado;
    }

    private static (string Campo, string Mensagem) Traduzir(IdentityError erro, PasswordOptions senha) => erro.Code switch
    {
        nameof(IdentityErrorDescriber.PasswordTooShort) => (CampoSenha, $"A senha deve ter pelo menos {senha.RequiredLength} caracteres."),
        nameof(IdentityErrorDescriber.PasswordRequiresDigit) => (CampoSenha, "A senha deve ter pelo menos um número (0-9)."),
        nameof(IdentityErrorDescriber.PasswordRequiresUpper) => (CampoSenha, "A senha deve ter pelo menos uma letra maiúscula (A-Z)."),
        nameof(IdentityErrorDescriber.PasswordRequiresLower) => (CampoSenha, "A senha deve ter pelo menos uma letra minúscula (a-z)."),
        nameof(IdentityErrorDescriber.PasswordRequiresNonAlphanumeric) => (CampoSenha, "A senha deve ter pelo menos um caractere especial (ex.: ! @ # $ %)."),
        nameof(IdentityErrorDescriber.PasswordRequiresUniqueChars) => (CampoSenha, $"A senha deve ter pelo menos {senha.RequiredUniqueChars} caracteres diferentes."),
        nameof(IdentityErrorDescriber.PasswordMismatch) => (CampoSenha, "Senha incorreta."),
        nameof(IdentityErrorDescriber.DuplicateUserName) => (CampoEmail, "Já existe uma conta com este e-mail."),
        nameof(IdentityErrorDescriber.DuplicateEmail) => (CampoEmail, "Já existe uma conta com este e-mail."),
        nameof(IdentityErrorDescriber.InvalidEmail) => (CampoEmail, "Informe um e-mail válido."),
        nameof(IdentityErrorDescriber.InvalidUserName) => (CampoEmail, "O e-mail contém caracteres não permitidos (use letras sem acento, números e . _ - @ +)."),
        _ => (CampoGeral, "Não foi possível criar a conta. Verifique os dados informados."),
    };
}
