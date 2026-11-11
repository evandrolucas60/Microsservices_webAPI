using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Compliance.Api.Migrations
{
    /// <summary>
    /// Semeia (se ainda não existir) folgas_consecutivas_5x2 = 1 (regime, booleano, Rígida) BLOQUEADA: na semana 5x2 as
    /// duas folgas em dias seguidos da semana civil. É política interna (não é exigência legal): o superAdmin ativa
    /// quando a política exigir. No 6x1 (uma folga) não se aplica; semana com menos de 2 folgas não é verificada.
    /// </summary>
    public partial class RegraFolgasConsecutivas5x2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO compliance_rules ("Categoria", "Chave", "Valor", "Unidade", "Descricao", "BaseLegal", "PisoLegal", "TetoLegal",
                    "VigenciaInicio", "VigenciaFim", "Status", "Aplicacao", "AtualizadoPor", "AtualizadoEm")
                SELECT 'regime', 'folgas_consecutivas_5x2', 1, 'booleano',
                       'Folgas consecutivas no 5x2 (1 = as duas folgas da semana 5x2 em dias seguidos)',
                       'Política interna (não é exigência legal; CLT art. 67 exige o descanso semanal, não folgas seguidas)',
                       NULL, NULL, NULL, NULL, 'Bloqueado', 'Rigida', 'seed', now()
                WHERE NOT EXISTS (SELECT 1 FROM compliance_rules r WHERE r."Chave" = 'folgas_consecutivas_5x2');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM compliance_rules WHERE "AtualizadoPor" = 'seed' AND "Chave" = 'folgas_consecutivas_5x2';
                """);
        }
    }
}
