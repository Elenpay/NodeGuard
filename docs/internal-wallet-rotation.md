# Internal Wallet Key Rotation Runbook

Procedure for rotating the NodeGuard **internal wallet** master seed as **proactive hygiene** (not incident response): the old key remains configured and signable indefinitely, funds drain gradually, and no channel is force-closed. For a compromise scenario, the same phases apply but compressed in time and finished by removing the old seed entirely.

> **Scope**: production deployments running `ENABLE_REMOTE_SIGNER=true`, where the internal wallet row stores only XPUB + master fingerprint and the seed lives in the SignPSBT Lambda (KMS-encrypted `MF_*` env vars). See the remote signer README (submodule at [remote-signer/](../remote-signer/)) for the Lambda-side procedures referenced here.

## How rotation works (read first)

- The "current" internal wallet is simply **the `InternalWallets` row with the highest `Id`** (`InternalWalletRepository.GetCurrentInternalWallet`, uncached). Inserting a new row flips "current" instantly — no deploy, no restart.
- The internal wallet is bound to a wallet **only at wallet creation** (`WalletRepository.AddAsync` copies `InternalWalletId`, `InternalWalletMasterFingerprint` and allocates the subderivation path). **Existing wallets never re-bind** and keep signing with their original key forever.
- Existing wallets **cannot be re-keyed in place**: swapping the internal xpub changes the `sortedmulti()` script, hence every address. Rotation therefore means: *new wallets on the new key + transfer funds + archive the old wallets*.
- The Lambda selects the seed **per PSBT input by master fingerprint**: env var `MF_<fingerprint>` → KMS-decrypt → sign along the PSBT's own derivation paths. Old and new seeds coexist; each wallet's PSBTs carry its own fingerprint.
- Never build a wallet containing **both** the old and new NodeGuard keys — the Lambda refuses PSBT inputs matching two configured fingerprints.

## Actors

| Actor | Responsibilities |
|---|---|
| Superadmin (NodeGuard) | Verifies internal wallet flip, audit trail |
| FinanceManager (NodeGuard) | Creates/finalises wallets, transfers funds, signs withdrawals, archives wallets |
| NodeManager (NodeGuard) | Re-points node funds destinations and liquidity rules |
| DBA / infra operator | Runs the SQL inventory + INSERT, takes the DB backup |
| AWS admin | KMS key/grants, Lambda env updates (`SignPSBT-stg` / `SignPSBT-prod`, eu-central-1) |
| Ceremony team (2 people) | Runs the seed ceremony CLI, custodies the paper/steel backup |

---

## Phase 0 — Preflight & inventory (read-only)

Access needed: psql (read), AWS creds with `lambda:GetFunctionConfiguration` + `kms:Encrypt` (+ `lambda:UpdateFunctionConfiguration`, `kms:CreateGrant` for later phases), NodeGuard Superadmin + FinanceManager accounts, a staging environment.

**0.1 Internal wallet history** — record the current row's `DerivationPath` **verbatim** (it is the ceremony tool input; the default is `m/48'/1'` but deployments can override `DEFAULT_DERIVATION_PATH`):

```sql
SELECT "Id", "DerivationPath", "MasterFingerprint",
       ("XPUB" IS NOT NULL)           AS has_xpub,
       ("MnemonicString" IS NOT NULL) AS has_mnemonic,
       "CreationDatetime"
FROM "InternalWallets"
ORDER BY "Id";
```

Gate: the highest-Id row has `has_xpub = t` and `has_mnemonic = f` (remote-signer mode).

**0.2 Wallet inventory** — mark in-scope wallets (finalised, not BIP39-imported, not watch-only, bound to the old internal wallet):

```sql
SELECT w."Id", w."Name", w."MofN", w."IsFinalised", w."IsArchived", w."IsHotWallet",
       w."IsBIP39Imported", (w."ImportedOutputDescriptor" IS NOT NULL) AS is_watch_only,
       w."IsCompromised", w."InternalWalletId", w."InternalWalletMasterFingerprint",
       w."InternalWalletSubDerivationPath"
FROM "Wallets" w
ORDER BY w."InternalWalletId", w."Id";
```

