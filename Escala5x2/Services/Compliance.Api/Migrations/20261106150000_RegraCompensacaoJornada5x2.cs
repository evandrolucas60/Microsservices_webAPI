using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Compliance.Api.Migrations
{
    /// <summary>
    /// Semeia (se ainda não existir) compensacao_jornada_5x2 = 1 (jornada, booleano, Rígida, ATIVA, sem vigência): na semana
    /// 5x2 a jornada diária pode passar do limite_diario_minutos até carga_horaria_semanal ÷ dias de trabalho do regime
    /// (44h ÷ 5 = 8h48; 42h ÷ 5 = 8h24; 40h ÷ 5 = 8h) — acordo de compensação de horas da semana (o sábado não trabalhado
    /// é compensado nos outros dias), sempre dentro da carga semanal e no máximo 10h por dia (limite diário + hora extra
    /// máxima). Caso relatado em 06/11/2026: a escala de 44h no 5x2 (8h48 pagas) era recusada contra o limite diário de 8h
    /// ("nenhum turno cabe no expediente"). O superAdmin bloqueia a regra quando a empresa não tiver o acordo de
    /// compensação (volta a valer só o limite diário).
    /// </summary>
    public partial class RegraCompensacaoJornada5x2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO compliance_rules ("Categoria", "Chave", "Valor", "Unidade", "Descricao", "BaseLegal", "PisoLegal", "TetoLegal",
                    "VigenciaInicio", "VigenciaFim", "Status", "Aplicacao", "AtualizadoPor", "AtualizadoEm")
                SELECT 'jornada', 'compensacao_jornada_5x2', 1, 'booleano',
                       'Compensação semanal no 5x2: na semana 5x2 o limite diário passa a ser a carga semanal vigente ÷ dias de trabalho do regime (44h → 8h48, 42h → 8h24), dentro da carga semanal e no máximo limite diário + hora extra máxima (10h). Bloqueada: vale só o limite diário',
                       'CLT art. 59, §2º (acordo de compensação de horas, limite de 10h diárias); TST Súmula 85',
                       NULL, NULL, NULL, NULL, 'Ativo', 'Rigida', 'seed', now()
                WHERE NOT EXISTS (SELECT 1 FROM compliance_rules r WHERE r."Chave" = 'compensacao_jornada_5x2');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM compliance_rules WHERE "AtualizadoPor" = 'seed' AND "Chave" = 'compensacao_jornada_5x2';
                """);
        }
    }
}
