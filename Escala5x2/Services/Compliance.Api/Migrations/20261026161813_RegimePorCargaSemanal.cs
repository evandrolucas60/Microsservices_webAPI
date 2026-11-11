using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Compliance.Api.Migrations
{
    /// <summary>
    /// Regime da semana derivado da carga semanal (só dados; editável/bloqueável pelo superAdmin como as demais).
    /// Decisão da empresa: "na jornada de 44h semanais a escala deve ser a 6x1, nas demais é a 5x2".
    /// <list type="bullet">
    /// <item>Nova categoria "regime" com: carga_minima_regime_6x1_horas = 44 (semana com carga_horaria_semanal vigente
    /// ≥ 44h é 6x1; abaixo, 5x2; na semana de virada vale a carga mais restritiva); folgas_semana_6x1 = 1 e
    /// folgas_semana_5x2 = 2 (número EXATO de folgas: dias trabalhados = dias normais − folgas do regime; feriado sem
    /// autorização e afastamento reduzem os dias normais e não são folga); dias_consecutivos_max_6x1 = 6 (OJ 410 da
    /// SDI-1 do TST) e dias_consecutivos_max_5x2 = 5; folga_extra_domingo_6x1 = 1 (na semana 6x1 em que a pessoa folga no
    /// domingo pode haver 1 folga a mais: sem ela, 1 folga por semana + no máximo 6 dias seguidos só deixam a folga recuar
    /// na semana, e o rodízio de domingos da Lei 10.101 fica inviável).</item>
    /// <item>Aposentadas (convertidas, com valor, vigência, status e aplicação de cada linha): folgas_maximas_semana →
    /// folgas_semana_5x2; dias_consecutivos_max → dias_consecutivos_max_5x2 (o _6x1 herda o status/aplicação dela).
    /// As linhas antigas são removidas (ficam no backup e na auditoria) e o cadastro não aceita mais essas chaves;
    /// escalas com fotografia antiga continuam avaliadas pelo modelo anterior.</item>
    /// <item>folgas_minimas_semana vira o piso legal do DSR com vigência: a linha sem vigência passa a valer 1 até
    /// 15/12/2026 (CLT art. 67; Lei 605/1949) e ganha uma linha 2 a partir de 16/12/2026 (EC 141/2026, art. 10, I),
    /// com o mesmo status/aplicação. O piso prevalece sobre as folgas do regime.</item>
    /// <item>Regras bloqueadas: limiar bloqueado (ou carga semanal bloqueada na semana) → semana 5x2; folgas_semana_X
    /// bloqueada → sem número exato, vale só o piso (folgas extras possíveis); dias_consecutivos_max_X bloqueada → sem
    /// limite de dias seguidos nas semanas daquele regime.</item>
    /// </list>
    /// </summary>
    public partial class RegimePorCargaSemanal : Migration
    {
        private const string Columns = """
            "Categoria", "Chave", "Valor", "Unidade", "Descricao", "BaseLegal", "PisoLegal", "TetoLegal",
            "VigenciaInicio", "VigenciaFim", "Status", "Aplicacao", "AtualizadoPor", "AtualizadoEm"
            """;

        private const string Por = "migração regime 6x1/5x2";

        private const string DescCarga = "Regime da semana: com a carga semanal vigente (carga_horaria_semanal; na semana de virada, a mais restritiva) igual ou acima deste valor a semana é 6x1; abaixo, 5x2. Bloqueada: toda semana é 5x2";
        private const string BaseCarga = "Política interna (decisão da empresa: na jornada de 44h semanais a escala é 6x1; nas demais, 5x2); CF art. 7º, XIII e EC 141/2026, art. 6º (carga semanal)";
        private const string Desc6x1 = "Folgas por semana no regime 6x1 (número exato; feriado sem autorização e afastamento não contam como folga e reduzem os dias de trabalho). Bloqueada: vale só o piso das folgas mínimas";
        private const string Base6x1 = "CF art. 7º, XV; CLT art. 67; Lei 605/1949, art. 1º (repouso semanal remunerado de 24h consecutivas)";
        private const string Desc5x2 = "Folgas por semana no regime 5x2 (número exato; feriado sem autorização e afastamento não contam como folga e reduzem os dias de trabalho). Bloqueada: vale só o piso das folgas mínimas";
        private const string Base5x2 = "EC 141/2026, art. 10, I (2 folgas a partir de 16/12/2026); CLT art. 67; contrato 5x2 / política interna";
        private const string DescSeq6x1 = "Máximo de dias de trabalho seguidos nas semanas 6x1 (a sequência que vem da semana anterior, de qualquer regime, conta). Bloqueada: sem limite nas semanas 6x1";
        private const string BaseSeq6x1 = "CF art. 7º, XV; CLT art. 67; OJ 410 da SDI-1 do TST (repouso depois do 7º dia seguido viola o art. 7º, XV: no máximo 6 dias seguidos)";
        private const string DescSeq5x2 = "Máximo de dias de trabalho seguidos nas semanas 5x2 (a sequência que vem da semana anterior, de qualquer regime, conta). Bloqueada: sem limite nas semanas 5x2";
        private const string BaseSeq5x2 = "OJ 410 da SDI-1 do TST (teto legal de 6 dias seguidos); contrato 5x2 / política interna (5 dias seguidos)";
        private const string DescExtra = "1 = na semana 6x1 em que a pessoa folga no domingo pode haver 1 folga a mais (necessária para o rodízio de domingos sem passar de 6 dias seguidos; o motor só usa quando preciso). 0 ou bloqueada = número exato sempre (o 6x1 com rodízio de domingos fica inviável)";
        private const string BaseExtra = "Lei 10.101/2000, art. 6º, parágrafo único (folga no domingo a cada 3 semanas) combinada com a OJ 410 da SDI-1 do TST (no máximo 6 dias seguidos): com 1 folga por semana a folga só pode recuar na semana, e voltar ao domingo exige uma folga a mais";
        private const string DescMin ="Folgas mínimas na semana civil (piso legal do descanso semanal; prevalece sobre as folgas do regime)";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) Piso legal do DSR com vigência (só quando há uma linha única, sem vigência)
            migrationBuilder.Sql($"""
                INSERT INTO compliance_rules ({Columns})
                SELECT r."Categoria", r."Chave", 2, r."Unidade", '{DescMin}',
                       'EC 141/2026, art. 10, I (2 folgas a partir de 16/12/2026); CF art. 7º, XV; CLT art. 67; Lei 605/1949, art. 1º',
                       2, NULL, DATE '2026-12-16', NULL, r."Status", r."Aplicacao", '{Por} (folgas_minimas_semana ' || r."Id" || ')', now()
                FROM compliance_rules r
                WHERE r."Chave" = 'folgas_minimas_semana' AND r."VigenciaInicio" IS NULL AND r."VigenciaFim" IS NULL
                  AND NOT EXISTS (SELECT 1 FROM compliance_rules x WHERE x."Chave" = 'folgas_minimas_semana' AND x."Id" <> r."Id");

                UPDATE compliance_rules r
                SET "Valor" = 1, "VigenciaFim" = DATE '2026-12-15', "PisoLegal" = 1, "Descricao" = '{DescMin}',
                    "BaseLegal" = '{Base6x1}', "AtualizadoPor" = '{Por}', "AtualizadoEm" = now()
                WHERE r."Chave" = 'folgas_minimas_semana' AND r."VigenciaInicio" IS NULL AND r."VigenciaFim" IS NULL
                  AND EXISTS (SELECT 1 FROM compliance_rules x WHERE x."Chave" = 'folgas_minimas_semana'
                              AND x."VigenciaInicio" = DATE '2026-12-16' AND x."AtualizadoPor" LIKE '{Por}%');
                """);

            // 2) folgas_maximas_semana → folgas_semana_5x2 (cada linha, com valor, vigência, status e aplicação); sem ela, semeia 2
            migrationBuilder.Sql($"""
                INSERT INTO compliance_rules ({Columns})
                SELECT 'regime', 'folgas_semana_5x2', m."Valor", 'dias', '{Desc5x2}', '{Base5x2}',
                       CASE WHEN m."VigenciaFim" IS NOT NULL AND m."VigenciaFim" < DATE '2026-12-16' THEN 1 ELSE 2 END, NULL,
                       m."VigenciaInicio", m."VigenciaFim", m."Status", m."Aplicacao", '{Por} (folgas_maximas_semana ' || m."Id" || ')', now()
                FROM compliance_rules m
                WHERE m."Chave" = 'folgas_maximas_semana'
                  AND NOT EXISTS (SELECT 1 FROM compliance_rules r WHERE r."Chave" = 'folgas_semana_5x2');

                INSERT INTO compliance_rules ({Columns})
                SELECT 'regime', 'folgas_semana_5x2', 2, 'dias', '{Desc5x2}', '{Base5x2}', 2, NULL, NULL, NULL, 'Ativo', 'Rigida', 'seed', now()
                WHERE NOT EXISTS (SELECT 1 FROM compliance_rules r WHERE r."Chave" = 'folgas_semana_5x2');
                """);

            // 3) dias_consecutivos_max → dias_consecutivos_max_5x2 (cada linha); sem ela, semeia 5
            migrationBuilder.Sql($"""
                INSERT INTO compliance_rules ({Columns})
                SELECT 'regime', 'dias_consecutivos_max_5x2', m."Valor", 'dias', '{DescSeq5x2}', '{BaseSeq5x2}', NULL, 6,
                       m."VigenciaInicio", m."VigenciaFim", m."Status", m."Aplicacao", '{Por} (dias_consecutivos_max ' || m."Id" || ')', now()
                FROM compliance_rules m
                WHERE m."Chave" = 'dias_consecutivos_max'
                  AND NOT EXISTS (SELECT 1 FROM compliance_rules r WHERE r."Chave" = 'dias_consecutivos_max_5x2');

                INSERT INTO compliance_rules ({Columns})
                SELECT 'regime', 'dias_consecutivos_max_5x2', 5, 'dias', '{DescSeq5x2}', '{BaseSeq5x2}', NULL, 6, NULL, NULL, 'Ativo', 'Rigida', 'seed', now()
                WHERE NOT EXISTS (SELECT 1 FROM compliance_rules r WHERE r."Chave" = 'dias_consecutivos_max_5x2');
                """);

            // 4) Regras novas do 6x1 e o limiar (o máximo de dias seguidos do 6x1 herda o status/aplicação da regra aposentada)
            migrationBuilder.Sql($"""
                WITH antiga AS (
                  SELECT "Status", "Aplicacao" FROM compliance_rules WHERE "Chave" = 'dias_consecutivos_max'
                  ORDER BY "VigenciaInicio" DESC NULLS LAST LIMIT 1
                ),
                padrao(categoria, chave, valor, unidade, descricao, base_legal, piso, teto, status, aplicacao, ordem) AS (VALUES
                  ('regime', 'carga_minima_regime_6x1_horas', 44::numeric, 'horas', '{DescCarga}', '{BaseCarga}', NULL::numeric, NULL::numeric, 'Ativo', 'Rigida', 1),
                  ('regime', 'folgas_semana_6x1', 1, 'dias', '{Desc6x1}', '{Base6x1}', 1, NULL, 'Ativo', 'Rigida', 2),
                  ('regime', 'dias_consecutivos_max_6x1', 6, 'dias', '{DescSeq6x1}', '{BaseSeq6x1}', NULL, 6,
                   coalesce((SELECT "Status" FROM antiga), 'Ativo'), coalesce((SELECT "Aplicacao" FROM antiga), 'Rigida'), 3),
                  ('regime', 'folga_extra_domingo_6x1', 1, 'booleano', '{DescExtra}', '{BaseExtra}', NULL, NULL, 'Ativo', 'Rigida', 4)
                )
                INSERT INTO compliance_rules ({Columns})
                SELECT p.categoria, p.chave, p.valor, p.unidade, p.descricao, p.base_legal, p.piso, p.teto, NULL, NULL,
                       p.status, p.aplicacao, 'seed', now()
                FROM padrao p
                WHERE NOT EXISTS (SELECT 1 FROM compliance_rules r WHERE r."Chave" = p.chave)
                ORDER BY p.ordem;
                """);

            // 5) Aposenta as chaves substituídas (não ficam duas regras conflitantes)
            migrationBuilder.Sql("""
                DELETE FROM compliance_rules WHERE "Chave" IN ('folgas_maximas_semana', 'dias_consecutivos_max');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                INSERT INTO compliance_rules ({Columns})
                SELECT 'descanso', 'folgas_maximas_semana', r."Valor", 'dias',
                       'Folgas máximas por semana (escala 5x2: com as folgas mínimas, exatamente 2 folgas; feriado e afastamento não contam como folga e reduzem os dias de trabalho)',
                       'CLT art. 58 (jornada) e art. 67 (descanso semanal); EC 141/2026 (carga semanal); contrato 5x2 / política interna — o máximo de folgas não é limite legal: garante os 5 dias de trabalho do contrato',
                       NULL, NULL, r."VigenciaInicio", r."VigenciaFim", r."Status", r."Aplicacao", 'seed', now()
                FROM compliance_rules r
                WHERE r."Chave" = 'folgas_semana_5x2'
                  AND NOT EXISTS (SELECT 1 FROM compliance_rules x WHERE x."Chave" = 'folgas_maximas_semana');

                INSERT INTO compliance_rules ({Columns})
                SELECT 'descanso', 'dias_consecutivos_max', r."Valor", 'dias', 'Máximo de dias de trabalho seguidos', 'CLT art. 67; política 5x2',
                       NULL, 6, r."VigenciaInicio", r."VigenciaFim", r."Status", r."Aplicacao", '{Por} (desfeita)', now()
                FROM compliance_rules r
                WHERE r."Chave" = 'dias_consecutivos_max_5x2'
                  AND NOT EXISTS (SELECT 1 FROM compliance_rules x WHERE x."Chave" = 'dias_consecutivos_max');

                DELETE FROM compliance_rules WHERE "Chave" IN ('carga_minima_regime_6x1_horas', 'folgas_semana_6x1', 'folgas_semana_5x2',
                    'dias_consecutivos_max_6x1', 'dias_consecutivos_max_5x2', 'folga_extra_domingo_6x1');

                DELETE FROM compliance_rules WHERE "Chave" = 'folgas_minimas_semana' AND "VigenciaInicio" = DATE '2026-12-16'
                    AND "VigenciaFim" IS NULL AND "AtualizadoPor" LIKE '{Por}%';

                UPDATE compliance_rules
                SET "Valor" = 2, "VigenciaFim" = NULL, "PisoLegal" = 2, "Descricao" = 'Folgas mínimas na semana civil (segunda a domingo)',
                    "BaseLegal" = 'EC 141/2026, art. 10, I; CLT art. 67; política 5x2', "AtualizadoPor" = '{Por} (desfeita)', "AtualizadoEm" = now()
                WHERE "Chave" = 'folgas_minimas_semana' AND "VigenciaInicio" IS NULL AND "VigenciaFim" = DATE '2026-12-15'
                  AND NOT EXISTS (SELECT 1 FROM compliance_rules x WHERE x."Chave" = 'folgas_minimas_semana' AND x."VigenciaInicio" IS NOT NULL);
                """);
        }
    }
}
