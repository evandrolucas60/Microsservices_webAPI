using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Compliance.Api.Migrations
{
    /// <summary>
    /// Semeia (se ainda não existir) max_folgas_seguidas = 2 (descanso, dias, Rígida, ATIVA, sem vigência): no máximo 2
    /// folgas seguidas — folgas de preferência intercaladas, no máximo duas juntas —, inclusive na virada de uma semana
    /// para a outra e na emenda com a escala anterior/seguinte (evita 3 e 4 folgas seguidas). Feriado sem autorização e
    /// ausência (férias, afastamento, atestado, indisponibilidade) não são folga e interrompem a contagem. Política interna
    /// (não é exigência legal); editável/bloqueável pelo superAdmin como as demais (valor ≥ 1). Compatível com
    /// folgas_consecutivas_5x2 (o par exigido tem 2 dias).
    /// </summary>
    public partial class RegraMaxFolgasSeguidas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO compliance_rules ("Categoria", "Chave", "Valor", "Unidade", "Descricao", "BaseLegal", "PisoLegal", "TetoLegal",
                    "VigenciaInicio", "VigenciaFim", "Status", "Aplicacao", "AtualizadoPor", "AtualizadoEm")
                SELECT 'descanso', 'max_folgas_seguidas', 2, 'dias',
                       'Máximo de folgas seguidas (folgas de preferência intercaladas, no máximo 2 juntas, inclusive na virada da semana e na emenda com a escala anterior/seguinte; feriado e afastamento não contam como folga)',
                       'Política interna (não é exigência legal; CLT art. 67 exige o descanso semanal, não limita folgas seguidas)',
                       NULL, NULL, NULL, NULL, 'Ativo', 'Rigida', 'seed', now()
                WHERE NOT EXISTS (SELECT 1 FROM compliance_rules r WHERE r."Chave" = 'max_folgas_seguidas');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM compliance_rules WHERE "AtualizadoPor" = 'seed' AND "Chave" = 'max_folgas_seguidas';
                """);
        }
    }
}
