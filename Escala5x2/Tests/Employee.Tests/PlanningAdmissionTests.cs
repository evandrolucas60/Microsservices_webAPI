using Employee.Api.Domain;
using Employee.Api.Endpoints;
using Escala5x2.BuildingBlocks.Contracts.Planning;
using Xunit;

namespace Employee.Tests;

/// <summary>
/// Fotografia do planejamento com admissão no meio do período: antes, quem não tinha contrato vigente na segunda-feira do
/// horizonte (ex.: 26/10 para novembro/2026) ficava fora da escala do mês inteiro. Agora entra com o contrato que começa no
/// período e os dias sem contrato viram ausência ("Indisponivel"): não trabalha, não é folga.
/// </summary>
public sealed class PlanningAdmissionTests
{
    private static readonly DateOnly From = new(2026, 10, 26); // segunda-feira do horizonte de novembro/2026
    private static readonly DateOnly To = new(2026, 12, 6);

    private static Employee.Api.Domain.Employee Admitted(DateOnly validFrom, DateOnly? validTo = null)
    {
        var e = new Employee.Api.Domain.Employee { Registration = "777", Name = "Nova" };
        e.Contracts.Add(new ContractVersion { EmployeeId = e.Id, ValidFrom = validFrom, ValidTo = validTo });
        return e;
    }

    [Fact]
    public void Admission_in_the_middle_of_the_month_keeps_the_employee_with_the_days_before_as_absence()
    {
        var e = Admitted(new DateOnly(2026, 11, 12)); // quinta-feira

        var contract = EmployeeEndpoints.PlanningContract(e, From, To);
        Assert.NotNull(contract);
        var days = EmployeeEndpoints.PlanningAbsencesOf(e, From, To);
        Assert.Equal(new DateOnly(2026, 11, 12).DayNumber - From.DayNumber, days.Count);
        Assert.All(days, d => Assert.Equal(UnavailabilityKinds.Indisponivel, d.Tipo));
        Assert.Equal(new DateOnly(2026, 11, 11), days[^1].Date);
        Assert.DoesNotContain(days, d => d.Date >= new DateOnly(2026, 11, 12));
    }

    [Fact]
    public void Contract_ending_in_the_month_and_registered_absences_are_merged_without_duplicates()
    {
        var e = Admitted(new DateOnly(2020, 1, 1), new DateOnly(2026, 11, 20));
        e.Unavailabilities.Add(new Unavailability { EmployeeId = e.Id, Start = new DateOnly(2026, 11, 18), End = new DateOnly(2026, 11, 25), Type = UnavailabilityType.Ferias });

        var days = EmployeeEndpoints.PlanningAbsencesOf(e, From, To);

        // Férias de 18 a 25/11 (com o tipo) e, sem contrato, de 26/11 a 06/12
        Assert.Equal(8 + 11, days.Count);
        Assert.Equal(days.Count, days.Select(d => d.Date).Distinct().Count());
        Assert.Equal(UnavailabilityKinds.Ferias, days.Single(d => d.Date == new DateOnly(2026, 11, 21)).Tipo);
        Assert.Equal(UnavailabilityKinds.Indisponivel, days.Single(d => d.Date == new DateOnly(2026, 11, 30)).Tipo);
    }

    [Fact]
    public void Without_any_contract_in_the_period_the_employee_stays_out()
    {
        Assert.Null(EmployeeEndpoints.PlanningContract(Admitted(new DateOnly(2026, 12, 20)), From, To));
        Assert.Null(EmployeeEndpoints.PlanningContract(Admitted(new DateOnly(2020, 1, 1), new DateOnly(2026, 10, 1)), From, To));
    }
}
