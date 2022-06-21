using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FundsManager.Migrations
{
    public partial class AddedRelationships : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Key_AspNetUsers_UserId",
                table: "Key");

            migrationBuilder.DropForeignKey(
                name: "FK_Wallet_AspNetUsers_UserId",
                table: "Wallet");

            migrationBuilder.DropTable(
                name: "ChannelOpenRequest");

            migrationBuilder.DropTable(
                name: "ChannelOpenRequestSignature");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Wallet",
                table: "Wallet");

            migrationBuilder.DropIndex(
                name: "IX_Wallet_UserId",
                table: "Wallet");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Node",
                table: "Node");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Key",
                table: "Key");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "Wallet");

            migrationBuilder.RenameTable(
                name: "Wallet",
                newName: "Wallets");

            migrationBuilder.RenameTable(
                name: "Node",
                newName: "Nodes");

            migrationBuilder.RenameTable(
                name: "Key",
                newName: "Keys");

            migrationBuilder.RenameIndex(
                name: "IX_Key_UserId",
                table: "Keys",
                newName: "IX_Keys_UserId");

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "Nodes",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "Nodes",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "WalletId",
                table: "Keys",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WalletId1",
                table: "Keys",
                type: "integer",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_Wallets",
                table: "Wallets",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Nodes",
                table: "Nodes",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Keys",
                table: "Keys",
                column: "Id");

            migrationBuilder.CreateTable(
                name: "ApplicationUserNode",
                columns: table => new
                {
                    NodesId = table.Column<int>(type: "integer", nullable: false),
                    UsersId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationUserNode", x => new { x.NodesId, x.UsersId });
                    table.ForeignKey(
                        name: "FK_ApplicationUserNode_AspNetUsers_UsersId",
                        column: x => x.UsersId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ApplicationUserNode_Nodes_NodesId",
                        column: x => x.NodesId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Channels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ChannelPoint = table.Column<string>(type: "text", nullable: false),
                    ChannelId = table.Column<string>(type: "text", nullable: false),
                    Capacity = table.Column<string>(type: "text", nullable: false),
                    CreationDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdateDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Channels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChannelOperationRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Amount = table.Column<decimal>(type: "numeric", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    AmountCryptoUnit = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    RequestType = table.Column<int>(type: "integer", nullable: false),
                    WalletId = table.Column<string>(type: "text", nullable: true),
                    WalletId1 = table.Column<int>(type: "integer", nullable: true),
                    SourceNodeId = table.Column<int>(type: "integer", nullable: false),
                    DestNodeId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    ChannelId = table.Column<string>(type: "text", nullable: true),
                    ChannelId1 = table.Column<int>(type: "integer", nullable: true),
                    CreationDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdateDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelOperationRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChannelOperationRequests_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChannelOperationRequests_Channels_ChannelId1",
                        column: x => x.ChannelId1,
                        principalTable: "Channels",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ChannelOperationRequests_Nodes_DestNodeId",
                        column: x => x.DestNodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChannelOperationRequests_Nodes_SourceNodeId",
                        column: x => x.SourceNodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChannelOperationRequests_Wallets_WalletId1",
                        column: x => x.WalletId1,
                        principalTable: "Wallets",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ChannelOperationRequestSignatures",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PSBT = table.Column<string>(type: "text", nullable: false),
                    SignatureContent = table.Column<string>(type: "text", nullable: false),
                    ChannelOpenRequestId = table.Column<string>(type: "text", nullable: true),
                    ChannelOperationRequestId = table.Column<int>(type: "integer", nullable: true),
                    CreationDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdateDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelOperationRequestSignatures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChannelOperationRequestSignatures_ChannelOperationRequests_~",
                        column: x => x.ChannelOperationRequestId,
                        principalTable: "ChannelOperationRequests",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Keys_WalletId1",
                table: "Keys",
                column: "WalletId1");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationUserNode_UsersId",
                table: "ApplicationUserNode",
                column: "UsersId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelOperationRequests_ChannelId1",
                table: "ChannelOperationRequests",
                column: "ChannelId1");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelOperationRequests_DestNodeId",
                table: "ChannelOperationRequests",
                column: "DestNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelOperationRequests_SourceNodeId",
                table: "ChannelOperationRequests",
                column: "SourceNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelOperationRequests_UserId",
                table: "ChannelOperationRequests",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelOperationRequests_WalletId1",
                table: "ChannelOperationRequests",
                column: "WalletId1");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelOperationRequestSignatures_ChannelOperationRequestId",
                table: "ChannelOperationRequestSignatures",
                column: "ChannelOperationRequestId");

            migrationBuilder.AddForeignKey(
                name: "FK_Keys_AspNetUsers_UserId",
                table: "Keys",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Keys_Wallets_WalletId1",
                table: "Keys",
                column: "WalletId1",
                principalTable: "Wallets",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Keys_AspNetUsers_UserId",
                table: "Keys");

            migrationBuilder.DropForeignKey(
                name: "FK_Keys_Wallets_WalletId1",
                table: "Keys");

            migrationBuilder.DropTable(
                name: "ApplicationUserNode");

            migrationBuilder.DropTable(
                name: "ChannelOperationRequestSignatures");

            migrationBuilder.DropTable(
                name: "ChannelOperationRequests");

            migrationBuilder.DropTable(
                name: "Channels");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Wallets",
                table: "Wallets");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Nodes",
                table: "Nodes");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Keys",
                table: "Keys");

            migrationBuilder.DropIndex(
                name: "IX_Keys_WalletId1",
                table: "Keys");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "WalletId",
                table: "Keys");

            migrationBuilder.DropColumn(
                name: "WalletId1",
                table: "Keys");

            migrationBuilder.RenameTable(
                name: "Wallets",
                newName: "Wallet");

            migrationBuilder.RenameTable(
                name: "Nodes",
                newName: "Node");

            migrationBuilder.RenameTable(
                name: "Keys",
                newName: "Key");

            migrationBuilder.RenameIndex(
                name: "IX_Keys_UserId",
                table: "Key",
                newName: "IX_Key_UserId");

            migrationBuilder.AddColumn<string>(
                name: "UserId",
                table: "Wallet",
                type: "text",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_Wallet",
                table: "Wallet",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Node",
                table: "Node",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Key",
                table: "Key",
                column: "Id");

            migrationBuilder.CreateTable(
                name: "ChannelOpenRequest",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Amount = table.Column<decimal>(type: "numeric", nullable: false),
                    AmountCryptoUnit = table.Column<string>(type: "text", nullable: false),
                    CreationDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    UpdateDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelOpenRequest", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChannelOpenRequestSignature",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CreationDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PSBT = table.Column<string>(type: "text", nullable: false),
                    SignatureContent = table.Column<string>(type: "text", nullable: false),
                    UpdateDatetime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelOpenRequestSignature", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Wallet_UserId",
                table: "Wallet",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Key_AspNetUsers_UserId",
                table: "Key",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Wallet_AspNetUsers_UserId",
                table: "Wallet",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id");
        }
    }
}
