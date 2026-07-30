ci_settings(readiness_timeout = '10m')

docker_compose([
  "./docker-compose.yml",
], profiles = ["polar", "loop", "mempool", "40swap", "e2e"])

# Labels are used to group containers on the UI.
labels = {
  'nodeguard': [
    'postgres',
    'nbxplorer',
  ],

  'loop': [
    'loopserver',
    'loopd-bob',
    'loopd-carol',
  ],

  'bitcoin': [
    'bitcoind',
    'alice',
    'bob', 
    'carol',
    'setup',
  ],

  'mempool': [
    'mempool-frontend-btc',
    'mempool-backend-btc',
    'mempool-db-btc',
    'electrumx',
  ],

  '40swap': [
    '40swap-lnd-setup',
    '40swap-postgres-backend',
    '40swapd-postgres-bob',
    '40swapd-postgres-carol',
    '40swapd-bob',
    '40swapd-carol',
    '40swap-backend',
  ],

  'grafana': [
    'grafana',
  ],

  # Option-B end-to-end stack (live NodeGuard + test runner). Disabled by default — manual
  # trigger only, consistent with NodeGuard itself not being Tilt-managed. Run via `just test-e2e`.
  'e2e': [
    'setup-e2e',
    'extract-env',
    'nodeguard',
    'e2e-runner',
  ],
}

for (label, services) in labels.items():
  for s in services:
    # Mempool, 40swap, grafana and e2e services are included but stopped by default (auto_init=False)
    if label == 'mempool':
      dc_resource(s, auto_init=False, labels = [label])
    elif label == '40swap':
      dc_resource(s, auto_init=False, labels = [label])
    elif label == 'grafana':
      dc_resource(s, auto_init=False, labels = [label])
    elif label == 'e2e':
      dc_resource(s, auto_init=False, labels = [label])
    else:
      dc_resource(s, labels = [label])