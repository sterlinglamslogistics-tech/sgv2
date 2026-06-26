using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SterlingLams.Web.Migrations
{
    /// <inheritdoc />
    public partial class OrderNumberSequence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Shared counter for short, sequential order numbers (SL-30012 / POS-30013, …).
            // First value handed out is 30001. Postgres only — the SQLite test harness skips this.
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql") == true)
                migrationBuilder.Sql("CREATE SEQUENCE IF NOT EXISTS order_number_seq START WITH 30001 INCREMENT BY 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql") == true)
                migrationBuilder.Sql("DROP SEQUENCE IF EXISTS order_number_seq;");
        }
    }
}
