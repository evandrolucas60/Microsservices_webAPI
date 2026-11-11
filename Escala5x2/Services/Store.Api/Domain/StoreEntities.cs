using Escala5x2.BuildingBlocks.Contracts.Planning;

namespace Store.Api.Domain;

public enum StoreFormat { Varejo, Atacarejo, Atacado }

public sealed class Store
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>Município da loja (feriados municipais). A sincronização com a LG nunca altera os dados de localização.</summary>
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = "PE";
    /// <summary>Endereço (cadastro manual da loja; a LG não preenche nem sobrescreve).</summary>
    public string? Logradouro { get; set; }
    public string? Numero { get; set; }
    public string? Bairro { get; set; }
    /// <summary>CEP só com os 8 dígitos.</summary>
    public string? Cep { get; set; }
    /// <summary>Código IBGE do município (7 dígitos, opcional): quando o feriado municipal também tem, a comparação é por ele.</summary>
    public string? CodigoIbge { get; set; }
    /// <summary>
    /// Funcionamento padrão nos feriados em que o trabalho é permitido (convenção ou ponto facultativo): null = horário
    /// normal do dia; Fechada; ou Especial com <see cref="FeriadoAbertura"/>/<see cref="FeriadoFechamento"/>. O feriado ou a
    /// exceção da loja para o feriado prevalecem (HolidayCalendar.Effective).
    /// </summary>
    public FuncionamentoDia? FeriadoFuncionamento { get; set; }
    public TimeOnly? FeriadoAbertura { get; set; }
    public TimeOnly? FeriadoFechamento { get; set; }
    /// <summary>Último a alterar o cadastro (localização/horário) da loja.</summary>
    public string? CadastroAtualizadoPor { get; set; }
    public DateTime? CadastroAtualizadoEm { get; set; }
    public StoreFormat Format { get; set; }
    /// <summary>Há norma coletiva cadastrada que autoriza 8h24 na transição (EC 141, art. 7º).</summary>
    public bool HasExtendedDayAgreement { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>
    /// false = centro de custo encerrado na LG (Status ≠ 0 ou DataFim passada). A loja nunca é apagada: sai dos
    /// seletores (o Admin ainda a vê, marcada "inativa") e volta a ativa se a LG reabrir o centro de custo.
    /// </summary>
    public bool Active { get; set; } = true;
    /// <summary>
    /// CentroDeCusto.Codigo da LG que define a loja (ex.: "201000" = "VAREJO LOJA 01 - CABO"). Único; preenchido pela
    /// sincronização de lojas. O Employee.Api casa o colaborador com a loja primeiro por este código.
    /// </summary>
    public string? LgCentroDeCustoCodigo { get; set; }
    /// <summary>Última sincronização com a LG que tocou esta loja.</summary>
    public DateTime? LgSincronizadoEm { get; set; }

    public List<Sector> Sectors { get; set; } = [];
    public List<Shift> Shifts { get; set; } = [];
    public List<StaffDemand> Demands { get; set; } = [];
    /// <summary>Horário de funcionamento por dia da semana (vazio = não cadastrado: vale o expediente da operação todos os dias).</summary>
    public List<StoreHours> Horarios { get; set; } = [];

    /// <summary>Padrão de funcionamento nos feriados (null = horário normal do dia).</summary>
    public HolidayHours? PadraoFeriado() => FeriadoFuncionamento is { } f ? new HolidayHours(f, FeriadoAbertura, FeriadoFechamento) : null;

    public StoreLocation Location() => new(Id, City, State, CodigoIbge);
}

/// <summary>
/// Horário de funcionamento da loja num dia da semana (uma linha por dia). É a fonte do expediente da operação da loja:
/// o motor usa o horário de cada dia (OperationSnapshot.Horarios); a exceção de um setor mantém o próprio expediente,
/// mas não trabalha nos dias em que a loja fecha.
/// </summary>
public sealed class StoreHours
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StoreId { get; set; }
    public DayOfWeek DiaSemana { get; set; }
    public bool Fechada { get; set; }
    public TimeOnly? Abertura { get; set; }
    public TimeOnly? Fechamento { get; set; }
}

