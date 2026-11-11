using Escala5x2.BuildingBlocks.Contracts.Planning;
using Escala5x2.BuildingBlocks.Shared.Auth;
using Xunit;

namespace Optimization.Tests;

/// <summary>
/// Cadastro: lojas da rede pela lista oficial da intranet (cadastradas na inicialização do Store.Api) e a classificação
/// de turnos do catálogo editados (abertura/fechamento/flexível pelo horário, refeita a cada geração).
/// </summary>
public sealed class CadastroTests
{
    [Theory]
    [InlineData("28 - ASA BRANCA", "28", "ASA BRANCA")]
    [InlineData("27 - NOSSA SENHORA DO Ó", "27", "NOSSA SENHORA DO Ó")]
    [InlineData("CD", "CD", LojasRede.NomeCentroDistribuicao)]
    public void Converte_loja_da_lista_em_codigo_e_nome(string loja, string code, string name)
    {
        Assert.Equal(new LojaRede(code, name), LojasRede.Converter(loja));
    }

    [Fact]
    public void Lista_oficial_tem_codigos_unicos_normalizados()
    {
        Assert.NotEmpty(LojasRede.Oficiais);
        Assert.Equal(LojasRede.Oficiais.Count, LojasRede.Oficiais.Select(l => CodigoLoja.Normalizar(l.Code)).Distinct().Count());
        Assert.Contains(LojasRede.Oficiais, l => l.Code == "CD");
        Assert.Contains(LojasRede.Oficiais, l => l.Code == "28" && l.Name == "ASA BRANCA");
    }

    [Fact]
    public void Faltantes_ignora_codigo_ja_cadastrado_com_ou_sem_zero_e_preserva_demo()
    {
        // "4" já cadastrada (sem zero à esquerda), "cd" em minúsculas e a DEMO: nenhuma é recriada
        var faltantes = LojasRede.Faltantes(["4", "cd", "DEMO"]);

        Assert.DoesNotContain(faltantes, l => CodigoLoja.Normalizar(l.Code) is "4" or "CD");
        Assert.DoesNotContain(faltantes, l => l.Code == "DEMO");
        Assert.Equal(LojasRede.Oficiais.Count - 2, faltantes.Count);
    }

    [Fact]
    public void Faltantes_e_idempotente()
    {
        var primeira = LojasRede.Faltantes(["DEMO"]);
        var segunda = LojasRede.Faltantes(primeira.Select(l => l.Code).Append("DEMO"));

        Assert.Equal(LojasRede.Oficiais.Count, primeira.Count);
        Assert.Empty(segunda);
    }

    [Fact]
    public void Faltantes_nao_repete_codigo_dentro_da_lista()
    {
        var faltantes = LojasRede.Faltantes([], [new LojaRede("04", "PRAZERES"), new LojaRede("4", "PRAZERES (duplicada)")]);

        Assert.Single(faltantes);
    }

    private static OperationSnapshot Op() => new(new TimeOnly(7, 0), new TimeOnly(22, 0), 2, 2, 2, 15, null, null, 500, 60);

    [Fact]
    public void Turno_editado_continua_classificado_pelo_horario()
    {
        // M 07:00–15:20 e T 13:40–22:00 (8h20 com 1h de intervalo)
        Assert.Equal(JourneyProfiles.Abertura, OperationPlan.Classify(Op(), new TimeOnly(7, 0), new TimeOnly(15, 20)));
        Assert.Equal(JourneyProfiles.Fechamento, OperationPlan.Classify(Op(), new TimeOnly(13, 40), new TimeOnly(22, 0)));
        // Editado para começar 07:30: deixa de ser abertura e vira flexível (entrada no incremento de 15 min)
        Assert.Equal(JourneyProfiles.Flexivel, OperationPlan.Classify(Op(), new TimeOnly(7, 30), new TimeOnly(15, 50)));
        // Editado para terminar 21:30 com outra duração: fora da operação (só escala manual)
        Assert.Null(OperationPlan.Classify(Op(), new TimeOnly(14, 0), new TimeOnly(21, 0)));
    }

    [Fact]
    public void Expand_usa_o_horario_atual_do_turno_editado()
    {
        var id = Guid.NewGuid();
        var editado = new ShiftSnapshot(id, "M", new TimeOnly(7, 0), new TimeOnly(15, 20), 60);

        var turnos = OperationPlan.Expand(Op(), Guid.NewGuid(), [editado], 60);

        Assert.Equal(JourneyProfiles.Abertura, turnos.Single(s => s.ShiftId == id).Tipo);
        // O turno editado cobre a abertura: o motor não gera um "AB" a mais
        Assert.DoesNotContain(turnos, s => s.Code.StartsWith("AB "));
    }
}
