using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Compliance.Api.Migrations
{
    /// <summary>
    /// Regras do ajuste de almoço na visão do dia (só dados; editáveis pelo superAdmin como as demais). Semeia apenas as
    /// chaves que ainda não existem:
    /// <list type="bullet">
    /// <item>intervalo_obrigatorio_acima_minutos = 360 (Rígida, teto legal 360) — CLT art. 71, caput: substitui o limite de 6h
    /// que era fixo no código.</item>
    /// <item>intervalo_jornada_4a6h_minutos = 15 (Rígida, piso legal 15) — CLT art. 71, §1º.</item>
    /// <item>intervalo_inicio_minimo_apos_entrada_minutos = 60 e intervalo_fim_minimo_antes_saida_minutos = 60 (Preferenciais) —
    /// política interna: o TST considera desvirtuado o intervalo concedido logo no início ou no fim da jornada.</item>
    /// <item>jornada_continua_maxima_minutos = 360 (Rígida, teto legal 360) — CLT art. 71, caput: um trecho de mais de 6h
    /// sem pausa é exatamente o "trabalho contínuo" sem intervalo que a lei proíbe, mesmo que o almoço exista depois.</item>
    /// </list>
    /// Também preenche a base legal das regras de intervalo antigas que estiverem sem ela.
    /// </summary>
    public partial class RegrasIntervaloAlmoco : Migration
    {
        private const string Columns = """
            "Categoria", "Chave", "Valor", "Unidade", "Descricao", "BaseLegal", "PisoLegal", "TetoLegal",
            "VigenciaInicio", "VigenciaFim", "Status", "Aplicacao", "AtualizadoPor", "AtualizadoEm"
            """;

        private static readonly string[] Keys =
        [
            "intervalo_obrigatorio_acima_minutos", "intervalo_jornada_4a6h_minutos", "intervalo_inicio_minimo_apos_entrada_minutos",
            "intervalo_fim_minimo_antes_saida_minutos", "jornada_continua_maxima_minutos"
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                WITH novas(chave, valor, descricao, base_legal, piso, teto, aplicacao) AS (VALUES
                  ('intervalo_obrigatorio_acima_minutos', 360::numeric,
                   'Jornada líquida acima deste valor exige intervalo de refeição (mínimo/máximo/padrão passam a valer)',
                   'CLT art. 71, caput', NULL::numeric, 360::numeric, 'Rigida'),
                  ('intervalo_jornada_4a6h_minutos', 15,
                   'Intervalo mínimo para jornada acima de 4h até o limite do intervalo obrigatório',
                   'CLT art. 71, §1º', 15, NULL, 'Rigida'),
                  ('intervalo_inicio_minimo_apos_entrada_minutos', 60,
                   'O intervalo começa pelo menos N minutos depois da entrada',
                   'Política interna; finalidade do intervalo (CLT art. 71) — o TST considera desvirtuado o intervalo concedido no início da jornada',
                   NULL, NULL, 'Preferencial'),
                  ('intervalo_fim_minimo_antes_saida_minutos', 60,
                   'O intervalo termina pelo menos N minutos antes da saída',
                   'Política interna; finalidade do intervalo (CLT art. 71) — o TST considera desvirtuado o intervalo concedido no fim da jornada',
                   NULL, NULL, 'Preferencial'),
                  ('jornada_continua_maxima_minutos', 360,
                   'Máximo de trabalho contínuo antes ou depois do intervalo',
                   'CLT art. 71, caput (trabalho contínuo acima de 6h exige intervalo)', NULL, 360, 'Rigida')
                )
                INSERT INTO compliance_rules ({Columns})
                SELECT 'intervalo', n.chave, n.valor, 'minutos', n.descricao, n.base_legal, n.piso, n.teto,
                       NULL, NULL, 'Ativo', n.aplicacao, 'seed', now()
                FROM novas n
                WHERE NOT EXISTS (SELECT 1 FROM compliance_rules r WHERE r."Chave" = n.chave);
                """);

            migrationBuilder.Sql("""
                UPDATE compliance_rules SET "BaseLegal" = CASE "Chave"
                    WHEN 'intervalo_minimo_minutos' THEN 'CLT art. 71, caput'
                    WHEN 'intervalo_maximo_minutos' THEN 'CLT art. 71, caput (salvo acordo escrito ou norma coletiva)'
                    WHEN 'intervalo_padrao_minutos' THEN 'CLT art. 71; política interna' END
                WHERE "Chave" IN ('intervalo_minimo_minutos', 'intervalo_maximo_minutos', 'intervalo_padrao_minutos')
                  AND ("BaseLegal" IS NULL OR "BaseLegal" = '');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                DELETE FROM compliance_rules WHERE "AtualizadoPor" = 'seed' AND "Chave" IN ({string.Join(", ", Keys.Select(k => $"'{k}'"))});
                """);
        }
    }
}
