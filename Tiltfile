ci_settings(readiness_timeout = '10m')

docker_compose([
  "./docker-compose.yml",
], profiles = ["polar", "loop", "mempool"])

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
}

for (label, services) in labels.items():
  for s in services:
    # Mempool services are included but stopped by default (auto_init=False)
    if label == 'mempool':
      dc_resource(s, auto_init=False, labels = [label])
    else:
      dc_resource(s, labels = [label])