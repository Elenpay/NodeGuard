// NodeGuard
// Copyright (C) 2025  Elenpay
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY, without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see http://www.gnu.org/licenses/.

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodeGuard.Migrations
{
    /// <inheritdoc />
    public partial class WithdrawalFeeRate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "MempoolRecommendedFeesTypes",
                table: "ChannelOperationRequests",
                newName: "MempoolRecommendedFeesType");

            migrationBuilder.AddColumn<decimal>(
                name: "CustomFeeRate",
                table: "WalletWithdrawalRequests",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MempoolRecommendedFeesType",
                table: "WalletWithdrawalRequests",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CustomFeeRate",
                table: "WalletWithdrawalRequests");

            migrationBuilder.DropColumn(
                name: "MempoolRecommendedFeesType",
                table: "WalletWithdrawalRequests");

            migrationBuilder.RenameColumn(
                name: "MempoolRecommendedFeesType",
                table: "ChannelOperationRequests",
                newName: "MempoolRecommendedFeesTypes");
        }
    }
}