**0.3 Hardened-path gate (STOP condition)** — the next wallet's subderivation path is `Increment()` of the last finalised wallet's path. An XPUB-only internal wallet **cannot derive hardened children**, so the value below must contain **no apostrophe**:

```sql
SELECT "Id", "InternalWalletSubDerivationPath"
FROM "Wallets"
WHERE "IsFinalised" = true AND "IsBIP39Imported" = false
      AND "InternalWalletMasterFingerprint" IS NOT NULL
ORDER BY "Id" DESC
LIMIT 1;
```

If hardened (e.g. `1'`): STOP and escalate — wallet creation on the new internal wallet would fail.

**0.4 Liquidity rules inventory** (silent FKs — nothing warns when these point at retired wallets):

```sql
SELECT lr."Id" AS rule_id, lr."ChannelId", lr."NodeId", lr."IsReverseSwapWalletRule",
       lr."SwapWalletId",        sw."Name" AS swap_wallet,    sw."InternalWalletId" AS swap_iw,
       lr."ReverseSwapWalletId", rw."Name" AS reverse_wallet, rw."InternalWalletId" AS reverse_iw
FROM "LiquidityRules" lr
JOIN "Wallets" sw ON sw."Id" = lr."SwapWalletId"
LEFT JOIN "Wallets" rw ON rw."Id" = lr."ReverseSwapWalletId";
```

**0.5 Node funds destinations** — includes NULLs: `SweepNodeWalletsJob` silently auto-binds the **oldest available wallet** to a node without one (and throws if none is available):

```sql
SELECT n."Id", n."Name", n."FundsDestinationWalletId",
       w."Name" AS destination_wallet, w."InternalWalletId", w."IsArchived"
FROM "Nodes" n
LEFT JOIN "Wallets" w ON w."Id" = n."FundsDestinationWalletId";
```

**0.6 Channel close-address long tail** — channels that negotiated `upfront_shutdown_script` pay their close outputs to these (old-key) addresses **forever**; NodeGuard sends no override at close time. This population is why `MF_<old>` stays configured indefinitely:

```sql
SELECT c."Id", c."Status", c."BtcCloseAddress", c."CreationDatetime"
FROM "Channels" c
WHERE c."BtcCloseAddress" IS NOT NULL
ORDER BY c."Id";
```

**0.7 Lambda env budget** — total env vars are capped at 4 KB (~520 bytes per seed entry, so roughly 7 seeds max):

```bash
aws lambda get-function-configuration --function-name SignPSBT-prod \
  --region eu-central-1 --query 'Environment.Variables' \
  | tee lambda-env-prod-preflight.json | jq 'keys'
wc -c lambda-env-prod-preflight.json
```

**0.8 DB backup** — snapshot/pg_dump; record the restore point in the rotation log.

Rollback: nothing changed; abort freely.

---

## Phase 1 — Seed ceremony (ceremony team)

Rules: two people, hardened machine, and the **seed-ceremony CLI is the only software the mnemonic ever touches** — never a browser, editor, chat, shell argument, or the legacy "edit the unit test" flow.

```bash
cd remote-signer
just ceremony-generate <kms-key-id> mainnet manifest-prod.json
```

- `--derivation-path` must be the **verbatim** `DerivationPath` from step 0.1 if it differs from `m/48'/1'`.
- The tool shows the 24 words once (interactive terminal enforced), quizzes the backup, wipes the screen, KMS-encrypts (`kms:Encrypt` only) and writes a **manifest** containing public data only: `EnvName` (`MF_<newfp>`), `EnvValue` (the ciphertext JSON), `MasterFingerprint`, `AccountXpub`.
- Mnemonic custody: paper/steel, sealed, dual custody per treasury policy. The plaintext must not exist digitally after the ceremony.
- **Run the ceremony twice**: a throwaway seed for the staging rehearsal and the real production seed. Never configure the production seed on `SignPSBT-stg`.

Gate: `just ceremony-verify manifest-prod.json` is run later (Phase 2) by someone with `kms:Decrypt`; at this point check the manifest parses (`jq . manifest-prod.json`) and the fingerprint is 8 lowercase hex characters. Use it **verbatim** everywhere — env names are case-sensitive.

Rollback: destroy the material, re-run the ceremony.

---

## Phase 2 — KMS + Lambda configuration (AWS admin; staging first)

