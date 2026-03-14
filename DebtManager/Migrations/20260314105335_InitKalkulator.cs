using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DebtManager.Migrations
{
    /// <inheritdoc />
    public partial class InitKalkulator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Dlugi",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Nazwa = table.Column<string>(type: "TEXT", nullable: false),
                    WartoscPoczatkowa = table.Column<decimal>(type: "TEXT", nullable: false),
                    DataPierwszejRaty = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DzienRaty = table.Column<int>(type: "INTEGER", nullable: false),
                    Czestotliwosc = table.Column<int>(type: "INTEGER", nullable: false),
                    InnaCzestotliwoscDni = table.Column<int>(type: "INTEGER", nullable: false),
                    Oprocentowanie = table.Column<decimal>(type: "TEXT", nullable: false),
                    TypRat = table.Column<int>(type: "INTEGER", nullable: false),
                    WyrownanieWPierwszejRacie = table.Column<bool>(type: "INTEGER", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    ExchangeRate = table.Column<decimal>(type: "decimal(18, 4)", nullable: false),
                    LiczbaRat = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dlugi", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Nadplaty",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DlugId = table.Column<int>(type: "INTEGER", nullable: false),
                    Data = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Kwota = table.Column<decimal>(type: "TEXT", nullable: false),
                    Typ = table.Column<int>(type: "INTEGER", nullable: false),
                    Efekt = table.Column<int>(type: "INTEGER", nullable: false),
                    CzescOdsetkowa = table.Column<decimal>(type: "TEXT", nullable: false),
                    CzyEdytowanaRecznie = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Nadplaty", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Nadplaty_Dlugi_DlugId",
                        column: x => x.DlugId,
                        principalTable: "Dlugi",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Raty",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DlugId = table.Column<int>(type: "INTEGER", nullable: false),
                    NumerRaty = table.Column<int>(type: "INTEGER", nullable: false),
                    DataRaty = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Kapital = table.Column<decimal>(type: "TEXT", nullable: false),
                    Odsetki = table.Column<decimal>(type: "TEXT", nullable: false),
                    PozostaloKapitalu = table.Column<decimal>(type: "TEXT", nullable: false),
                    OprocentowanieRaty = table.Column<decimal>(type: "TEXT", nullable: false),
                    CzyEdytowanaRecznie = table.Column<bool>(type: "INTEGER", nullable: false),
                    CzyOplacona = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Raty", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Raty_Dlugi_DlugId",
                        column: x => x.DlugId,
                        principalTable: "Dlugi",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ZmianyOprocentowania",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DlugId = table.Column<int>(type: "INTEGER", nullable: false),
                    DataZmiany = table.Column<DateTime>(type: "TEXT", nullable: false),
                    NoweOprocentowanie = table.Column<decimal>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ZmianyOprocentowania", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ZmianyOprocentowania_Dlugi_DlugId",
                        column: x => x.DlugId,
                        principalTable: "Dlugi",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Nadplaty_DlugId",
                table: "Nadplaty",
                column: "DlugId");

            migrationBuilder.CreateIndex(
                name: "IX_Raty_DlugId",
                table: "Raty",
                column: "DlugId");

            migrationBuilder.CreateIndex(
                name: "IX_ZmianyOprocentowania_DlugId",
                table: "ZmianyOprocentowania",
                column: "DlugId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Nadplaty");

            migrationBuilder.DropTable(
                name: "Raty");

            migrationBuilder.DropTable(
                name: "ZmianyOprocentowania");

            migrationBuilder.DropTable(
                name: "Dlugi");
        }
    }
}
