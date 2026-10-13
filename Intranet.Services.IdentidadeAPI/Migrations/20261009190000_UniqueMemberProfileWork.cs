using Intranet.Services.IdentidadeAPI.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Intranet.Services.IdentidadeAPI.Migrations
{
    /// <summary>
    /// Índices únicos: Members.UserId, Profiles.MemberId e Works.MemberId.
    /// Antes de criá-los, mescla duplicatas existentes (geradas por requisições simultâneas)
    /// sem descartar dados: filhos dos registros duplicados são movidos para o registro mantido.
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261009190000_UniqueMemberProfileWork")]
    public partial class UniqueMemberProfileWork : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) Members duplicados por UserId: mantém o mais antigo e move perfis/trabalhos/endereço
            migrationBuilder.Sql("""
                DROP TABLE IF EXISTS _member_map;
                CREATE TEMP TABLE _member_map AS
                SELECT dup_id, keep_id FROM (
                    SELECT "MemberId" AS dup_id,
                           first_value("MemberId") OVER w AS keep_id,
                           row_number() OVER w AS rn
                    FROM "Members"
                    WHERE "UserId" IS NOT NULL
                    WINDOW w AS (PARTITION BY "UserId" ORDER BY "dateOfMembership", "MemberId")
                ) t
                WHERE rn > 1;

                UPDATE "Profiles" p SET "MemberId" = m.keep_id
                FROM _member_map m WHERE p."MemberId" = m.dup_id;

                UPDATE "Works" w SET "MemberId" = m.keep_id
                FROM _member_map m WHERE w."MemberId" = m.dup_id;

                -- Endereço (um por membro): se o mantido não tem, aproveita um dos duplicados
                UPDATE "Addresses" a SET "MemberId" = m.keep_id
                FROM _member_map m
                WHERE a."MemberId" = m.dup_id
                  AND NOT EXISTS (SELECT 1 FROM "Addresses" k WHERE k."MemberId" = m.keep_id)
                  AND a."AddressId" = (
                      SELECT min(a2."AddressId")
                      FROM "Addresses" a2
                      JOIN _member_map m2 ON a2."MemberId" = m2.dup_id
                      WHERE m2.keep_id = m.keep_id);

                DELETE FROM "Addresses" a USING _member_map m WHERE a."MemberId" = m.dup_id;
                DELETE FROM "Members" x USING _member_map m WHERE x."MemberId" = m.dup_id;
                DROP TABLE _member_map;
                """);

            // 2) Profiles duplicados por MemberId: mantém o que tem foto/capa e move as formações
            migrationBuilder.Sql("""
                DROP TABLE IF EXISTS _profile_map;
                CREATE TEMP TABLE _profile_map AS
                SELECT dup_id, keep_id FROM (
                    SELECT "ProfileId" AS dup_id,
                           first_value("ProfileId") OVER w AS keep_id,
                           row_number() OVER w AS rn
                    FROM "Profiles"
                    WINDOW w AS (PARTITION BY "MemberId"
                                 ORDER BY ("ProfilePicture" IS NULL), ("CoverPhoto" IS NULL), "ProfileId")
                ) t
                WHERE rn > 1;

                UPDATE "Educations" e SET "ProfileId" = pm.keep_id
                FROM _profile_map pm WHERE e."ProfileId" = pm.dup_id;

                DELETE FROM "Profiles" p USING _profile_map pm WHERE p."ProfileId" = pm.dup_id;
                DROP TABLE _profile_map;
                """);

            // 3) Works duplicados por MemberId: mantém o referenciado pelo perfil (ou o mais preenchido)
            //    e reaponta Profiles.WorkId antes de excluir (a FK é ON DELETE CASCADE)
            migrationBuilder.Sql("""
                DROP TABLE IF EXISTS _work_map;
                CREATE TEMP TABLE _work_map AS
                SELECT dup_id, keep_id FROM (
                    SELECT w."WorkId" AS dup_id,
                           first_value(w."WorkId") OVER win AS keep_id,
                           row_number() OVER win AS rn
                    FROM (
                        SELECT w0.*,
                               EXISTS (SELECT 1 FROM "Profiles" p WHERE p."WorkId" = w0."WorkId") AS referenced
                        FROM "Works" w0
                    ) w
                    WINDOW win AS (PARTITION BY w."MemberId"
                                   ORDER BY w.referenced DESC, (w."AdmissionDate" IS NULL), (w."Title" = ''), w."WorkId")
                ) t
                WHERE rn > 1;

                UPDATE "Profiles" p SET "WorkId" = wm.keep_id
                FROM _work_map wm WHERE p."WorkId" = wm.dup_id;

                DELETE FROM "Works" w USING _work_map wm WHERE w."WorkId" = wm.dup_id;
                DROP TABLE _work_map;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Members_UserId",
                table: "Members",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Profiles_MemberId",
                table: "Profiles",
                column: "MemberId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Works_MemberId",
                table: "Works",
                column: "MemberId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_Members_UserId", table: "Members");
            migrationBuilder.DropIndex(name: "IX_Profiles_MemberId", table: "Profiles");
            migrationBuilder.DropIndex(name: "IX_Works_MemberId", table: "Works");
        }
    }
}
