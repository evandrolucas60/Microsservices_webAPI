using System.Globalization;

namespace Dimensionamento.Api.Calculo;

/// <summary>Ponto da curva de demanda: média de documentos (chegadas) na hora, por dia. DiaSemana null = todos os dias.</summary>
public sealed record PontoCurva(int Hora, double Media, DayOfWeek? DiaSemana = null);

/// <summary>Total de documentos de um check-out no período importado.</summary>
public sealed record TotalCheckout(string Checkout, double Documentos);

/// <summary>Resultado da leitura do CSV <c>checkout;hora;dctos</c> (totais do período).</summary>
public sealed record ImportacaoCsv(
    int Linhas, double TotalDocumentos, int Dias, double[] TotalPorHora, double[] MediaPorHora,
    IReadOnlyList<TotalCheckout> Checkouts, IReadOnlyList<string> Avisos)
{
    /// <summary>Curva (uma média por hora com documentos), igual para todos os dias da semana.</summary>
    public List<PontoCurva> Curva() =>
        Enumerable.Range(0, 24).Where(h => TotalPorHora[h] > 0).Select(h => new PontoCurva(h, MediaPorHora[h])).ToList();
}

public static class CurvaDemanda
{
    /// <summary>Ordem dos dias no modelo: segunda (0) a domingo (6), igual à semana civil das escalas.</summary>
    public static readonly DayOfWeek[] Semana =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    public static int IndiceDia(DayOfWeek dia) => ((int)dia + 6) % 7;

    /// <summary>
    /// Lê o CSV do relatório "Análise ABC de vendas — distribuição por check-out + hora" transcrito como
    /// <c>checkout;hora;dctos</c> (separador ; ou , ou tab; cabeçalho opcional; hora 0–23, aceita "7h" e "07:00").
    /// Os documentos são o TOTAL do período: a média por hora e dia é total ÷ <paramref name="dias"/>.
    /// </summary>
    public static ImportacaoCsv LerCsv(string conteudo, int dias)
    {
        if (dias <= 0) throw new FormatException("Informe o número de dias do período (maior que zero).");
        if (string.IsNullOrWhiteSpace(conteudo)) throw new FormatException("O arquivo está vazio.");

        var porHora = new double[24];
        var porCheckout = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var avisos = new List<string>();
        var linhas = 0;
        var numero = 0;
        foreach (var bruta in conteudo.Replace("\r", string.Empty).Split('\n'))
        {
            numero++;
            var linha = bruta.Trim().TrimStart('﻿');
            if (linha.Length == 0) continue;
            var sep = linha.Contains(';') ? ';' : linha.Contains('\t') ? '\t' : ',';
            var campos = linha.Split(sep).Select(c => c.Trim().Trim('"')).ToArray();
            if (campos.Length < 3)
            {
                avisos.Add($"Linha {numero}: esperado checkout;hora;dctos — ignorada.");
                continue;
            }
            if (!TryHora(campos[1], out var hora))
            {
                // Cabeçalho (primeira linha não numérica) não é aviso
                if (linhas > 0 || numero > 1) avisos.Add($"Linha {numero}: hora \"{campos[1]}\" inválida — ignorada.");
                continue;
            }
            if (!TryNumero(campos[2], out var docs) || docs < 0)
            {
                avisos.Add($"Linha {numero}: quantidade \"{campos[2]}\" inválida — ignorada.");
                continue;
            }
            porHora[hora] += docs;
            var checkout = campos[0].Length == 0 ? "(sem check-out)" : campos[0];
            porCheckout[checkout] = porCheckout.GetValueOrDefault(checkout) + docs;
            linhas++;
        }
        if (linhas == 0) throw new FormatException("Nenhuma linha válida no formato checkout;hora;dctos.");

        var total = porHora.Sum();
        return new ImportacaoCsv(linhas, total, dias, porHora, porHora.Select(v => v / dias).ToArray(),
            porCheckout.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => new TotalCheckout(k.Key, k.Value)).ToList(),
            avisos);
    }

    private static bool TryHora(string texto, out int hora)
    {
        hora = -1;
        var t = texto.Trim().ToLowerInvariant().TrimEnd('h');
        if (t.Contains(':')) t = t.Split(':')[0];
        return int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out hora) && hora is >= 0 and <= 23;
    }

    private static bool TryNumero(string texto, out double valor)
    {
        var t = texto.Trim();
        // "1.234" (milhar) e "12,5" (decimal) no padrão brasileiro; "12.5" quando não há vírgula e não parece milhar
        if (t.Contains(','))
            t = t.Replace(".", string.Empty).Replace(',', '.');
        else if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^\d{1,3}(\.\d{3})+$"))
            t = t.Replace(".", string.Empty);
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out valor);
    }

    /// <summary>
    /// Matriz [dia 0..6 = seg..dom][hora 0..23] de documentos por hora. Ponto com dia da semana vale para aquele dia;
    /// sem dia, para todos os dias (multiplicado pelo fator do dia, quando houver).
    /// </summary>
    public static double[][] Matriz(IEnumerable<PontoCurva> curva, IReadOnlyList<double>? fatoresDia = null)
    {
        var pontos = curva.ToList();
        var m = new double[7][];
        for (var d = 0; d < 7; d++)
        {
            m[d] = new double[24];
            var dia = Semana[d];
            var fator = fatoresDia is { Count: 7 } ? fatoresDia[d] : 1.0;
            for (var h = 0; h < 24; h++)
            {
                var especifico = pontos.LastOrDefault(p => p.Hora == h && p.DiaSemana == dia);
                if (especifico is not null)
                {
                    m[d][h] = especifico.Media;
                    continue;
                }
                var geral = pontos.LastOrDefault(p => p.Hora == h && p.DiaSemana is null);
                m[d][h] = (geral?.Media ?? 0) * fator;
            }
        }
        return m;
    }

    /// <summary>Validação da curva digitada: hora 0–23, média ≥ 0, sem hora repetida para o mesmo dia.</summary>
    public static string? Validar(IReadOnlyList<PontoCurva> curva)
    {
        if (curva.Count == 0) return "Informe ao menos uma hora com demanda.";
        if (curva.Any(p => p.Hora is < 0 or > 23)) return "Hora deve estar entre 0 e 23.";
        if (curva.Any(p => p.Media < 0 || double.IsNaN(p.Media) || double.IsInfinity(p.Media))) return "A média de documentos por hora não pode ser negativa.";
        var repetida = curva.GroupBy(p => (p.Hora, p.DiaSemana)).FirstOrDefault(g => g.Count() > 1);
        return repetida is null ? null : $"A hora {repetida.Key.Hora}h aparece mais de uma vez{(repetida.Key.DiaSemana is { } d ? $" para {d}" : string.Empty)}.";
    }
}
