using Employee.Api.Domain;

namespace Employee.Api.Lg;

/// <summary>Alteração de um período de férias da LG: <paramref name="Acao"/> registrada | alterada | excluida (mesmas do evento).</summary>
public sealed record LgFeriasMudanca(Unavailability Ausencia, string Acao, DateOnly? InicioAnterior, DateOnly? FimAnterior);

/// <summary>
/// Resultado da aplicação num colaborador: <paramref name="Novas"/> (o chamador adiciona ao DbContext),
/// <paramref name="Removidas"/> (o chamador remove), todas as <paramref name="Mudancas"/> (eventos) e
/// <paramref name="ManuaisIguais"/>: períodos da LG já registrados manualmente (não duplicados).
/// </summary>
public sealed record LgFeriasAplicacao(
    IReadOnlyList<Unavailability> Novas, IReadOnlyList<Unavailability> Removidas, IReadOnlyList<LgFeriasMudanca> Mudancas, int ManuaisIguais)
{
    public bool Mudou => Mudancas.Count > 0;
}

/// <summary>
/// Regras das férias da LG gravadas como ausências do tipo Férias com origem "LG":
/// <list type="bullet">
/// <item>Período = DataDaOcorrencia..DataFim, os dois inclusive: escalável até a véspera do início, bloqueado do início ao
/// fim e disponível de novo no dia seguinte ao fim (o motor e o avaliador bloqueiam dia a dia pelas ausências).</item>
/// <item>Upsert idempotente pela chave colaborador + início: mesmo início com outro fim → ajusta o fim; período novo → cria.</item>
/// <item>Período da LG que não veio mais e ainda não terminou (em andamento ou futuro) → removido; os que já terminaram
/// ficam (histórico das escalas).</item>
/// <item>Períodos que terminaram há mais de LG:Ferias:MesesPassados meses não são importados.</item>
/// <item>Registros manuais (origem nula) nunca são alterados nem removidos; período da LG idêntico a um manual de férias
/// não é duplicado.</item>
/// </list>
/// </summary>
public static class LgFeriasRegras
{
    public const string Autor = "Integração LG";

    public static LgFeriasAplicacao Aplicar(Domain.Employee e, IEnumerable<LgFeriasPeriodo> periodos, DateOnly hoje, int mesesPassados, DateTime agora)
    {
        var limite = hoje.AddMonths(-Math.Max(0, mesesPassados));
        // Um período por início (o maior fim, se a LG repetir o início)
        var lg = periodos.Where(p => p.Fim >= p.Inicio && p.Fim >= limite)
            .GroupBy(p => p.Inicio)
            .Select(g => new LgFeriasPeriodo(g.Key, g.Max(p => p.Fim)))
            .OrderBy(p => p.Inicio)
            .ToList();
        var existentes = e.Unavailabilities.Where(u => u.DaLg).ToList();
        var novas = new List<Unavailability>();
        var removidas = new List<Unavailability>();
        var mudancas = new List<LgFeriasMudanca>();
        var manuaisIguais = 0;

        foreach (var p in lg)
        {
            var atual = existentes.FirstOrDefault(u => u.Start == p.Inicio);
            if (atual is not null)
            {
                if (atual.End == p.Fim && atual.Type == UnavailabilityType.Ferias) continue;
                var fimAnterior = atual.End;
                atual.End = p.Fim;
                atual.Type = UnavailabilityType.Ferias;
                atual.UpdatedAt = agora;
                atual.UpdatedBy = Autor;
                mudancas.Add(new LgFeriasMudanca(atual, "alterada", atual.Start, fimAnterior));
                continue;
            }
            // O operador já registrou exatamente este período à mão (ex.: pendência de período): não duplica
            if (e.Unavailabilities.Any(u => !u.DaLg && u.Type == UnavailabilityType.Ferias && u.Start == p.Inicio && u.End == p.Fim))
            {
                manuaisIguais++;
                continue;
            }
            var nova = new Unavailability
            {
                EmployeeId = e.Id,
                Start = p.Inicio,
                End = p.Fim,
                Type = UnavailabilityType.Ferias,
                Origem = Unavailability.OrigemLg,
                CreatedAt = agora,
                CreatedBy = Autor
            };
            e.Unavailabilities.Add(nova);
            novas.Add(nova);
            mudancas.Add(new LgFeriasMudanca(nova, "registrada", null, null));
        }

        // Saiu da LG (cancelada/remarcada) e ainda não terminou: remove; as já terminadas ficam como histórico
        var inicios = lg.Select(p => p.Inicio).ToHashSet();
        foreach (var u in existentes.Where(u => !inicios.Contains(u.Start) && u.End >= hoje))
        {
            e.Unavailabilities.Remove(u);
            removidas.Add(u);
            mudancas.Add(new LgFeriasMudanca(u, "excluida", null, null));
        }
        return new LgFeriasAplicacao(novas, removidas, mudancas, manuaisIguais);
    }
}
