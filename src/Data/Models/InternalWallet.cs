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

public required string DerivationPath { get; set; }

/// <summary>
/// 24 Words mnemonic
/// </summary>
public string? MnemonicString { get; set; }

/// <summary>
/// XPUB in the case the Mnemonic is not set (Remote signer mode)
/// </summary>
public string? XPUB
{
    get => GetXPUB();
    set => _xpub = value;
}
private string? _xpub;

public string? MasterFingerprint
{
    get => GetMasterFingerprint();
    set => _masterFingerprint = value;
}
private string? _masterFingerprint;