/// <summary>
/// Funcionamento de uma loja num feriado específico (exceção ao funcionamento do feriado e ao padrão de feriados da loja).
/// Gestor da loja e Admin editam. Normal/Especial só em feriado com trabalho permitido (convenção ou ponto facultativo).
/// </summary>
public sealed class HolidayStoreHours
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HolidayId { get; set; }
    public Guid StoreId { get; set; }
    public FuncionamentoDia Funcionamento { get; set; }
    public TimeOnly? Abertura { get; set; }
    public TimeOnly? Fechamento { get; set; }
    public string AtualizadoPor { get; set; } = string.Empty;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;

    public HolidayHours ToHours() => new(Funcionamento, Abertura, Fechamento);
}

/// <summary>
/// Horário de pico cadastrado manualmente: na faixa, o motor busca <see cref="Quantidade"/> pessoas (substitui a demanda
/// calculada/cadastrada para a faixa; nunca abaixo do mínimo rígido da operação) e/ou <see cref="Adicional"/> pessoas a mais.
/// Por loja, com setor opcional (null = todos os setores da loja); vale no <see cref="DiaSemana"/> ou só na
/// <see cref="Data"/> (o pico de uma data prevalece sobre o do dia da semana). Preferencial, como o reforço de pico.
/// </summary>
public sealed class ManualPeak
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StoreId { get; set; }
    public Guid? SectorId { get; set; }
    public DayOfWeek? DiaSemana { get; set; }
    public DateOnly? Data { get; set; }
    public TimeOnly Inicio { get; set; }
    public TimeOnly Fim { get; set; }
    public int? Quantidade { get; set; }
    public int? Adicional { get; set; }
    public string? Descricao { get; set; }
    public string AtualizadoPor { get; set; } = string.Empty;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;

    public DayOfWeek Dia => Data?.DayOfWeek ?? DiaSemana ?? DayOfWeek.Monday;
}

/// <summary>Origem do setor: cadastrado pelo gestor (Manual) ou criado pela integração LG a partir do OrgaoResponsavel.</summary>
public enum SectorOrigem { Manual, Lg }

/// <summary>
/// Setor da loja. Nunca é apagado (escalas, demandas e colaboradores o referenciam). Com <see cref="LgOrgaoCodigo"/>, é o
/// setor do OrgaoResponsavel da LG naquela loja (no máximo um setor por órgão e loja): criado pela LG
/// (<see cref="Origem"/> = Lg, nome = descrição da LG, acompanha a LG se a descrição mudar) ou setor manual adotado pelo
/// nome (Origem continua Manual e o nome dado pelo gestor nunca é sobrescrito). A fonte principal dos setores LG é o
/// catálogo de órgãos responsáveis da empresa (ConsultarListaPorDemanda), garantido em todas as lojas.
/// </summary>
public sealed class Sector
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StoreId { get; set; }
    public string Name { get; set; } = string.Empty;
    public SectorOrigem Origem { get; set; } = SectorOrigem.Manual;
    /// <summary>
    /// false = setor da LG cujo órgão ficou inativo (Status ≠ 0), perdeu a habilitação ou saiu do catálogo. Não aparece
    /// para novas escalas/edição, mas escalas, demandas e colaboradores que já o usam continuam funcionando; volta a ativo
    /// se o órgão voltar. Setor manual nunca é inativado pela LG.
    /// </summary>
    public bool Active { get; set; } = true;
    /// <summary>OrgaoResponsavel.Codigo da LG (ex.: 2 = FRENTE DE LOJA). Único por loja quando preenchido.</summary>
    public int? LgOrgaoCodigo { get; set; }
    /// <summary>Última OrgaoResponsavel.Descricao recebida da LG (comparação e aviso quando muda).</summary>
    public string? LgOrgaoDescricao { get; set; }
    /// <summary>Última sincronização (catálogo de órgãos ou colaboradores) que confirmou este órgão.</summary>
    public DateTime? LgSincronizadoEm { get; set; }
}

