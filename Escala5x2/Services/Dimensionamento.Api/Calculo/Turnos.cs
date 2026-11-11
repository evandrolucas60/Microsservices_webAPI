namespace Dimensionamento.Api.Calculo;

/// <summary>
/// Turno-tipo candidato do modelo de cobertura (minutos desde 00:00): entrada, saída e intervalo de refeição.
/// Tipo: Abertura (entra na abertura), Fechamento (sai no fechamento), Flexivel (horários intermediários) ou
/// Cadastrado (turno do cadastro da loja).
/// </summary>
public sealed record TurnoTipo(int Inicio, int Fim, int IntervaloInicio, int IntervaloMinutos, string Tipo, string? Codigo = null)
{
    public int Duracao => Fim - Inicio;
    public int MinutosPagos => Duracao - IntervaloMinutos;
    public int IntervaloFim => IntervaloInicio + IntervaloMinutos;

    /// <summary>Trabalhando (fora do intervalo) em todo o trecho [de, ate).</summary>
    public bool Trabalhando(int de, int ate) =>
        de >= Inicio && ate <= Fim && (IntervaloMinutos == 0 || ate <= IntervaloInicio || de >= IntervaloFim);
}

/// <summary>Modos de geração dos turnos candidatos.</summary>
public static class ModoTurnos
{
    /// <summary>Abertura, fechamento e flexíveis no incremento configurado (operação da loja).</summary>
    public const string Operacao = "Operacao";
    /// <summary>Só os turnos fixos de abertura e de fechamento (ex.: 07:00–15:20 e 13:40–22:00).</summary>
    public const string Fixos = "Fixos";
    /// <summary>Turnos ativos do cadastro da loja (Store.Api).</summary>
    public const string Cadastrados = "Cadastrados";

    public static readonly string[] Todos = [Operacao, Fixos, Cadastrados];
}

/// <summary>Janela legal/política do intervalo (regras de conformidade).</summary>
public sealed record RegrasIntervalo(int InicioMinimoAposEntrada = 60, int FimMinimoAntesSaida = 60, int JornadaContinuaMaxima = 360);

/// <summary>Turno do cadastro da loja (entrada, saída, intervalo).</summary>
public sealed record TurnoCadastrado(string Codigo, int Inicio, int Fim, int IntervaloMinutos);

public static class GeradorTurnos
{
    /// <summary>
    /// Gera os turnos-tipo: cada entrada permitida × cada posição do intervalo dentro da janela (começa pelo menos
    /// N min depois da entrada, termina pelo menos N min antes da saída, nenhum trecho contínuo acima do máximo),
    /// no passo do incremento. Só turnos dentro do expediente.
    /// </summary>
    public static List<TurnoTipo> Gerar(
        int abertura, int fechamento, int duracao, int intervalo, int incremento, string modo, RegrasIntervalo regras,
        int? flexMaisCedo = null, int? flexMaisTarde = null, IReadOnlyList<TurnoCadastrado>? cadastrados = null)
    {
        if (fechamento <= abertura) throw new ArgumentException("O fechamento precisa ser depois da abertura no mesmo dia.");
        if (incremento <= 0) throw new ArgumentException("O incremento dos turnos flexíveis deve ser maior que zero.");
        var resultado = new List<TurnoTipo>();

        if (modo == ModoTurnos.Cadastrados && cadastrados is { Count: > 0 })
        {
            foreach (var c in cadastrados.Where(c => c.Fim > c.Inicio))
            {
                var tipo = c.Inicio <= abertura ? "Abertura" : c.Fim >= fechamento ? "Fechamento" : "Flexivel";
                // Parte do turno fora do expediente não conta para a cobertura, mas o turno continua candidato
                AdicionarComIntervalos(resultado, c.Inicio, c.Fim, c.IntervaloMinutos, incremento, regras, tipo, c.Codigo);
            }
            return resultado;
        }

        if (duracao > fechamento - abertura)
            throw new ArgumentException("A duração do turno é maior que o expediente.");

        var entradas = new SortedDictionary<int, string> { [abertura] = "Abertura" };
        entradas.TryAdd(fechamento - duracao, "Fechamento");
        if (modo != ModoTurnos.Fixos)
        {
            var maisCedo = Math.Max(flexMaisCedo ?? abertura + incremento, abertura);
            var maisTarde = Math.Min(flexMaisTarde ?? fechamento - duracao, fechamento - duracao);
            for (var e = maisCedo; e <= maisTarde; e += incremento) entradas.TryAdd(e, "Flexivel");
        }
        foreach (var (inicio, tipo) in entradas)
            AdicionarComIntervalos(resultado, inicio, inicio + duracao, intervalo, incremento, regras, tipo, null);
        return resultado;
    }

    private static void AdicionarComIntervalos(List<TurnoTipo> lista, int inicio, int fim, int intervalo, int passo, RegrasIntervalo r,
        string tipo, string? codigo)
    {
        var duracao = fim - inicio;
        if (intervalo <= 0)
        {
            lista.Add(new TurnoTipo(inicio, fim, inicio, 0, tipo, codigo));
            return;
        }
        var antes = new List<TurnoTipo>();
        for (var o = r.InicioMinimoAposEntrada; o + intervalo + r.FimMinimoAntesSaida <= duracao; o += passo)
        {
            if (o > r.JornadaContinuaMaxima || duracao - o - intervalo > r.JornadaContinuaMaxima) continue;
            antes.Add(new TurnoTipo(inicio, fim, inicio + o, intervalo, tipo, codigo));
        }
        if (antes.Count == 0)
        {
            // Sem posição que atenda a todas as janelas: intervalo no meio do turno (o motor da escala aponta o problema)
            var meio = inicio + (duracao - intervalo) / 2;
            antes.Add(new TurnoTipo(inicio, fim, meio, intervalo, tipo, codigo));
        }
        lista.AddRange(antes);
    }

    /// <summary>Máximo divisor comum (granularidade da faixa: todos os limites de turno e intervalo caem na grade).</summary>
    public static int Mdc(IEnumerable<int> valores)
    {
        var g = 0;
        foreach (var v in valores.Select(Math.Abs).Where(v => v > 0))
        {
            var a = g;
            var b = v;
            while (b != 0) (a, b) = (b, a % b);
            g = a;
        }
        return g == 0 ? 60 : g;
    }
}
