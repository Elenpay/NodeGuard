# Outflow Monitor — Design

Design for detecting and alerting on funds leaving NodeGuard wallets that NodeGuard cannot account
for. Status: **proposed**. Implementation is split into three PRs (§8).

---

## 1. Goal

Raise an alert when money leaves a NodeGuard wallet and either:

1. **Unmatched** — the transaction does not correspond to any request in the NodeGuard database, or
2. **Unjustified** — a matching request exists, but it was not created by a logged-in user (UI) or by
   the backend through the authenticated gRPC API.

A "justified" outflow is one that a real actor initiated through a legitimate NodeGuard code path.
Anything else must page someone.

## 2. Scope

**In scope**

- On-chain outflows from **NodeGuard wallets** (multisig, internal, hot, BIP39-imported) tracked by
  NBXplorer.
- Outflow types NodeGuard creates today:
  - `WalletWithdrawalRequest` (UI withdrawals, wallet-to-wallet transfers, fee bumps, gRPC
    withdrawals).
  - `ChannelOperationRequest` of type Open funded from a NodeGuard wallet.

**Out of scope**

- LND on-chain wallets and Lightning off-chain payments.
- Incoming transactions (sweeps, swap-out proceeds, deposits).
- Reverting a transaction once it is broadcast. The monitor (§4–§7) only detects; §11 adds checks
  that block a tampered transaction before it is signed.

## 3. Threat model

| Scenario | Detected by |
|---|---|
| Keys used outside NodeGuard (e.g. signers co-sign a tx directly) | §4 matching — no request |
| Request row inserted/edited directly in the DB | §5 audit cross-check, §6 signatures |
| Request `TxId` edited to point at a rogue tx | §4 output check, §6 TxId signature |
| Request row *and* audit row forged directly in the DB | §6 signatures only |
| Request's tx fee-bumped outside NodeGuard (unsolicited RBF) | §4.4 replacement detection |
| Request backdated to fall under an exemption | No date-based exemptions exist (§5, §6.5) |
| Withdrawal template PSBT outputs edited before anyone signs | §6 template signature; blocked before signing by §11 |
| Change shrunk to inflate the fee | Withdrawals: §6 template signature, §11. Channel opens: §11 fee rate limit |
| Channel-open outputs edited after human approval (SIGHASH_NONE) | §11 checks before the internal wallet signs |
| Stolen API token used to request withdrawals | **Not covered** — the request is created through the legitimate gRPC path and counts as justified |
| Attacker with code execution on the app host | **Not covered** — they can create legitimate-looking requests and get them signed. KMS (§6.6) only stops them from keeping the key after they lose access |

## 4. Detection and matching (PR 2)

### 4.1 Detection

A long-running job, `WalletOutflowSubscriptorJob`, started at boot like `NodeSubscriptorJob`, using
`IDbContextFactory<ApplicationDbContext>`.

- **Startup scan.** For every NodeGuard wallet, `GetTransactionsAsync` from NBXplorer and evaluate
  outgoing transactions within a lookback window (`OUTFLOW_MONITOR_LOOKBACK_DAYS`, default 7).
  This replaces persisting a "last processed event id" and recovers anything missed while the app
  was down. The window must exceed the longest expected downtime (a couple of days); outflows during
  a longer outage are missed silently, so raise the value temporarily before restarting after one.
- **Real time.** Subscribe to NBXplorer `NewTransactionEvent`s and keep only those whose derivation
  strategy belongs to a NodeGuard wallet and that spend at least one of that wallet's inputs.
- **Which wallets.** Every NodeGuard wallet, including archived and compromised ones (funds can
  still leave them). The wallet list is reloaded periodically so wallets created after startup are
  monitored too.
