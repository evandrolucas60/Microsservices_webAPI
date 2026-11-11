using Escala5x2.BuildingBlocks.Contracts.Planning;

namespace Employee.Api.Domain;

public sealed class Employee
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Registration { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public Guid StoreId { get; set; }
    public string JobTitle { get; set; } = string.Empty;
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Perfil de jornada (<see cref="JourneyProfiles"/>): Abertura, Fechamento ou Flexivel. Só vale nos setores com
    /// operação configurada. Novos cadastros: Flexivel; existentes na migration PerfilJornada: Abertura/Fechamento
    /// alternados por loja e setor principal (como os turnos fixos de manhã e tarde de hoje).
    /// </summary>
    public string PerfilJornada { get; set; } = JourneyProfiles.Flexivel;
    /// <summary>Disponibilidade semanal (lista de <see cref="AvailabilityWindow"/> em JSON); nulo = disponível sempre.</summary>
    public string? DisponibilidadeJson { get; set; }
    /// <summary>Preferências opcionais (<see cref="JourneyPreferences"/> em JSON).</summary>
    public string? PreferenciasJson { get; set; }
    /// <summary>Custo/hora (R$) para custos adicionais (hora extra, adicional noturno); nulo = não informado.</summary>
    public decimal? CustoHora { get; set; }
    public string? JornadaAtualizadaPor { get; set; }
    public DateTime? JornadaAtualizadaEm { get; set; }

    // ---------- Integração LG (ConsultarListaHistoricosVigenteNaDataPorDemanda) ----------
    // Nulos = colaborador cadastrado só na Escala 5x2 (sem vínculo com a LG): disponível como sempre.

    /// <summary>Empresa da LG (Empresa.Codigo); com a matrícula identifica a pessoa entre lojas (mudança de centro de custo).</summary>
    public int? LgEmpresaCodigo { get; set; }
    /// <summary>SituacaoDoColaborador.Codigo da LG: 1 = ATIVIDADE NORMAL (disponível); qualquer outro = férias/afastamento.</summary>
    public int? LgSituacaoCodigo { get; set; }
    public string? LgSituacaoDescricao { get; set; }
    /// <summary>DataInicioSituacaoAtual da LG (início sugerido do período de férias/afastamento).</summary>
    public DateOnly? LgDataInicioSituacao { get; set; }
    public DateOnly? LgDataAdmissao { get; set; }
    /// <summary>CentroDeCusto.Codigo da LG (a loja vem da descrição: "VAREJO LOJA 02 - COHAB").</summary>
    public string? LgCentroDeCusto { get; set; }
    /// <summary>Última sincronização em que o colaborador veio na LG.</summary>
    public DateTime? LgSincronizadoEm { get; set; }
    /// <summary>Último ContratoDeTrabalhoParcial completo da LG (jsonb, todos os campos; CPF só sai mascarado/filtrado pela API).</summary>
    public string? LgRegistroJson { get; set; }
    /// <summary>Quando a sincronização inativou o colaborador por não vir mais na LG (volta a ser ativado se reaparecer).</summary>
    public DateTime? LgInativadoEm { get; set; }
    /// <summary>OrgaoResponsavel.Codigo atual na LG (o setor padrão do colaborador).</summary>
    public int? LgOrgaoCodigo { get; set; }
    /// <summary>OrgaoResponsavel.Descricao atual na LG (ex.: "DEPOSITO").</summary>
    public string? LgOrgaoDescricao { get; set; }
    /// <summary>Setor da loja (Store.Api) ligado ao órgão atual da LG; null = órgão vazio na LG.</summary>
    public Guid? LgSetorId { get; set; }
    /// <summary>
    /// Última consulta das férias na LG (ServicoDeFerias/ConsultarLista) com sucesso; null = nunca consultadas. A geração
    /// de escala avisa "usando a última sincronização de ..." quando a LG não responde.
    /// </summary>
    public DateTime? LgFeriasSincronizadoEm { get; set; }

    // ---------- Setores alterados manualmente ----------

    /// <summary>
    /// true = os setores foram definidos manualmente (edição do colaborador ou cadastro manual): a sincronização com a
    /// LG não os altera mais (só guarda o órgão atual em <see cref="LgSetorId"/> e a tela mostra a divergência). false =
    /// segue a LG: setor = órgão responsável, trocando se o órgão mudar na LG. "Voltar a seguir o setor da LG" limpa.
    /// </summary>
    public bool SetoresManuais { get; set; }
    public string? SetoresAlteradosPor { get; set; }
    public DateTime? SetoresAlteradosEm { get; set; }

    public List<EmployeeSector> Sectors { get; set; } = [];
    public List<ContractVersion> Contracts { get; set; } = [];
    public List<Unavailability> Unavailabilities { get; set; } = [];

    public ContractVersion? ContractAt(DateOnly date) =>
        Contracts.Where(c => c.ValidFrom <= date && (c.ValidTo is null || c.ValidTo >= date))
                 .OrderByDescending(c => c.ValidFrom)
                 .FirstOrDefault();
}

/// <summary>Habilitação da pessoa para trabalhar num setor (o setor pertence ao Store Service).</summary>
public sealed class EmployeeSector
{
    public Guid EmployeeId { get; set; }
    public Guid SectorId { get; set; }
}

/// <summary>
/// Versão de vínculo. Nunca é editada: uma mudança cria nova versão e só fecha a vigência
/// da anterior. Escalas antigas continuam apontando para a versão que usaram.
/// </summary>
public sealed class ContractVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public DateOnly ValidFrom { get; set; }
    public DateOnly? ValidTo { get; set; }
    /// <summary>Falso para cargos do art. 62 da CLT ou art. 8º da EC 141 (sem controle de jornada).</summary>
    public bool JourneyControlled { get; set; } = true;
    /// <summary>Existe acordo de prorrogação (CLT art. 59) válido.</summary>
    public bool OvertimeAgreement { get; set; }
    public SundayRule SundayRule { get; set; } = SundayRule.OneInThreeWeeks;
    /// <summary>Referência do parecer jurídico que definiu a regra de domingo, quando houver.</summary>
    public string? LegalOpinionRef { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = string.Empty;
}

/// <summary>
/// Tipos de ausência (nomes iguais a <see cref="UnavailabilityKinds"/>). AtestadoMedico = afastamento por dispensa
/// médica ("Afastamento médico"): não se pede nem se guarda CID/diagnóstico (LGPD, art. 11 — dado sensível).
/// </summary>
public enum UnavailabilityType { Ferias, Afastamento, Licenca, Indisponivel, AtestadoMedico }

/// <summary>
/// Período em que a pessoa não pode ser escalada. A observação é opcional, não deve conter dado de saúde e só é
/// mostrada a Admin, RH e Gestor da loja; o Colaborador vê só "Afastado".
/// </summary>
public sealed class Unavailability
{
    /// <summary>Origem dos períodos de férias importados da LG (<see cref="Origem"/>).</summary>
    public const string OrigemLg = "LG";

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public DateOnly Start { get; set; }
    public DateOnly End { get; set; }
    public UnavailabilityType Type { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    /// <summary>
    /// null = registrada na Escala 5x2 (manual); "LG" = período de férias importado da LG (ServicoDeFerias), mantido pela
    /// sincronização (cria, ajusta o fim, remove o futuro que sumiu da LG) e somente leitura na tela. A sincronização
    /// nunca toca registros manuais.
    /// </summary>
    public string? Origem { get; set; }

    public bool DaLg => Origem == OrigemLg;
}