**2.1** If a new KMS key is used, grant the Lambda execution role `kms:Decrypt` on it:

```bash
FN=SignPSBT-stg      # then SignPSBT-prod
REGION=eu-central-1
ROLE_ARN=$(aws lambda get-function-configuration --function-name "$FN" \
  --region "$REGION" --query Role --output text)
aws kms create-grant --region "$REGION" --key-id "$NEW_KMS_KEY_ARN" \
  --grantee-principal "$ROLE_ARN" --operations Decrypt
```

**2.2** Add `MF_<newfp>` while **keeping every existing `MF_*` var**. Follow the **snapshot → jq merge → apply** procedure in the remote signer README ("Applying a new seed to the lambda") — `update-function-configuration --environment` **replaces the whole map**, so never apply anything but the merged file, and keep the snapshot as the rollback artifact.

**2.3** Verify: all old `MF_*` names still present plus the new one; `LastUpdateStatus: Successful`.

**2.4** Regression check: run any routine signing operation on an **existing** wallet (small hot-wallet withdrawal) — proves the old seeds were not clobbered. Run `just ceremony-verify` against the manifest as the new-seed preflight.

**2.5 Ordering**: Lambda config **before** the DB insert. An unused `MF_*` var is inert; the reverse order leaves a window where new-wallet signing fails.

**2.6** Rehearse Phases 3–5 end-to-end on staging with the throwaway seed before touching prod.

Rollback: re-apply the snapshot file (`{Variables: ...}`-wrapped) via `update-function-configuration`; retire the KMS grant. Zero NodeGuard impact.

---

## Phase 3 — DB insert + canary (DBA + Superadmin + FinanceManager)

**3.1** Announce a **wallet-creation freeze** (no new wallets, no channel opens) from the INSERT until the canary passes. Existing operations are unaffected.

**3.2** Insert the new internal wallet row — copies `DerivationPath` from the current row so it cannot drift; sets both required timestamps; `MnemonicString` stays NULL:

```bash
psql "$DATABASE_URL" \
  -v new_xpub='<AccountXpub from the manifest>' \
  -v new_mf='<MasterFingerprint from the manifest, 8 lowercase hex>' <<'SQL'
BEGIN;
INSERT INTO "InternalWallets"
    ("DerivationPath", "MnemonicString", "XPUB", "MasterFingerprint",
     "CreationDatetime", "UpdateDatetime")
SELECT "DerivationPath", NULL, :'new_xpub', :'new_mf', now(), now()
FROM "InternalWallets"
ORDER BY "Id" DESC
LIMIT 1
RETURNING "Id", "DerivationPath", "MasterFingerprint";
COMMIT;
SQL
```

Record the returned `Id` as **NEW_IW_ID** in the rotation log. Takes effect immediately.

> Alternative (call-out): inserting a bare row (`DerivationPath` + timestamps only) makes NodeGuard redirect every user to `/setup-internal-wallet`, where a Superadmin pastes XPUB + fingerprint with format validation and an audit-log entry. Trade-off: an app-wide soft-lock and a window where a malformed current wallet exists. The atomic INSERT above is the recommended path; if using the UI path, save both fields in one action and keep the window to minutes.

**3.3** Post-insert checks (before any UI action): re-run the 0.1 query — new row on top, `DerivationPath` identical to the previous row, fingerprint matches the Lambda env var suffix **exactly**. Confirm `/setup-internal-wallet` redirects to `/`.

**3.4 Canary** (FinanceManager):

1. Create a **hot single-sig** wallet named `rotation-canary-<date>` and finalise it (hot = NodeGuard signs alone → fastest full-path test).
2. Verify binding:
   ```sql
   SELECT "InternalWalletId", "InternalWalletMasterFingerprint", "InternalWalletSubDerivationPath"
   FROM "Wallets" WHERE "Name" = 'rotation-canary-<date>';
   ```
   Must show NEW_IW_ID + the new fingerprint; the subderivation path continues the global counter (expected — see Appendix A.4) and must be non-hardened.