- **Transaction source.** NBXplorer (event or startup scan) only provides the `TxId`. The
  transaction itself (inputs, outputs, amounts) is fetched from bitcoind through NBXplorer's RPC proxy
  (`ExplorerClient.RPCClient`, `getrawtransaction`), so matching runs on what the node actually has
  rather than on NBXplorer's interpretation. Requirements:
  - NBXplorer runs with the RPC proxy enabled (`NBXPLORER_EXPOSERPC=1`); NodeGuard does not use it
    today.
  - bitcoind has `txindex` enabled, so `getrawtransaction` works for any transaction without a block
    hash.
- **Cutoff.** Transactions before `OUTFLOW_MONITOR_CUTOFF_DATE` (the PR 1 deploy) are skipped, since
  they predate complete audit coverage.

### 4.2 Matching

Only the spending side is evaluated (a wallet-to-wallet transfer is checked once, on the source
wallet). A transaction matches if one of these exists:

- a `WalletWithdrawalRequest` with the same `TxId` and `WalletId` (including fee-bump replacements),
- a `ChannelOperationRequest` of type Open with the same `TxId` and `WalletId`.

The outputs must also agree with the request: same destinations and amounts for withdrawals, same
funding amount for channel opens.

### 4.3 Grace period

`TxId` is persisted **after** the transaction is published:

- withdrawals: [`BitcoinService.cs`](../src/Services/BitcoinService.cs) broadcasts via NBXplorer, then
  sets `TxId` and updates the row;
- channel opens: LND publishes the funding tx and NodeGuard stores `TxId` on the `ChanPending` update
  in [`LightningService.cs`](../src/Services/LightningService.cs).

So NBXplorer may report the tx before the DB knows about it. An unmatched tx is re-checked after
`OUTFLOW_MONITOR_GRACE_PERIOD_SECONDS` (default 120) using an in-memory delay. If the app restarts
during the wait, the startup scan picks it up again.

Note: if the post-broadcast `Update` fails, the error is only logged and the tx stays without a
`TxId`. The monitor will report it as `UnmatchedOutflow`, which is the desired behaviour.

### 4.4 Unsolicited fee bumps

RBF done through NodeGuard (UI or gRPC `BumpWithdrawal` / `CancelWithdrawal`) creates a new
`WalletWithdrawalRequest` linked through `BumpingWalletWithdrawalRequestId` (`IsRbfCancellation` for
cancellations), with its own `TxId`, and the original request moves to `Bumped` / `Cancelled`. Those
replacements match like any other request (§4.2).

What the monitor watches for is an RBF done **without NodeGuard's intervention**: a transaction that
spends an input of a known request's transaction, but has a `TxId` no request knows about. The
original request keeps pointing at a tx that will never confirm, so NodeGuard loses track of it. The
monitor reports it as `UnsolicitedReplacement`, referencing the original request, instead of a plain
`UnmatchedOutflow`. It alerts like any other anomaly; the request is not updated with the new
`TxId`.

## 5. Justification via AuditLog (PR 1 + PR 2)

For a matched request, require an `AuditLog` entry with `ObjectId = request.Id` and an action of
`Create`, `Transfer`, `BumpFee`, or `Cancel` for RBF cancellations (`IsRbfCancellation`):

- **User path.** The entry's `UserId` equals the request's `UserRequestorId` / `UserId`, and the
  user exists. The user had the right role (`FinanceManager` for withdrawals, `NodeManager` for
  channels) **when the request was created**: PR 1 records the user's roles in the audit entry's
  details, and the monitor checks those, not the user's current roles. Otherwise removing a role
  later would raise false alerts on rescans.
- **API path.** The request has no user and a gRPC-originated audit entry identifies the `APIToken`.

Otherwise the outflow is `UnjustifiedOutflow`.

There is no exemption for old requests. `AuditLogCleanupJob` deletes entries after 180 days, so a
request broadcast more than 180 days after it was created is flagged. That is rare and worth a look,
and an exemption based on the request's creation date could be abused by backdating a forged row.

### 5.1 Audit gaps to close first (PR 1)

