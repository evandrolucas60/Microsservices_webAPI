using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Store.Api.Migrations
{
    /// <summary>
    /// Feriados por abrangência (Nacional/Estadual/Municipal), recorrência e autoria.
    /// <list type="bullet">
    /// <item>Feriados sem loja (StoreId nulo) viram Nacional.</item>
    /// <item>Feriados ligados a uma loja viram Municipal com a cidade/UF da loja. Se a loja não tiver cidade, o feriado
    /// fica Municipal com município vazio: não vale para nenhuma loja e aparece na lista do Admin para correção.</item>
    /// <item>StoreId deixa de existir (feriado é definido por lei federal, estadual ou municipal, não por loja).</item>
    /// <item>Semeia os feriados nacionais fixos (Lei 662/1949 com a redação da Lei 10.607/2002, Lei 6.802/1980 e
    /// Lei 14.759/2023) como recorrentes, só os que ainda não existem; um nacional já cadastrado no mesmo dia/mês
    /// passa a ser recorrente. Feriados móveis (Carnaval, Sexta-feira Santa, Corpus Christi) não são semeados:
    /// são cadastrados ano a ano.</item>
    /// </list>
    /// </summary>
    public partial class HolidayScopes : Migration
    {
        // Mesma normalização do HolidayCalendar.Normalize (sem acentos, maiúsculas, espaços simples) para os acentos do português
        private const string Normalize =
            "upper(translate(regexp_replace(btrim({0}), '\\s+', ' ', 'g'), " +
            "'áàâãäéèêëíìîïóòôõöúùûüçÁÀÂÃÄÉÈÊËÍÌÎÏÓÒÔÕÖÚÙÛÜÇ', 'aaaaaeeeeiiiiooooouuuucAAAAAEEEEIIIIOOOOOUUUUC'))";

        private static readonly (int Month, int Day, string Name, int BaseYear, string Lei)[] Nacionais =
        [
            (1, 1, "Confraternização Universal", 2002, "Lei 662/1949 (red. Lei 10.607/2002)"),
            (4, 21, "Tiradentes", 2002, "Lei 662/1949 (red. Lei 10.607/2002)"),
            (5, 1, "Dia do Trabalho", 2002, "Lei 662/1949 (red. Lei 10.607/2002)"),
            (9, 7, "Independência do Brasil", 2002, "Lei 662/1949 (red. Lei 10.607/2002)"),
            (10, 12, "Nossa Senhora Aparecida", 1980, "Lei 6.802/1980"),
            (11, 2, "Finados", 2002, "Lei 662/1949 (red. Lei 10.607/2002)"),
            (11, 15, "Proclamação da República", 2002, "Lei 662/1949 (red. Lei 10.607/2002)"),
            (11, 20, "Dia Nacional de Zumbi e da Consciência Negra", 2024, "Lei 14.759/2023"),
            (12, 25, "Natal", 2002, "Lei 662/1949 (red. Lei 10.607/2002)"),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "Abrangencia", table: "holidays", type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "");
            migrationBuilder.AddColumn<string>(name: "Uf", table: "holidays", type: "character varying(2)", maxLength: 2, nullable: true);
            migrationBuilder.AddColumn<string>(name: "Municipio", table: "holidays", type: "character varying(120)", maxLength: 120, nullable: true);
            migrationBuilder.AddColumn<string>(name: "LocalChave", table: "holidays", type: "character varying(130)", maxLength: 130, nullable: false, defaultValue: "");
            migrationBuilder.AddColumn<bool>(name: "Recorrente", table: "holidays", type: "boolean", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<string>(name: "CriadoPor", table: "holidays", type: "character varying(200)", maxLength: 200, nullable: false, defaultValue: "");
            migrationBuilder.AddColumn<DateTime>(name: "CriadoEm", table: "holidays", type: "timestamp with time zone", nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));
            migrationBuilder.AddColumn<string>(name: "AtualizadoPor", table: "holidays", type: "character varying(200)", maxLength: 200, nullable: true);
            migrationBuilder.AddColumn<DateTime>(name: "AtualizadoEm", table: "holidays", type: "timestamp with time zone", nullable: true);

            // Dados existentes: sem loja → Nacional; com loja → Municipal da cidade/UF da loja
            migrationBuilder.Sql("""
                UPDATE holidays SET "Abrangencia" = 'Nacional', "LocalChave" = '', "CriadoPor" = 'migração (HolidayScopes)', "CriadoEm" = now()
                WHERE "StoreId" IS NULL;
                """);
            migrationBuilder.Sql($"""
                UPDATE holidays h SET
                    "Abrangencia" = 'Municipal',
                    "Uf" = upper(left(btrim(s."State"), 2)),
                    "Municipio" = NULLIF(left(regexp_replace(btrim(s."City"), '\s+', ' ', 'g'), 120), ''),
                    "LocalChave" = left(upper(left(btrim(s."State"), 2)) || '|' || {string.Format(Normalize, "s.\"City\"")}, 130),
                    "CriadoPor" = 'migração (HolidayScopes): feriado da loja ' || s."Code",
                    "CriadoEm" = now()
                FROM stores s
                WHERE h."StoreId" = s."Id";
                """);
            // Loja apagada (StoreId sem loja): não há como saber o município; fica Municipal sem local para revisão
            migrationBuilder.Sql("""
                UPDATE holidays SET "Abrangencia" = 'Municipal', "LocalChave" = '|', "CriadoPor" = 'migração (HolidayScopes): loja não encontrada', "CriadoEm" = now()
                WHERE "Abrangencia" = '';
                """);

            migrationBuilder.DropIndex(name: "IX_holidays_StoreId_Date", table: "holidays");
            migrationBuilder.DropColumn(name: "StoreId", table: "holidays");

            migrationBuilder.Sql("""UPDATE holidays SET "Name" = left("Name", 150) WHERE length("Name") > 150;""");
            migrationBuilder.AlterColumn<string>(name: "Name", table: "holidays", type: "character varying(150)", maxLength: 150, nullable: false,
                oldClrType: typeof(string), oldType: "text");
            migrationBuilder.Sql("""UPDATE holidays SET "AuthorizationRef" = left("AuthorizationRef", 200) WHERE length("AuthorizationRef") > 200;""");
            migrationBuilder.AlterColumn<string>(name: "AuthorizationRef", table: "holidays", type: "character varying(200)", maxLength: 200, nullable: true,
                oldClrType: typeof(string), oldType: "text", oldNullable: true);

            migrationBuilder.CreateIndex(name: "IX_holidays_Abrangencia_LocalChave_Date", table: "holidays",
                columns: new[] { "Abrangencia", "LocalChave", "Date" });

            // Nacionais fixos: o já cadastrado no mesmo dia/mês vira recorrente; os que faltam são inseridos
            foreach (var (month, day, name, baseYear, lei) in Nacionais)
            {
                migrationBuilder.Sql($"""
                    UPDATE holidays SET "Recorrente" = true, "AtualizadoPor" = 'migração (HolidayScopes): recorrente, {lei}', "AtualizadoEm" = now()
                    WHERE "Id" = (SELECT "Id" FROM holidays WHERE "Abrangencia" = 'Nacional'
                                  AND extract(month FROM "Date") = {month} AND extract(day FROM "Date") = {day}
                                  ORDER BY "Recorrente" DESC, "Date" LIMIT 1)
                      AND NOT "Recorrente";
                    """);
                migrationBuilder.Sql($"""
                    INSERT INTO holidays ("Id", "Abrangencia", "Uf", "Municipio", "LocalChave", "Date", "Name", "Recorrente", "WorkAuthorized", "AuthorizationRef", "CriadoPor", "CriadoEm")
                    SELECT md5(random()::text || clock_timestamp()::text || '{month}-{day}')::uuid, 'Nacional', NULL, NULL, '',
                           DATE '{baseYear:0000}-{month:00}-{day:00}', '{name}', true, false, NULL, 'seed (HolidayScopes): {lei}', now()
                    WHERE NOT EXISTS (SELECT 1 FROM holidays WHERE "Abrangencia" = 'Nacional'
                                      AND extract(month FROM "Date") = {month} AND extract(day FROM "Date") = {day});
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Volta ao modelo antigo (StoreId): remove os nacionais semeados; municipal → feriado da primeira loja do município;
            // estaduais e municipais sem loja correspondente são perdidos (o modelo antigo não os representa).
            migrationBuilder.Sql("""DELETE FROM holidays WHERE "CriadoPor" LIKE 'seed (HolidayScopes)%';""");
            migrationBuilder.AddColumn<Guid>(name: "StoreId", table: "holidays", type: "uuid", nullable: true);
            migrationBuilder.Sql($"""
                UPDATE holidays h SET "StoreId" = (
                    SELECT s."Id" FROM stores s
                    WHERE upper(left(btrim(s."State"), 2)) || '|' || {string.Format(Normalize, "s.\"City\"")} = h."LocalChave"
                    ORDER BY s."Code" LIMIT 1)
                WHERE h."Abrangencia" = 'Municipal';
                """);
            migrationBuilder.Sql("""DELETE FROM holidays WHERE "Abrangencia" = 'Estadual' OR ("Abrangencia" = 'Municipal' AND "StoreId" IS NULL);""");

            migrationBuilder.DropIndex(name: "IX_holidays_Abrangencia_LocalChave_Date", table: "holidays");
            migrationBuilder.DropColumn(name: "Abrangencia", table: "holidays");
            migrationBuilder.DropColumn(name: "AtualizadoEm", table: "holidays");
            migrationBuilder.DropColumn(name: "AtualizadoPor", table: "holidays");
            migrationBuilder.DropColumn(name: "CriadoEm", table: "holidays");
            migrationBuilder.DropColumn(name: "CriadoPor", table: "holidays");
            migrationBuilder.DropColumn(name: "LocalChave", table: "holidays");
            migrationBuilder.DropColumn(name: "Municipio", table: "holidays");
            migrationBuilder.DropColumn(name: "Recorrente", table: "holidays");
            migrationBuilder.DropColumn(name: "Uf", table: "holidays");

            migrationBuilder.AlterColumn<string>(name: "Name", table: "holidays", type: "text", nullable: false,
                oldClrType: typeof(string), oldType: "character varying(150)", oldMaxLength: 150);
            migrationBuilder.AlterColumn<string>(name: "AuthorizationRef", table: "holidays", type: "text", nullable: true,
                oldClrType: typeof(string), oldType: "character varying(200)", oldMaxLength: 200, oldNullable: true);

            migrationBuilder.CreateIndex(name: "IX_holidays_StoreId_Date", table: "holidays", columns: new[] { "StoreId", "Date" });
        }
    }
}
