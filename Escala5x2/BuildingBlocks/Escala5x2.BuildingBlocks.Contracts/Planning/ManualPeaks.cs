namespace Escala5x2.BuildingBlocks.Contracts.Planning;

/// <summary>
/// Picos manuais (horários de pico cadastrados pelo usuário no Store.Api): rótulos usados pelo Store.Api, pelo motor
/// (avisos "Pico manual 11:00–14:00 de sábado pede 4, máximo possível 3") e pelo dimensionamento.
/// </summary>
public static class ManualPeaks
{
    public static string DayName(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "segunda",
        DayOfWeek.Tuesday => "terça",
        DayOfWeek.Wednesday => "quarta",
        DayOfWeek.Thursday => "quinta",
        DayOfWeek.Friday => "sexta",
        DayOfWeek.Saturday => "sábado",
        _ => "domingo"
    };

    /// <summary>"Pico manual 11:00–14:00 de sábado" (ou "de 24/12" para pico de uma data).</summary>
    public static string Label(TimeOnly start, TimeOnly end, DayOfWeek day, DateOnly? date) =>
        $"Pico manual {start:HH\\:mm}–{end:HH\\:mm} de {(date is { } d ? d.ToString("dd/MM") : DayName(day))}";

    public static string Label(DemandSnapshot p) => Label(p.Start, p.End, p.DayOfWeek, p.Data);
}
