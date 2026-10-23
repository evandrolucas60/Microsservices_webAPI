using BuildingBlocks.Contracts.Identidade;
using System.ComponentModel.DataAnnotations;

namespace Intranet.Services.AuthAPI.Models.DTOs
{
    /// <summary>
    /// Cadastro de conta. Além do login (e-mail/senha), traz os dados essenciais do card da página
    /// de Ramais; eles seguem para o IdentidadeAPI no evento <see cref="ColaboradorCadastrado"/>.
    /// As mesmas regras são validadas no formulário (Frontend/src/features/auth/components/AuthForm.jsx).
    /// </summary>
    public class RegisterModel
    {
        [Required(ErrorMessage = "Informe o nome completo.")]
        [StringLength(100, MinimumLength = 3, ErrorMessage = "O nome deve ter entre 3 e 100 caracteres.")]
        [RegularExpression(@"^\s*\S+(\s+\S+)+\s*$", ErrorMessage = "Informe nome e sobrenome.")]
        public string? Nome { get; set; }

        [Required(ErrorMessage = "Informe o e-mail corporativo.")]
        [EmailAddress(ErrorMessage = "Informe um e-mail válido.")]
        [StringLength(256, ErrorMessage = "O e-mail deve ter no máximo 256 caracteres.")]
        public string? Email { get; set; }

        [Required(ErrorMessage = "Selecione o setor.")]
        [SetorValido]
        public string? Setor { get; set; }

        [Required(ErrorMessage = "Informe o cargo.")]
        [StringLength(100, MinimumLength = 2, ErrorMessage = "O cargo deve ter entre 2 e 100 caracteres.")]
        public string? Cargo { get; set; }

        [Required(ErrorMessage = "Selecione a loja/unidade.")]
        [LojaValida]
        public string? Loja { get; set; }

        [Required(ErrorMessage = "Informe o ramal.")]
        [RegularExpression(DadosCadastroColaborador.RamalRegex, ErrorMessage = "O ramal deve ter de 2 a 6 dígitos (somente números).")]
        public string? Ramal { get; set; }

        [Required(ErrorMessage = "Informe o telefone.")]
        [RegularExpression(DadosCadastroColaborador.TelefoneRegex, ErrorMessage = "Informe o telefone no formato (81) 99999-9999 ou (81) 3333-4444.")]
        public string? Telefone { get; set; }

        [Required(ErrorMessage = "Informe a senha.")]
        [StringLength(100, ErrorMessage = "A senha deve ter no máximo 100 caracteres.")]
        public string? Password { get; set; }

        [Required(ErrorMessage = "Confirme a senha.")]
        [Compare(nameof(Password), ErrorMessage = "As senhas não conferem.")]
        public string? ConfirmPassword { get; set; }
    }

    /// <summary>Setor precisa estar em <see cref="DadosCadastroColaborador.Setores"/> (vazio fica com o [Required]).</summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class SetorValidoAttribute : ValidationAttribute
    {
        public SetorValidoAttribute() : base("Selecione um setor da lista.") { }

        public override bool IsValid(object? value) =>
            value is not string s || string.IsNullOrWhiteSpace(s) || DadosCadastroColaborador.SetorValido(s);
    }

    /// <summary>Loja precisa estar em <see cref="DadosCadastroColaborador.Lojas"/> (vazio fica com o [Required]).</summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class LojaValidaAttribute : ValidationAttribute
    {
        public LojaValidaAttribute() : base("Selecione uma loja/unidade da lista.") { }

        public override bool IsValid(object? value) =>
            value is not string s || string.IsNullOrWhiteSpace(s) || DadosCadastroColaborador.LojaValida(s);
    }
}