- [`NodeGuardService`](../src/Rpc/NodeGuardService.cs) writes no audit entries. Add `LogSystemAsync`
  with the API token id to `RequestWithdrawal`, `BumpWithdrawal` and `OpenChannel`.
  [`GRPCAuthInterceptor`](../src/Rpc/GRPCAuthInterceptor.cs) must expose the validated token to the
  handlers.
- [`TransferFundsModal.razor`](../src/Shared/TransferFundsModal.razor) audits transfers against
  `AuditObjectType.Wallet` with the wallet id. It must also record the withdrawal request id.
- Verify that every UI creation path writes an entry whose `ObjectId` is the request id: withdrawal
  create, fee bump, channel open create.
- Include the requesting user's roles in the audit entry's details (§5).

## 6. Signed records (PR 3)

The AuditLog check can be defeated by someone who writes both the request row and its audit row.
Signing request rows with a key that does not live in the database closes that gap.

The key is introduced in two steps:

1. **Now:** an HMAC key held in an environment variable (§6.2). Simple to ship, but anyone with
   access to the app host's environment can read the key.
2. **Later:** [AWS KMS](https://docs.aws.amazon.com/kms/latest/developerguide/overview.html) (Key
   Management Service), a managed service that holds the key in AWS hardware and performs the
   sign/verify operations itself, so the key never reaches NodeGuard and cannot be copied out
   (§6.6).

The signer is hidden behind an interface so the second step is a new implementation, not a
rewrite.

### 6.1 Signer abstraction

```csharp
public interface IRecordSigner
{
    string KeyId { get; }
    Task<byte[]> SignAsync(byte[] payload, CancellationToken ct);
    Task<bool> VerifyAsync(byte[] payload, byte[] signature, string keyId, CancellationToken ct);
}
```

- Async from the start so a KMS implementation needs no caller changes.
- Verification always goes through the signer (with KMS the key never leaves AWS).
- `KeyId` is stored with each signature so rows are verified by the key that signed them.

### 6.2 First implementation: `EnvHmacRecordSigner`

- HMAC-SHA256, `KeyId = env-hmac-v1`.
- Key read from an environment variable populated from a secrets manager, never from a committed
  `.env` file.
- **Fail closed:** if signing is enabled and the key is missing, the app refuses to start.
- Supports additional verify-only keys for rotation and for the later KMS migration.

### 6.3 Schema

One migration adding to `WalletWithdrawalRequest` and `ChannelOperationRequest`:

| Column | Content |
|---|---|
| `Signature` | HMAC of the request record (§6.4), computed at creation. |
| `SignatureKeyId` | Key that produced `Signature` (e.g. `env-hmac-v1`, later a KMS key). |
| `TxIdSignature` | HMAC of `(requestId, TxId)`, computed when `TxId` is stored after broadcast. |
| `TxIdSignatureKeyId` | Key that produced `TxIdSignature`. |

- **Two signatures** because the `TxId` does not exist at creation (§4.3). Without the second one,
  a valid request could be re-pointed at a different transaction by editing its `TxId`.
- **Separate key ids** because the key can change between creation and broadcast (e.g. a request
  created before the KMS switch and broadcast after it); each signature is verified with the key
  that produced it.

**Alternative considered: two columns.** When the `TxId` is saved, check `Signature` first, then sign
the whole record again including the `TxId`, replacing `Signature` and `SignatureKeyId`. That leaves
only two columns.

The cost: once replaced, you can no longer prove what the request looked like when it was created.
We keep four columns so that proof is not lost.

**Withdrawal template PSBTs.** The same migration adds `Signature` and `SignatureKeyId` to
`WalletWithdrawalRequestPSBT`, populated only on the template row. The request signature proves *who
asked for what*; the template signature proves *the exact transaction NodeGuard built for it*:
inputs, every output including change, and therefore the fee.

The `TxIdSignature` is still needed for withdrawals. For segwit and taproot inputs the template's
unsigned transaction id equals the final txid, but `Legacy` wallets exist and their txid changes
once signatures are added.

### 6.4 Payloads

Built deterministically (fixed field order, invariant number formatting, sorted destinations):

