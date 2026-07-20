using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SterlingLams.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddWhatsAppOptIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Default TRUE so existing/in-flight orders (placed before the checkbox existed) and POS
            // orders keep notifying — matches the model initializer and the opt-out UX. EF generated
            // defaultValue:false from the CLR default; changed by hand.
            migrationBuilder.AddColumn<bool>(
                name: "WhatsAppOptIn",
                table: "Orders",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WhatsAppOptIn",
                table: "Orders");
        }
    }
}
