#!/usr/bin/env bash
# Laddar (om) det syntetiska nätet i demomiljön som ett Job i klustret.
# Datagen-imagen tas från samma tagg som cmdb-api kör, så schemat alltid stämmer.
#
# Användning: scripts/load-demo-data.sh [--scale small|medium|full] [--seed N]
# Kräver kubectl med KUBECONFIG mot klustret. Se docs/drift.md.
set -euo pipefail

SCALE=full; SEED=1
while [[ $# -gt 0 ]]; do
  case "$1" in
    --scale) SCALE="$2"; shift ;;
    --seed)  SEED="$2"; shift ;;
    *) echo "Okänt argument: $1"; exit 1 ;;
  esac; shift
done

api_image=$(kubectl -n cmdb get deploy cmdb-api -o jsonpath='{.spec.template.spec.containers[0].image}')
tag=$(sed -E 's#^.*:(sha-[0-9a-f]+).*$#\1#' <<<"$api_image")
[[ "$tag" == sha-* ]] || { echo "Kunde inte läsa taggen ur $api_image" >&2; exit 1; }
image="registry.rosenvall.se/carnufex/cmdb-datagen:$tag"
job="cmdb-datagen-$(date +%Y%m%d%H%M%S)"
echo "Laddar $SCALE (seed $SEED) med $image som $job"

kubectl -n cmdb apply -f - <<EOF
apiVersion: batch/v1
kind: Job
metadata:
  name: $job
  namespace: cmdb
spec:
  backoffLimit: 0
  ttlSecondsAfterFinished: 86400
  template:
    metadata:
      labels:
        # Matched by the CiliumNetworkPolicy in the homelab that lets the job reach the database (#182).
        app.kubernetes.io/name: cmdb-datagen
    spec:
      restartPolicy: Never
      serviceAccountName: cmdb-runtime
      securityContext:
        runAsNonRoot: true
      containers:
        - name: datagen
          image: $image
          args: ["--scale", "$SCALE", "--seed", "$SEED", "--reset"]
          env:
            - name: ConnectionStrings__Cmdb
              valueFrom:
                secretKeyRef:
                  name: cmdb-secrets
                  key: ConnectionStrings__Cmdb
          securityContext:
            allowPrivilegeEscalation: false
            capabilities:
              drop: ["ALL"]
          resources:
            requests:
              cpu: 500m
              memory: 1Gi
            limits:
              memory: 2Gi
EOF

kubectl -n cmdb wait --for=condition=Ready "pod" -l "job-name=$job" --timeout=5m >/dev/null || true
kubectl -n cmdb logs -f "job/$job"
kubectl -n cmdb wait --for=condition=Complete "job/$job" --timeout=30s
# Restart so anything the API holds in memory (the graph engine, ADR-0002) is rebuilt from the new data.
kubectl -n cmdb rollout restart deploy/cmdb-api
kubectl -n cmdb rollout status deploy/cmdb-api --timeout=5m
