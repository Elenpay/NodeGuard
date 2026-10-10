# Local Spark network

A regtest Spark network on the Polar chain, for developing and testing NodeGuard's Spark swap provider. It is the `spark` compose profile.

It contains:
- three Spark operators, 2-of-3, with their Postgres;
- an open-source Spark Service Provider (SSP), [benthecarman/open-ssp](https://github.com/benthecarman/open-ssp);
- the SSP's Lightning node, an ldk-server.

The one-shot `spark-setup` service then does two things, funded from bitcoind's default wallet:
- funds the SSP with Spark liquidity;
- opens a channel from alice to the SSP's node, so NodeGuard's LND can pay Spark invoices.

The SSP also pays its cooperative exits from bitcoind's default wallet. A second loaded wallet would break the bitcoind clients that don't name their wallet, NodeGuard's dev funding at startup among them.

This stack is for development and CI only. The operator keys are open-ssp's public e2e fixtures, and the TLS material and admin token are throwaway regtest values. Never reuse any of them.

## Running it

```bash
just spark-up      # Polar + the Spark network
just spark-down    # removes the Spark containers, keeps Polar and the volumes
```

In Tilt, the services are in the `spark` group, which is disabled by default.

The E2E stack uses this network when the `spark` profile is up and `SPARK_ENABLED=true`. That configuration is in [docker/e2e/docker-compose.yml](../e2e/docker-compose.yml). NodeGuard then uses the embedded signer, with the Spark keys of its current internal wallet.

For a NodeGuard running on the host, `docker/extract-macaroons.sh` (run by `just run`; run it yourself before debugging from an IDE) writes these settings to `src/nodeguard-macaroons.env` whenever the Spark network is up, and copies the operators' certificates to `src/.spark-tls/`:

```
SPARK_ENABLED=true
SPARK_ACCOUNT=0
SPARK_OPERATORS=https://localhost:8535|0000000000000000000000000000000000000000000000000000000000000001|0322ca18fc489ae25418a0e768273c2c61cabb823edfb14feb891e9bec62016510;https://localhost:8536|0000000000000000000000000000000000000000000000000000000000000002|0341727a6c41b168f07eb50865ab8c397a53c7eef628ac1020956b705e43b6cb27;https://localhost:8537|0000000000000000000000000000000000000000000000000000000000000003|0305ab8d485cc752394de4981f8a5ae004f2becfea6f432c9a59d5022d8764f0a6
SPARK_SSP_URL=http://localhost:5100/graphql/spark/2025-03-19
SPARK_OPERATOR_CERTS_DIR=<repository>/src/.spark-tls
```

## Images

The images are built from open-ssp commit `89525792d65d7e0f7092e9f6d66b5082c00e48ca`, with its submodules, by the manual workflow [spark-dev-images.yml](../../.github/workflows/spark-dev-images.yml). The workflow publishes multi-arch images with build provenance:

| Image | Built from | License |
|---|---|---|
| `ghcr.io/elenpay/nodeguard-spark-dev-operator` | open-ssp `vendor/spark` (benthecarman's fork of [buildonspark/spark](https://github.com/buildonspark/spark)) | Apache-2.0 |
| `ghcr.io/elenpay/nodeguard-spark-dev-ldk-server` | open-ssp `vendor/ldk-server` ([lightningdevkit/ldk-server](https://github.com/lightningdevkit/ldk-server)) | MIT or Apache-2.0 |
| `ghcr.io/elenpay/nodeguard-spark-dev-ssp` | open-ssp `ssp.Dockerfile` | MIT |

[docker-compose.yml](docker-compose.yml) refers to them by the tag `open-ssp-<commit>`. Each can be overridden with `SPARK_OPERATOR_IMAGE`, `SPARK_LDK_SERVER_IMAGE` and `SPARK_SSP_IMAGE`.

To build the images locally instead, for example to try another open-ssp commit:

```bash
git clone --recurse-submodules https://github.com/benthecarman/open-ssp ~/open-ssp
cd ~/open-ssp && git checkout 89525792d65d7e0f7092e9f6d66b5082c00e48ca && git submodule update --init --recursive
SPARK_ADMIN_TOKEN=x SSP_IMAGE=open-ssp:regtest-local \
  docker compose -f docker-compose.regtest.yml build spark-operator-0 ldk-server ssp
docker tag open-ssp-spark-operator:regtest ghcr.io/elenpay/nodeguard-spark-dev-operator:open-ssp-89525792d65d
docker tag open-ssp-ldk-server:regtest ghcr.io/elenpay/nodeguard-spark-dev-ldk-server:open-ssp-89525792d65d
docker tag open-ssp:regtest-local ghcr.io/elenpay/nodeguard-spark-dev-ssp:open-ssp-89525792d65d
```

The configuration in [config/](config/) and [setup-spark.sh](setup-spark.sh) is adapted from open-ssp's e2e setup (MIT).
