using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HRMS.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SecureWorkflowConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Payslips_EmployeeId_Month_Year",
                table: "Payslips",
                columns: new[] { "EmployeeId", "Month", "Year" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Attendance_EmployeeId",
                table: "Attendance",
                column: "EmployeeId",
                unique: true,
                filter: "\"CheckOut\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payslips_EmployeeId_Month_Year",
                table: "Payslips");

            migrationBuilder.DropIndex(
                name: "IX_Attendance_EmployeeId",
                table: "Attendance");
        }
    }
}
