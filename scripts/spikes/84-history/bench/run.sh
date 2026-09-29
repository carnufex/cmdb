#!/bin/sh
# Runs each pgbench script against cmdb_spike and prints tps, p50 and p95 in ms. Inside the db container.
cd "$(dirname "$0")"
DB=${DB:-cmdb_spike}; T=${T:-15}; C=${C:-4}
psql -U cmdb -d "$DB" -qc "CREATE TABLE IF NOT EXISTS spike_operation (id bigserial PRIMARY KEY, at timestamptz NOT NULL DEFAULT now(), actor text NOT NULL, command jsonb NOT NULL)" \
  -c "CREATE TABLE IF NOT EXISTS spike_read_log (at timestamptz NOT NULL DEFAULT now(), actor text NOT NULL, surface text NOT NULL, request jsonb NOT NULL, scopes text[] NOT NULL, result_hash bytea NOT NULL, rows int NOT NULL) PARTITION BY RANGE (at)" \
  -c "CREATE TABLE IF NOT EXISTS spike_read_log_today PARTITION OF spike_read_log FOR VALUES FROM ('2020-01-01') TO ('2100-01-01')"
printf '%-14s %8s %8s %8s\n' script tps p50_ms p95_ms
for s in ${SCRIPTS:-$(ls *.sql)}; do
  rm -f /tmp/pgbench_log.*
  out=$(pgbench -U cmdb -d "$DB" -n -c "$C" -j "$C" -T "$T" -f "$s" --log --log-prefix=/tmp/pgbench_log 2>&1)
  tps=$(echo "$out" | sed -n 's/^tps = \([0-9.]*\).*/\1/p')
  pct=$(cat /tmp/pgbench_log.* | awk '{print $3}' | sort -n | awk '{a[NR]=$1} END {printf "%.2f %.2f", a[int(NR*0.5)]/1000, a[int(NR*0.95)]/1000}')
  printf '%-14s %8s %8s %8s\n' "${s%.sql}" "${tps%.*}" $pct
done