3. Wallets page → "Export output descriptor" → the embedded xpub/fingerprint must match the manifest.
4. Deposit a small amount (50–100k sats), wait for confirmation ("Rescan wallet" if needed).
5. "Transfer funds to another wallet" (Transfer all funds) to any existing wallet — the withdrawal goes `PSBTSignaturesPending` and NodeGuard signs via the Lambda.

Gate: the withdrawal confirms on-chain. This proves DB row → wallet creation → NBXplorer tracking → PSBT fingerprint → `MF_<new>` lookup → KMS decrypt → valid signature, end to end. On failure, read the `SignPSBT-<env>` CloudWatch logs (distinct errors for missing env var, KMS decrypt denied, fingerprint mismatch, xpub mismatch).

**3.5** Lift the freeze. All new wallets now bind to the new key automatically.

Rollback: safe **only while nothing references the new row**:

```sql
SELECT (SELECT count(*) FROM "Wallets" WHERE "InternalWalletId" = :new_iw_id) AS wallets,
       (SELECT count(*) FROM "Keys"    WHERE "InternalWalletId" = :new_iw_id) AS keys;
-- both 0, then:
DELETE FROM "InternalWallets" WHERE "Id" = :new_iw_id;
```

Past the canary (finalised wallets can never be deleted), roll **forward** instead: insert a corrected higher-Id row and archive the bad canary. Never DELETE an `InternalWallets` row that any Wallet/Key references.

---

## Phase 4 — Recreate wallets + re-point references (FinanceManager + NodeManager)

**4.1** For each in-scope wallet from 0.2: create its replacement with the **same human keys** (reusable as-is — pick the same FinanceManager keys in the wallet modal) and same M-of-N — **except 2-of-2**, which NodeGuard now rejects; use 2-of-3 or higher. Finalise. Verify binding per wallet (3.4.2 query): new fingerprint, NEW_IW_ID.

**4.2** Re-point every liquidity rule from 0.4 whose swap/reverse-swap wallet is an old-key wallet to the replacement wallet.

**4.3** Re-point every node's **funds destination wallet** (Nodes page, or gRPC `AddNode.returning_funds_wallet_id`) to a new-key wallet, and set it explicitly where it was NULL. This drives `upfront_shutdown_script` for **future** channels, swap-out destinations and LND sweep destinations.

**4.4** Verify by re-running 0.4 and 0.5: zero rules/nodes referencing old-key wallets (minus documented exceptions).

**Ordering**: 4.2/4.3 MUST be complete and verified **before any archiving** (Phase 6) — archiving first leaves rules serving archived wallets silently and can make the sweep job pick a wrong wallet or throw.

Rollback: fully reversible — point everything back; old wallets still sign.

---

## Phase 5 — Drain old wallets (FinanceManager; UI-only)

The gRPC API cannot sweep (`RequestWithdrawal` has no withdraw-all) — draining happens in the UI, one wallet at a time.

Per wallet:

1. Book the M-1 human signers **before** creating the request — template PSBTs invalidate if the wallet's UTXO set changes (the request flips to `Failed`; harmless, just recreate it). Pause swaps/sweeps/channel ops touching the wallet.
2. Wallets page → "Transfer funds to another wallet" → source = old wallet, target = its replacement, check **Transfer all funds** (the target address is auto-generated — never hand-typed).
3. Hot wallet → `PSBTSignaturesPending`, signs and broadcasts automatically. Cold wallet → `Pending`; on the Withdrawals page: approve, collect the M-1 human signatures, NodeGuard co-signs last via `MF_<old>` (this is why the old env var stays).
4. Wait for confirmation; verify the old wallet shows zero and the replacement increased accordingly.

Gate: re-run 0.2 — every in-scope old wallet at zero confirmed balance.

Rollback: transfers are on-chain and irreversible but harmless — both endpoints are yours; "rollback" = transfer back.

---

## Phase 6 — Archive + long tail (indefinite)

**6.1** Archive each drained old wallet (Wallets page inline edit → `IsArchived`). Archived wallets disappear from availability queries but **withdrawals still work** — intentional, for the long tail.

**6.2** **Never untrack or delete old wallets.** NBXplorer keeps indexing them; channel-close outputs will land there for years and stay visible.