- **Record signature**, at creation: request id, wallet id, destinations and amounts (or channel
  amount and peer), user id or API token id, creation time.
  - **Withdraw-all-funds requests** sign the `WithdrawAllFunds` flag and the destination address,
    but not the amount: [`BitcoinService.cs`](../src/Services/BitcoinService.cs) overwrites the
    destination amount with the wallet balance when it builds the template. The template signature
    fixes the exact amount.
- **TxId signature**, where `TxId` is stored: `(requestId, TxId)`.
- **Template signature**, when `BitcoinService` builds a withdrawal's template PSBT and before it is
  persisted: `(requestId, unsigned transaction bytes)`. The unsigned transaction is already a fixed
  binary format, so no custom serialisation is needed.

Signing points are the same creation paths as §5.1, the two `TxId` assignment sites (§4.3), and
every place a withdrawal template is generated (including fee bumps).

### 6.5 Monitor integration

A matched request must verify its record and TxId signatures (and, for withdrawals, its template
signature), otherwise `UnjustifiedOutflow`.

**No exemptions for existing rows.** Exempting requests by creation date would let a forged,
backdated row skip the check. Instead, as part of the PR 3 deploy, a one-off command signs every
existing request, TxId and template. It is run manually once, not at every startup: a job that
signs unsigned rows on each boot would also sign rows an attacker inserted. After it runs, any
unsigned row is treated as invalid.

### 6.6 Later: KMS

- `KmsRecordSigner` using AWS KMS HMAC keys (`GenerateMac` / `VerifyMac`). Same payloads and
  columns, so no migration.
- Keep `env-hmac-v1` verify-only until old rows age out of the lookback window, or re-sign them in a
  one-off job after verifying with the env key.
- KMS stops the key from being stolen, not from being used. An attacker who takes over the NodeGuard
  server can't copy the key, but while they are there they can still ask KMS to sign fake requests.
  Once they lose access they can't sign anymore; with the env var key they could keep a copy and
  forge signatures forever.

## 7. Alerting

The monitor writes a log entry (`NODEGUARD_OUTFLOW_ALERT`) for each `UnmatchedOutflow` or
`UnjustifiedOutflow`, and a Grafana alert is set up on it. A second alert fires if the monitor
stops running.

## 8. Delivery plan

| PR | Content | Schema changes |
|---|---|---|
| 1 | Audit gaps (§5.1) | None |
| 2 | `WalletOutflowSubscriptorJob`, matching, AuditLog justification, alerting and heartbeat (§4, §5, §7) | None |
| 3 | `IRecordSigner`, `EnvHmacRecordSigner`, signing points, one-off signing of existing rows, monitor verification (§6), checks before signing (§11) | One migration |
| later | `KmsRecordSigner` (§6.6) | None |

## 9. Testing

**PR 1** — every creation path produces an audit entry with the correct object type, id,
user/token and the user's roles.

**PR 2**

- no matching request → `UnmatchedOutflow`
- `TxId` matches but outputs differ → `UnmatchedOutflow`
- no audit entry → `UnjustifiedOutflow`
- audit user differs from requestor → `UnjustifiedOutflow`
- API path with a token audit entry → justified
- fee-bump replacement and wallet-to-wallet transfer → justified
- request's tx replaced by a fee bump made outside NodeGuard → `UnsolicitedReplacement`
- unmatched after the grace period → alert; matched within it → no alert
- tx before the cutoff → skipped
- user lost their role after creating the request → still justified
- request created more than 180 days before broadcast → `UnjustifiedOutflow`
- outflow from an archived wallet, or a wallet created after startup → evaluated
- heartbeat is logged while the subscription is healthy and stops when it is not

**PR 3**

- payload serialisation is stable
- tampered amount, destination or `TxId` fails verification
- tampered template PSBT (inputs, outputs or change) fails verification
- unknown `KeyId` fails verification
- missing key with signing enabled stops startup
- withdraw-all-funds request verifies after the template overwrites its amount
- unsigned row after the one-off signing → `UnjustifiedOutflow`
- §11: tampered withdrawal template → internal wallet (local or remote signer) refuses to sign and
  nothing is broadcast
