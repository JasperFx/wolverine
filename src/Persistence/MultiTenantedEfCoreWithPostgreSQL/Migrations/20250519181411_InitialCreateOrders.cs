using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MultiTenantedEfCoreWithPostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreateOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "mt_orders");

            migrationBuilder.CreateTable(
                name: "orders",
                schema: "mt_orders",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    OrderStatus = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_orders", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "orders",
                schema: "mt_orders");
        }
    }
}