**6.3** Keep `MF_<old>` on the Lambda indefinitely. Once **all old hot/single-sig wallets are at zero**, set `"Compromised": true` on the `MF_<old>` entry (snapshot → merge → apply; jq one-liner in the remote signer README). The old seed then only co-signs true multisig inputs (threshold ≥ 2) — a compromised NodeGuard host or stolen AWS credentials can no longer drain legacy single-sig wallets through the Lambda, while multisig drains remain human-gated. If a channel close later lands on an old *single-sig* address, flip the flag off temporarily for that drain, then back on.

**6.4** Quarterly (and after any channel close): re-run 0.6 and check archived-wallet balances; drain any non-zero balance via Phase 5 (works on archived wallets).

**6.5** Rotation log: NEW_IW_ID, both fingerprints, dates, operators, canary txids, Lambda env snapshot filenames, backup restore point.

---

## Appendix A — Known gaps (documented, not fixed)

- **A.1 Stale co-signer key in the add-key modal**: opening "add key" on an OLD wallet offers the NEW internal key (`Wallets.razor` acknowledged TODO). Never add keys to old wallets post-rotation; doing so can also trigger A.2.
- **A.2 Mixed-fingerprint inputs brick signing**: the Lambda throws when a PSBT input matches two configured `MF_*` fingerprints. Never mix old + new NodeGuard keys in one wallet.
- **A.3 Silent stale FKs**: liquidity rules and node funds destinations keep serving archived/old wallets without warnings; the sweep job auto-binds the oldest available wallet when unset and throws when none exists.
- **A.4 Subderivation paths do not restart at 0** per fingerprint (`GetNextSubderivationPath` ignores the fingerprint despite the model comment's intent). Cosmetic — uniqueness is enforced on (path, fingerprint); new wallets simply continue the global counter.
- **A.5 Hardened subderivation paths are fatal in remote mode** — XPUB-only internal wallets can only derive non-hardened children. Preflight 0.3 is mandatory.
- **A.6 BIP39-imported / watch-only wallets** are out of scope (no internal co-signer). Note: imported BIP39 wallets under `ENABLE_REMOTE_SIGNER` appear to have no working signing path at all — a pre-existing issue, unrelated to rotation.
- **A.7 Old-key close addresses forever**: cooperative closes honour the `upfront_shutdown_script` negotiated at open; NodeGuard sends no `DeliveryAddress` at close time. Accepted under proactive rotation; mitigated by 6.4.
- **A.8 Derivation-path string sensitivity**: the code default is `48'/1'` (no `m/` prefix) and env-overridable; keys match by **string equality** on the path. The INSERT in 3.2 copies the old row's string structurally, and the ceremony tool must receive the same string.

## Appendix B — What NOT to do

1. Never put the old and new NodeGuard keys in one wallet (A.2).
2. Never untrack, delete or stop monitoring old wallets; never DELETE an `InternalWallets` row that anything references.
3. Never remove `MF_<old>` while any old wallet has a balance or any 0.6 channel is open.
4. Never call `update-function-configuration --environment` without the snapshot + merge pattern — it replaces the whole map.
5. Never use the legacy edit-a-unit-test encryption flow; the ceremony CLI is the only sanctioned path.
6. Never paste the mnemonic anywhere except the ceremony CLI; never pass secrets inline on command lines — files or hidden prompts only.
7. Never create wallets during the 3.1→3.5 freeze window.
8. Never archive old wallets before Phase 4 re-pointing is verified.
9. Never attempt to drain via gRPC — sweeping is UI-only.
10. Never re-case the fingerprint — 8 lowercase hex, verbatim from the manifest, everywhere.

## Appendix C — Quick reference

| Artifact | Where |
|---|---|
| Ceremony CLI | `remote-signer/RemoteSigner.SeedCeremony` (`just ceremony-generate/encrypt/verify`) |
| Lambda env merge procedure | remote-signer README → "Applying a new seed to the lambda" |
| Compromised flag semantics | remote-signer README → "Setting the function main config" |
| Inventory SQL | Phase 0 (0.1–0.6) |
| The rotation switch | Phase 3.2 INSERT (`RETURNING "Id"` → NEW_IW_ID) |
| Rollback safety check | Phase 3 rollback SQL (Wallets/Keys reference counts) |
| Lambda functions | `SignPSBT-stg` / `SignPSBT-prod`, eu-central-1 |
