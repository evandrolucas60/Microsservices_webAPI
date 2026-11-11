using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Compliance.Api.Migrations
{
    /// <summary>
    /// Cadastro de regras de conformidade (compliance_rules) no lugar das versões legais (legal_rule_versions):
    /// 1) cria a tabela; 2) converte cada versão legal nas chaves do cadastro, mantendo as vigências das fases
    /// da EC 141 (parâmetro igual em todas as versões vira uma linha sem vigência); 3) semeia as regras que eram
    /// fixas no motor e as novas (limite de domingo, intervalo...) que ainda não existirem; 4) remove a tabela antiga.
    /// Tudo na mesma transação da migration.
    /// </summary>
    public partial class ComplianceRules : Migration
    {
        // Metadados de cada chave: categoria, unidade, descrição, base legal, aplicação e ordem de exibição
        private const string Meta = """
            meta(chave, categoria, unidade, descricao, base_legal, aplicacao, ordem) AS (VALUES
              ('carga_horaria_semanal', 'jornada', 'horas', 'Máximo de horas por semana', 'CF art. 7º, XIII; EC 141/2026, art. 6º', 'Rigida', 1),
              ('limite_diario_minutos', 'jornada', 'minutos', 'Jornada diária máxima (minutos pagos, sem hora extra)', 'CF art. 7º, XIII; CLT art. 58', 'Rigida', 2),
              ('limite_diario_com_acordo_minutos', 'jornada', 'minutos', 'Jornada diária máxima quando a loja tem norma coletiva (vale no lugar do limite diário)', 'EC 141/2026, art. 7º', 'Rigida', 3),
              ('hora_extra_max_diaria_minutos', 'jornada', 'minutos', 'Hora extra máxima por dia (só para quem tem acordo de prorrogação)', 'CLT art. 59', 'Rigida', 4),
              ('limite_domingo_minutos', 'compliance', 'minutos', 'Limite líquido de jornada em domingos, em minutos', 'Política interna', 'Rigida', 5),
              ('folgas_minimas_semana', 'descanso', 'dias', 'Folgas mínimas na semana civil (segunda a domingo)', 'EC 141/2026, art. 10, I; CLT art. 67; política 5x2', 'Rigida', 6),
              ('dias_consecutivos_max', 'descanso', 'dias', 'Máximo de dias de trabalho seguidos', 'CLT art. 67; política 5x2', 'Rigida', 7),
              ('interjornada_minima_minutos', 'descanso', 'minutos', 'Descanso mínimo entre duas jornadas', 'CLT art. 66', 'Rigida', 8),
              ('folga_isolada_horas_livres', 'descanso', 'horas', 'Horas livres mínimas numa folga isolada (11h de interjornada + 24h de repouso)', 'CLT arts. 66 e 67; Súmula 110 do TST', 'Rigida', 9),
              ('domingo_folga_janela_semanas', 'domingo', 'semanas', 'Folga em pelo menos 1 domingo a cada N semanas', 'Lei 10.101/2000, art. 6º, parágrafo único', 'Rigida', 10),
              ('domingo_quinzenal_janela_semanas', 'domingo', 'semanas', 'Revezamento quinzenal (quem tem regra quinzenal): folga em 1 domingo a cada N semanas', 'CLT art. 386', 'Rigida', 11),
              ('intervalo_padrao_minutos', 'intervalo', 'minutos', 'Duração de pausa para refeição', 'CLT art. 71; política interna', 'Preferencial', 12),
              ('intervalo_minimo_minutos', 'intervalo', 'minutos', 'Intervalo mínimo de refeição para jornada acima de 6h', 'CLT art. 71', 'Rigida', 13),
              ('intervalo_maximo_minutos', 'intervalo', 'minutos', 'Intervalo máximo de refeição para jornada acima de 6h (salvo acordo escrito ou norma coletiva)', 'CLT art. 71', 'Rigida', 14),
              ('cobertura_faixa_minutos', 'cobertura', 'minutos', 'Tamanho da faixa de horário em que a cobertura da demanda é medida', NULL, 'Rigida', 15),
              ('feriado_trabalho_exige_autorizacao', 'feriado', 'booleano', 'Trabalho em feriado só com autorização em convenção coletiva (1 = exige)', 'Lei 10.101/2000, art. 6º-A', 'Rigida', 16)
            )
            """;

        // Limites legais (mesma regra de LegalLimits no código): o mais restritivo dentro da vigência
        private static string Piso(string a) => $"""
            CASE {a}.chave
              WHEN 'folgas_minimas_semana' THEN CASE WHEN {a}.fim IS NOT NULL AND {a}.fim < DATE '2026-12-16' THEN 1 ELSE 2 END
              WHEN 'interjornada_minima_minutos' THEN 660
              WHEN 'folga_isolada_horas_livres' THEN 35
              WHEN 'intervalo_minimo_minutos' THEN 60
              WHEN 'intervalo_padrao_minutos' THEN 60
              WHEN 'feriado_trabalho_exige_autorizacao' THEN 1
            END
            """;

        private static string Teto(string a) => $"""
            CASE {a}.chave
              WHEN 'carga_horaria_semanal' THEN CASE WHEN {a}.fim IS NULL OR {a}.fim >= DATE '2027-12-16' THEN 40 WHEN {a}.fim >= DATE '2026-12-16' THEN 42 ELSE 44 END
              WHEN 'limite_diario_minutos' THEN 480
              WHEN 'limite_diario_com_acordo_minutos' THEN 504
              WHEN 'hora_extra_max_diaria_minutos' THEN 120
              WHEN 'dias_consecutivos_max' THEN 6
              WHEN 'domingo_folga_janela_semanas' THEN 3
              WHEN 'domingo_quinzenal_janela_semanas' THEN 2
              WHEN 'intervalo_maximo_minutos' THEN 120
              WHEN 'intervalo_padrao_minutos' THEN 120
            END
            """;

        private const string Columns = """
            "Categoria", "Chave", "Valor", "Unidade", "Descricao", "BaseLegal", "PisoLegal", "TetoLegal",
            "VigenciaInicio", "VigenciaFim", "Status", "Aplicacao", "AtualizadoPor", "AtualizadoEm"
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "compliance_rules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Categoria = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Chave = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Valor = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    Unidade = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Descricao = table.Column<string>(type: "text", nullable: false),
                    BaseLegal = table.Column<string>(type: "text", nullable: true),
                    PisoLegal = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    TetoLegal = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    VigenciaInicio = table.Column<DateOnly>(type: "date", nullable: true),
                    VigenciaFim = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Aplicacao = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AtualizadoPor = table.Column<string>(type: "text", nullable: false),
                    AtualizadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_compliance_rules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_compliance_rules_Chave_VigenciaInicio",
                table: "compliance_rules",
                columns: new[] { "Chave", "VigenciaInicio" },
                unique: true);

            // 2) Versões legais → chaves do cadastro
            migrationBuilder.Sql($"""
                WITH {Meta},
                src AS (
                  SELECT v."Code" AS code, v."ValidFrom" AS de, v."ValidTo" AS ate, k.chave, round(k.valor, 2) AS valor
                  FROM legal_rule_versions v
                  CROSS JOIN LATERAL (VALUES
                    ('carga_horaria_semanal', v."WeeklyLimitMinutes" / 60.0),
                    ('limite_diario_minutos', v."DailyLimitMinutes"::numeric),
                    ('limite_diario_com_acordo_minutos', v."DailyLimitWithAgreementMinutes"::numeric),
                    ('hora_extra_max_diaria_minutos', v."MaxOvertimeMinutesPerDay"::numeric),
                    ('folgas_minimas_semana', v."MinRestDaysPerWeek"::numeric),
                    ('dias_consecutivos_max', v."MaxConsecutiveWorkDays"::numeric),
                    ('interjornada_minima_minutos', v."MinInterShiftRestMinutes"::numeric),
                    ('folga_isolada_horas_livres', (1440 + v."MinInterShiftRestMinutes") / 60.0),
                    ('domingo_folga_janela_semanas', v."SundayWindowWeeks"::numeric)
                  ) AS k(chave, valor)
                  -- 0 = "não se aplica" no limite com norma coletiva
                  WHERE NOT (k.chave = 'limite_diario_com_acordo_minutos' AND k.valor <= 0)
                ),
                total AS (SELECT count(*) AS n, min("ValidFrom") AS primeiro FROM legal_rule_versions),
                -- Mesmo valor em todas as versões: uma linha só, sem vigência (vale sempre)
                unico AS (SELECT chave FROM src GROUP BY chave HAVING count(DISTINCT valor) = 1 AND count(*) = (SELECT n FROM total)),
                linhas AS (
                  SELECT chave, min(valor) AS valor, NULL::date AS inicio, NULL::date AS fim, string_agg(code, ', ' ORDER BY de) AS origem
                  FROM src WHERE chave IN (SELECT chave FROM unico) GROUP BY chave
                  UNION ALL
                  SELECT chave, valor, CASE WHEN de = (SELECT primeiro FROM total) THEN NULL ELSE de END, ate, code
                  FROM src WHERE chave NOT IN (SELECT chave FROM unico)
                )
                INSERT INTO compliance_rules ({Columns})
                SELECT m.categoria, l.chave, l.valor, m.unidade, m.descricao, m.base_legal, {Piso("l")}, {Teto("l")},
                       l.inicio, l.fim, 'Ativo', m.aplicacao, 'migração de legal_rule_versions (' || l.origem || ')', now()
                FROM linhas l JOIN meta m ON m.chave = l.chave
                ORDER BY m.ordem, l.inicio NULLS FIRST;
                """);

            // 3) Regras que eram fixas no motor e as novas (só as chaves que ainda não existem)
            migrationBuilder.Sql($"""
                WITH {Meta},
                padrao(chave, valor, inicio, fim) AS (VALUES
                  ('carga_horaria_semanal', 44::numeric, NULL::date, DATE '2026-12-15'),
                  ('carga_horaria_semanal', 42, DATE '2026-12-16', DATE '2027-12-15'),
                  ('carga_horaria_semanal', 40, DATE '2027-12-16', NULL),
                  ('limite_diario_minutos', 480, NULL, NULL),
                  ('limite_diario_com_acordo_minutos', 504, DATE '2026-12-16', DATE '2027-12-15'),
                  ('hora_extra_max_diaria_minutos', 120, NULL, NULL),
                  ('limite_domingo_minutos', 480, NULL, NULL),
                  ('folgas_minimas_semana', 2, NULL, NULL),
                  ('dias_consecutivos_max', 5, NULL, NULL),
                  ('interjornada_minima_minutos', 660, NULL, NULL),
                  ('folga_isolada_horas_livres', 35, NULL, NULL),
                  ('domingo_folga_janela_semanas', 3, NULL, NULL),
                  ('domingo_quinzenal_janela_semanas', 2, NULL, NULL),
                  ('intervalo_padrao_minutos', 105, NULL, NULL),
                  ('intervalo_minimo_minutos', 60, NULL, NULL),
                  ('intervalo_maximo_minutos', 120, NULL, NULL),
                  ('cobertura_faixa_minutos', 30, NULL, NULL),
                  ('feriado_trabalho_exige_autorizacao', 1, NULL, NULL)
                )
                INSERT INTO compliance_rules ({Columns})
                SELECT m.categoria, p.chave, p.valor, m.unidade, m.descricao, m.base_legal, {Piso("p")}, {Teto("p")},
                       p.inicio, p.fim, 'Ativo', m.aplicacao, 'seed', now()
                FROM padrao p JOIN meta m ON m.chave = p.chave
                WHERE NOT EXISTS (SELECT 1 FROM compliance_rules r WHERE r."Chave" = p.chave)
                ORDER BY m.ordem, p.inicio NULLS FIRST;
                """);

            // 4) Só depois de migrar
            migrationBuilder.DropTable(
                name: "legal_rule_versions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "legal_rule_versions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedBy = table.Column<string>(type: "text", nullable: false),
                    Code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DailyLimitMinutes = table.Column<int>(type: "integer", nullable: false),
                    DailyLimitWithAgreementMinutes = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    LegalBasis = table.Column<string>(type: "text", nullable: false),
                    LegalOpinionRef = table.Column<string>(type: "text", nullable: true),
                    MaxConsecutiveWorkDays = table.Column<int>(type: "integer", nullable: false),
                    MaxOvertimeMinutesPerDay = table.Column<int>(type: "integer", nullable: false),
                    MinInterShiftRestMinutes = table.Column<int>(type: "integer", nullable: false),
                    MinRestDaysPerWeek = table.Column<int>(type: "integer", nullable: false),
                    Origin = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    SundayWindowWeeks = table.Column<int>(type: "integer", nullable: false),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true),
                    WeeklyLimitMinutes = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_legal_rule_versions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_legal_rule_versions_ValidFrom",
                table: "legal_rule_versions",
                column: "ValidFrom",
                unique: true);

            // Reconstrói uma versão por vigência da carga semanal com os demais parâmetros vigentes no início dela
            migrationBuilder.Sql("""
                WITH c AS (
                  SELECT coalesce("VigenciaInicio", DATE '2000-01-01') AS de, "VigenciaFim" AS ate, "Valor" AS horas
                  FROM compliance_rules WHERE "Chave" = 'carga_horaria_semanal' AND "Status" = 'Ativo'
                ),
                v AS (
                  SELECT c.*, (SELECT r."Valor" FROM compliance_rules r WHERE r."Status" = 'Ativo' AND r."Chave" = k.chave
                                 AND (r."VigenciaInicio" IS NULL OR r."VigenciaInicio" <= c.de) AND (r."VigenciaFim" IS NULL OR r."VigenciaFim" >= c.de)
                                 ORDER BY r."VigenciaInicio" DESC NULLS LAST LIMIT 1) AS valor, k.chave
                  FROM c CROSS JOIN (VALUES ('limite_diario_minutos'), ('limite_diario_com_acordo_minutos'), ('folgas_minimas_semana'),
                    ('dias_consecutivos_max'), ('interjornada_minima_minutos'), ('domingo_folga_janela_semanas'), ('hora_extra_max_diaria_minutos')) AS k(chave)
                )
                INSERT INTO legal_rule_versions ("Id", "Code", "Description", "LegalBasis", "Origin", "ValidFrom", "ValidTo",
                  "WeeklyLimitMinutes", "DailyLimitMinutes", "DailyLimitWithAgreementMinutes", "MinRestDaysPerWeek", "MaxConsecutiveWorkDays",
                  "MinInterShiftRestMinutes", "SundayWindowWeeks", "MaxOvertimeMinutesPerDay", "ApprovedBy", "CreatedAt")
                SELECT md5(random()::text || clock_timestamp()::text || de::text)::uuid,
                       CASE WHEN de >= DATE '2027-12-16' THEN 'EC141-DEFINITIVA' WHEN de >= DATE '2026-12-16' THEN 'EC141-TRANSICAO' ELSE 'PRE-EC141' END,
                       'Restaurada de compliance_rules', 'Restaurada de compliance_rules', 'Lei', de, ate, round(horas * 60),
                       coalesce(max(valor) FILTER (WHERE chave = 'limite_diario_minutos'), 480),
                       coalesce(max(valor) FILTER (WHERE chave = 'limite_diario_com_acordo_minutos'), 0),
                       coalesce(max(valor) FILTER (WHERE chave = 'folgas_minimas_semana'), 2),
                       coalesce(max(valor) FILTER (WHERE chave = 'dias_consecutivos_max'), 5),
                       coalesce(max(valor) FILTER (WHERE chave = 'interjornada_minima_minutos'), 660),
                       coalesce(max(valor) FILTER (WHERE chave = 'domingo_folga_janela_semanas'), 3),
                       coalesce(max(valor) FILTER (WHERE chave = 'hora_extra_max_diaria_minutos'), 120),
                       'migration down', now()
                FROM v GROUP BY de, ate, horas;
                """);

            migrationBuilder.DropTable(
                name: "compliance_rules");
        }
    }
}