/// <summary>Turno do catálogo. O intervalo de refeição não entra na jornada (CLT art. 71, §2º).</summary>
public sealed class Shift
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StoreId { get; set; }
    public string Code { get; set; } = string.Empty;
    public TimeOnly Start { get; set; }
    public TimeOnly End { get; set; }
    public int BreakMinutes { get; set; }
    public bool Active { get; set; } = true;

    public int DurationMinutes
    {
        get
        {
            var start = Start.Hour * 60 + Start.Minute;
            var end = End.Hour * 60 + End.Minute;
            if (end <= start) end += 24 * 60;
            return end - start;
        }
    }

    public int PaidMinutes => DurationMinutes - BreakMinutes;
}

/// <summary>
/// Operação da loja (<see cref="SectorId"/> nulo) ou exceção de um setor: expediente, mínimos rígidos de cobertura,
/// abertura e fechamento, entradas flexíveis e duração da jornada (ver <see cref="OperationSnapshot"/>). O setor sem
/// exceção usa a operação da loja; loja sem operação segue o modelo anterior (só demanda por faixa).
/// Gestor da loja e Admin editam.
/// </summary>
public sealed class StoreOperation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StoreId { get; set; }
    public Guid? SectorId { get; set; }
    public TimeOnly Abertura { get; set; }
    public TimeOnly Fechamento { get; set; }
    public int CoberturaMinima { get; set; }
    public int MinimoAbertura { get; set; }
    public int MinimoFechamento { get; set; }
    public int IncrementoFlexivelMinutos { get; set; }
    /// <summary>Nulo = abertura + incremento.</summary>
    public TimeOnly? EntradaFlexivelMaisCedo { get; set; }
    /// <summary>Nulo = fechamento − duração − incremento.</summary>
    public TimeOnly? EntradaFlexivelMaisTarde { get; set; }
    public int DuracaoJornadaMinutos { get; set; } = OperationPlan.DuracaoPadraoMinutos;
    /// <summary>Intervalo dos turnos gerados. Nulo = intervalo_padrao_minutos do cadastro de regras.</summary>
    public int? IntervaloMinutos { get; set; }
    /// <summary>
    /// Quando faltar gente com perfil Abertura/Fechamento, o motor faz rodízio entre os colaboradores do setor (turno fora
    /// do perfil com penalidade). Padrão ligado.
    /// </summary>
    public bool RodizioAberturaFechamento { get; set; } = true;
    /// <summary>
    /// Setor com exatamente 2 colaboradores disponíveis no mês: folgas fixas (mesmos dias da semana) e intercaladas (as dos
    /// dois nunca coincidem). Padrão ligado. Ver <see cref="OperationSnapshot.FolgasFixasSetorDuasPessoas"/>.
    /// </summary>
    public bool FolgasFixasSetorDuasPessoas { get; set; } = true;
    /// <summary>
    /// Gerar mesmo quando o quadro não cobre o mínimo: no setor em que a cobertura mínima é impossível com o quadro, a
    /// cobertura vira meta (a escala sai com as faixas descobertas avisadas). Padrão ligado. Ver
    /// <see cref="OperationSnapshot.CoberturaComoMetaQuadroInsuficiente"/>.
    /// </summary>
    public bool CoberturaComoMetaQuadroInsuficiente { get; set; } = true;
    /// <summary>
    /// Turnos derivados do horário de funcionamento de cada dia e das regras de conformidade vigentes (padrão ligado; a
    /// migration TurnosAutomaticos ligou em todas as operações). Desligado = "Personalizar turnos": valem
    /// <see cref="DuracaoJornadaMinutos"/>, <see cref="IntervaloMinutos"/>, a janela flexível e o catálogo de turnos. Ver
    /// <see cref="OperationSnapshot.TurnosAutomaticos"/>.
    /// </summary>
    public bool TurnosAutomaticos { get; set; } = true;
    public string AtualizadoPor { get; set; } = string.Empty;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;

    public OperationSnapshot ToSnapshot() => new(Abertura, Fechamento, CoberturaMinima, MinimoAbertura, MinimoFechamento,
        IncrementoFlexivelMinutos, EntradaFlexivelMaisCedo, EntradaFlexivelMaisTarde, DuracaoJornadaMinutos, IntervaloMinutos,
        RodizioAberturaFechamento, FolgasFixasSetorDuasPessoas: FolgasFixasSetorDuasPessoas,
        CoberturaComoMetaQuadroInsuficiente: CoberturaComoMetaQuadroInsuficiente, TurnosAutomaticos: TurnosAutomaticos);
}