- §11: channel open with wrong funding amount or excessive fee rate → refused before the internal
  wallet signs

## 10. Alternatives considered

- **Persisted monitor state** (last NBXplorer event id, per-tx check status). Rejected for now:
  the lookback scan covers restarts, and re-alerting on unresolved anomalies is acceptable. Revisit
  if restart noise becomes a problem.
- **Polling job instead of a subscription.** Rejected for latency; the startup scan already gives
  the robustness of polling.
- **Trusting `UserRequestorId` alone.** Rejected: a direct DB write can fake it.
- **Asymmetric signatures.** Deferred: would let an independent watcher verify with only a public
  key, but adds complexity not needed yet.

## 11. Suggested addition: verify PSBT outputs before signing

The monitor (§4–§6) detects a tampered PSBT only **after** broadcast. Example: a bad actor edits the
outputs of a withdrawal's template PSBT directly in the database, before anyone signs it. (After a
signature exists, SIGHASH_ALL makes any output change invalidate it.) The request row and its signature
are untouched, so they still verify. Approvals are validated against the stored template PSBT
([`BitcoinService.cs`](../src/Services/BitcoinService.cs)), but nothing checks the template's outputs
against the request's destinations. The tampered PSBT is shown to the human signers (caught only if
they check the address on their hardware device), co-signed by the internal wallet, and broadcast.
The monitor then alerts, but the funds are already gone.

**Channel opens are more exposed.** Human signers approve channel-open PSBTs with SIGHASH_NONE
([`ChannelRequests.razor`](../src/Pages/ChannelRequests.razor),
[`ChannelOperationRequestPSBTRepository.cs`](../src/Data/Repositories/ChannelOperationRequestPSBTRepository.cs)),
so their signatures do not cover the outputs. Only the internal wallet signs last with SIGHASH_ALL
([`LightningService.cs`](../src/Services/LightningService.cs)). Outputs changed after the humans
approve keep their signatures valid, and the internal wallet commits to whatever outputs are there.
The window for tampering runs until the internal wallet signs, not just until the first human
signature.

Because PR 3 already provides the signer and the signatures, the checks can run **before** funds
move, at almost no extra cost. If any check fails, NodeGuard refuses to sign/broadcast and emits the
same `NODEGUARD_OUTFLOW_ALERT`.

The internal wallet can sign locally or through the remote signer
([`RemoteSignerService`](../src/Services/RemoteSignerService.cs), an AWS Lambda). The checks run
before either one is called.

**Withdrawals** — in `BitcoinService`, before the internal wallet co-signs and before broadcast:

1. Verify the request's record signature and the template's signature (§6.4).
2. Check that the template's outputs match the request's destinations and amounts.

Approvals are already validated against the template
([`PsbtApprovalValidator`](../src/Helpers/PsbtApprovalValidator.cs)), so a verified template covers
the whole chain: inputs, destinations, change and fee. Editing any of them, including shrinking the
change to inflate the fee, breaks the template signature.

**Channel opens** — in `LightningService`, right before the internal wallet's SIGHASH_ALL signature:

1. Verify the request's record signature.
2. Check that the funding output amount matches the request.

The final funding transaction is assembled during the open from the output LND returns at the
`PsbtFund` step, so there is no stored template that covers it. The change is not covered by any
signature here: a bad actor could shrink it and inflate the fee. Rejecting a fee rate far above the
request's fee rate closes that gap.

This turns detection into prevention for these attacks and fits naturally in PR 3.

## 12. Next steps

- **Scan every transaction in mined blocks.** Detection (§4.1) trusts NBXplorer to report each
  wallet's outgoing transactions via `GetTransactionsAsync`. Instead of trusting it, scan every
  transaction in each new block from bitcoind and check whether it spends an input of a NodeGuard
  wallet. Not required for the initial release.
