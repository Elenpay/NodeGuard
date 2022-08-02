﻿using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundsManager.Migrations
{
    public partial class missingOverhaul : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OperationRequestPsbts_AspNetUsers_UserSignerId",
                table: "OperationRequestPsbts");

            migrationBuilder.DropForeignKey(
                name: "FK_OperationRequestPsbts_ChannelOperationRequests_ChannelOpera~",
                table: "OperationRequestPsbts");

            migrationBuilder.DropPrimaryKey(
                name: "PK_OperationRequestPsbts",
                table: "OperationRequestPsbts");

            migrationBuilder.RenameTable(
                name: "OperationRequestPsbts",
                newName: "ChannelOperationRequestPSBTs");

            migrationBuilder.RenameIndex(
                name: "IX_OperationRequestPsbts_UserSignerId",
                table: "ChannelOperationRequestPSBTs",
                newName: "IX_ChannelOperationRequestPSBTs_UserSignerId");

            migrationBuilder.RenameIndex(
                name: "IX_OperationRequestPsbts_ChannelOperationRequestId",
                table: "ChannelOperationRequestPSBTs",
                newName: "IX_ChannelOperationRequestPSBTs_ChannelOperationRequestId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ChannelOperationRequestPSBTs",
                table: "ChannelOperationRequestPSBTs",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ChannelOperationRequestPSBTs_AspNetUsers_UserSignerId",
                table: "ChannelOperationRequestPSBTs",
                column: "UserSignerId",
                principalTable: "AspNetUsers",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ChannelOperationRequestPSBTs_ChannelOperationRequests_Chann~",
                table: "ChannelOperationRequestPSBTs",
                column: "ChannelOperationRequestId",
                principalTable: "ChannelOperationRequests",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChannelOperationRequestPSBTs_AspNetUsers_UserSignerId",
                table: "ChannelOperationRequestPSBTs");

            migrationBuilder.DropForeignKey(
                name: "FK_ChannelOperationRequestPSBTs_ChannelOperationRequests_Chann~",
                table: "ChannelOperationRequestPSBTs");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ChannelOperationRequestPSBTs",
                table: "ChannelOperationRequestPSBTs");

            migrationBuilder.RenameTable(
                name: "ChannelOperationRequestPSBTs",
                newName: "OperationRequestPsbts");

            migrationBuilder.RenameIndex(
                name: "IX_ChannelOperationRequestPSBTs_UserSignerId",
                table: "OperationRequestPsbts",
                newName: "IX_OperationRequestPsbts_UserSignerId");

            migrationBuilder.RenameIndex(
                name: "IX_ChannelOperationRequestPSBTs_ChannelOperationRequestId",
                table: "OperationRequestPsbts",
                newName: "IX_OperationRequestPsbts_ChannelOperationRequestId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_OperationRequestPsbts",
                table: "OperationRequestPsbts",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_OperationRequestPsbts_AspNetUsers_UserSignerId",
                table: "OperationRequestPsbts",
                column: "UserSignerId",
                principalTable: "AspNetUsers",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_OperationRequestPsbts_ChannelOperationRequests_ChannelOpera~",
                table: "OperationRequestPsbts",
                column: "ChannelOperationRequestId",
                principalTable: "ChannelOperationRequests",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
