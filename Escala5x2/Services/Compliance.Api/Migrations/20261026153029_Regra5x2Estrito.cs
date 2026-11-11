using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Compliance.Api.Migrations
{
    /// <summary>
    /// Escala 5x2 estrita (só dados; editável/bloqueável pelo superAdmin como as demais). Semeia, se ainda não existir:
    /// <list type="bullet">
    /// <item>folgas_maximas_semana = 2 (descanso, dias, Rígida, sem piso/teto legal). Junto com folgas_minimas_semana = 2
    /// dá exatamente 2 folgas por colaborador e semana civil. Feriado sem autorização e ausência (férias, afastamento...)
    /// não são folga: reduzem os dias de trabalho (semana comum = 5 dias; com feriado = 4; com 2 dias de afastamento = 3).
    /// Bloquear a regra volta ao comportamento anterior (só o limite de dias trabalhados das folgas mínimas).</item>
    /// </list>
    /// </summary>
    public partial class Regra5x2Estrito : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO compliance_rules ("Categoria", "Chave", "Valor", "Unidade", "Descricao", "BaseLegal", "PisoLegal", "TetoLegal",
                    "VigenciaInicio", "VigenciaFim", "Status", "Aplicacao", "AtualizadoPor", "AtualizadoEm")
                SELECT 'descanso', 'folgas_maximas_semana', 2, 'dias',
                       'Folgas máximas por semana (escala 5x2: com as folgas mínimas, exatamente 2 folgas; feriado e afastamento não contam como folga e reduzem os dias de trabalho)',
                       'CLT art. 58 (jornada) e art. 67 (descanso semanal); EC 141/2026 (carga semanal); contrato 5x2 / política interna — o máximo de folgas não é limite legal: garante os 5 dias de trabalho do contrato',
                       NULL, NULL, NULL, NULL, 'Ativo', 'Rigida', 'seed', now()
                WHERE NOT EXISTS (SELECT 1 FROM compliance_rules r WHERE r."Chave" = 'folgas_maximas_semana');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM compliance_rules WHERE "AtualizadoPor" = 'seed' AND "Chave" = 'folgas_maximas_semana';
                """);
        }
    }
}
