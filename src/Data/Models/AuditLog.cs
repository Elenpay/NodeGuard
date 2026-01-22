/*
 * NodeGuard
 * Copyright (C) 2023  Elenpay
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see http://www.gnu.org/licenses/.
 *
 */

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NodeGuard.Data.Models;

/// <summary>
/// Represents an audit log entry for tracking user actions and system events
/// </summary>
public class AuditLog
{
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>
    /// Timestamp when the audit event occurred
    /// </summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// The type of action that was performed
    /// </summary>
    public AuditActionType ActionType { get; set; }

    /// <summary>
    /// The result/outcome of the action
    /// </summary>
    public AuditEventType EventType { get; set; }

    /// <summary>
    /// The ID of the user who performed the action (nullable for system actions)
    /// </summary>
    [MaxLength(450)]
    public string? UserId { get; set; }

    /// <summary>
    /// The username of the user who performed the action
    /// </summary>
    [MaxLength(256)]
    public string? Username { get; set; }

    /// <summary>
    /// The IP address from which the action was performed
    /// </summary>
    [MaxLength(45)]
    public string? IpAddress { get; set; }

    /// <summary>
    /// The type of object that was affected by the action
    /// </summary>
    public AuditObjectType ObjectAffected { get; set; }

    /// <summary>
    /// The ID of the object that was affected (e.g., wallet ID, channel ID)
    /// </summary>
    [MaxLength(450)]
    public string? ObjectId { get; set; }

    /// <summary>
    /// Additional details about the action in JSON format
    /// </summary>
    public string? Details { get; set; }
}

/// <summary>
/// Types of actions that can be audited
/// </summary>
public enum AuditActionType
{
    // CRUD Operations
    Create,
    Update,
    Delete,

    // Approval/Rejection
    Approve,
    Reject,
    Cancel,

    // Authentication
    Login,
    Logout,
    TwoFactorLogin,
    LoginWithRecoveryCode,

    // 2FA Management
    TwoFactorEnabled,
    TwoFactorDisabled,
    TwoFactorReset,
    GenerateRecoveryCodes,

    // Password Management
    ChangePassword,
    SetPassword,
    ResetPassword,

    // User Management
    LockUser,
    UnlockUser,
    UpdateRoles,

    // Wallet Operations
    Transfer,
    Import,
    Finalise,
    Rescan,
    FreezeUTXO,
    UnfreezeUTXO,
    AddKey,

    // Channel Operations
    Close,
    ForceClose,
    MarkAsClosed,
    EnableLiquidityManagement,
    DisableLiquidityManagement,

    // API Token Operations
    Block,
    Unblock,

    // Swap Operations
    SwapOut,
    SwapIn,
    SwapOutInitiated,
    SwapOutCompleted,

    // Wallet Sweep Operations
    WalletSweep,
    NodeWalletAssigned,

    // Node Operations
    AddNode,
    UpdateNode,
    DeleteNode,

    // Internal Wallet
    GenerateInternalWallet,

    // Withdrawal Operations
    BumpFee,

    // Signing
    Sign
}

/// <summary>
/// Types of event outcomes
/// </summary>
public enum AuditEventType
{
    Success,
    Failure,
    Attempt
}

/// <summary>
/// Types of objects that can be affected by audited actions
/// </summary>
public enum AuditObjectType
{
    User,
    Wallet,
    Channel,
    ChannelOperationRequest,
    WalletWithdrawalRequest,
    Node,
    APIToken,
    SwapOut,
    LiquidityRule,
    Key,
    UTXO,
    InternalWallet,
    Session
}