/// <summary>Quantidade mínima de pessoas num setor, num dia da semana e numa faixa de horário.</summary>
public sealed class StaffDemand
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StoreId { get; set; }
    public Guid SectorId { get; set; }
    public DayOfWeek DayOfWeek { get; set; }
    public TimeOnly Start { get; set; }
    public TimeOnly End { get; set; }
    public int MinStaff { get; set; }
}

/// <summary>
/// Feriado por abrangência: Nacional (todas as lojas), Estadual (lojas da <see cref="Uf"/>), Municipal (lojas do
/// <see cref="Municipio"/>/<see cref="Uf"/>, comparados sem maiúsculas/acentos, ou do <see cref="CodigoIbge"/>) ou Loja
/// (só a <see cref="StoreId"/>: fechamento/data própria da loja). <see cref="PontoFacultativo"/>: não é feriado legal
/// (Carnaval, Corpus Christi). <see cref="Funcionamento"/>: como as lojas funcionam no dia (null = padrão de cada loja).
/// <see cref="Recorrente"/>: repete todo ano no mesmo dia/mês a partir do ano de <see cref="Date"/> (ex.: 07/09);
/// feriados móveis (Carnaval, Sexta-feira Santa, Corpus Christi) são cadastrados ano a ano, sem recorrência.
/// Trabalho só com autorização em convenção coletiva (Lei 10.101/2000, art. 6º-A).
/// </summary>
public sealed class Holiday
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public HolidayScope Abrangencia { get; set; }
    /// <summary>Sigla da UF (estadual e municipal), em maiúsculas.</summary>
    public string? Uf { get; set; }
    /// <summary>Município como digitado (municipal).</summary>
    public string? Municipio { get; set; }
    /// <summary>Chave normalizada do local (<see cref="HolidayCalendar.LocalKey"/>): "", "PE" ou "PE|CABO DE SANTO AGOSTINHO".</summary>
    public string LocalChave { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Recorrente { get; set; }
    public bool WorkAuthorized { get; set; }
    public string? AuthorizationRef { get; set; }
    public bool PontoFacultativo { get; set; }
    /// <summary>Loja do feriado de abrangência Loja.</summary>
    public Guid? StoreId { get; set; }
    /// <summary>Código IBGE do município (feriado municipal, opcional).</summary>
    public string? CodigoIbge { get; set; }
    public FuncionamentoDia? Funcionamento { get; set; }
    public TimeOnly? Abertura { get; set; }
    public TimeOnly? Fechamento { get; set; }
    public string CriadoPor { get; set; } = string.Empty;
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public string? AtualizadoPor { get; set; }
    public DateTime? AtualizadoEm { get; set; }

    public HolidayDefinition ToDefinition() => new(Id, Abrangencia, Uf, Municipio, Date, Name, Recorrente, WorkAuthorized, AuthorizationRef,
        PontoFacultativo, StoreId, CodigoIbge, Funcionamento, Abertura, Fechamento);
}
