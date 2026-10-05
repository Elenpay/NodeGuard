# Third-party notices

NodeGuard is licensed under the AGPL-3.0 (see [LICENSE](LICENSE)). It also builds in the source and
binaries listed below, which keep their own licenses. NuGet packages are not listed: each carries
its license in its package metadata.

## NSpark

- Path: [vendor/nspark](vendor/nspark/), a git submodule of the Elenpay fork
  <https://github.com/Elenpay/nspark> of <https://github.com/orklabs/nspark>.
- License: MIT, Copyright (c) 2026 OrkLabs S.A.S. and NSpark contributors. The full text is in
  `vendor/nspark/LICENSE`.
- NSpark's own dependencies are listed in `vendor/nspark/THIRD_PARTY_NOTICES.md`.

## spark-frost native library

- Path: `vendor/nspark/src/NSpark/runtimes/<rid>/native/`, copied to `runtimes/<rid>/native/` in
  NodeGuard's build output and image.
- Built from Lightspark's <https://github.com/buildonspark/spark> (crate
  `signer/spark-frost-uniffi`) at commit `0b3a32a05c9ac06cc411683551dd1f1bde9d0caa`. How it is built
  and verified is in `vendor/nspark/docs/native-build.md`, and the hashes are in
  `vendor/nspark/src/NSpark/runtimes/SHA256SUMS.txt`.
- License: Apache-2.0, <https://github.com/buildonspark/spark/blob/main/LICENSE>. Its Rust
  dependencies are predominantly MIT or Apache-2.0.
